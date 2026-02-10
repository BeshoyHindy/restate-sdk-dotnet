using System.Text;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.StateMachine;

internal readonly record struct StartInfo(
    string InvocationId,
    string? Key,
    int KnownEntries,
    ulong RandomSeed,
    ReadOnlyMemory<byte> Input);

internal sealed partial class InvocationStateMachine
{
    public async Task<StartInfo> StartAsync(CancellationToken ct)
    {
        if (State != InvocationState.WaitingStart)
            throw new InvalidOperationException($"Cannot start in state {State}");

        Log.ReadingMessage(Logger, "(pre-start)");
        var startMsg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                       ?? throw new ProtocolException("Stream ended before StartMessage");
        Log.MessageRead(Logger, "(pre-start)", startMsg.Header.Type, startMsg.Header.Length);

        if (startMsg.Header.Type != MessageType.Start)
            throw new ProtocolException($"Expected StartMessage, got {startMsg.Header.Type}");

        var fields = ProtobufCodec.ParseStartMessage(startMsg.Payload);
        startMsg.Dispose();

        Initialize(
            fields.InvocationId,
            fields.RawId,
            fields.Key ?? "",
            fields.RandomSeed,
            (int)fields.KnownEntries,
            fields.EagerState);

        // The InputCommand always follows StartMessage (regardless of known_entries).
        Log.ReadingMessage(Logger, InvocationId);
        var inputMsg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                       ?? throw new ProtocolException("Stream ended before InputCommand");
        Log.MessageRead(Logger, InvocationId, inputMsg.Header.Type, inputMsg.Header.Length);

        if (inputMsg.Header.Type != MessageType.InputCommand)
            throw new ProtocolException($"Expected InputCommand, got {inputMsg.Header.Type}");

        ReadOnlyMemory<byte> input;
        if (inputMsg.HasPayload)
        {
            var (parsedInput, parsedHeaders) = ProtobufCodec.ParseInputCommand(inputMsg.Payload);
            input = parsedInput;
            // Store raw headers — FrozenDictionary is built lazily on first access via Headers property.
            _rawHeaders = parsedHeaders;
        }
        else
        {
            input = ReadOnlyMemory<byte>.Empty;
        }

        _journal.Append(JournalEntry.Completed(JournalEntryType.Input, input));
        inputMsg.Dispose();

        // Read remaining known entries (replayed commands and notifications).
        // known_entries includes the InputCommand (entry 0), so we start from 1.
        for (var i = 1; i < (int)fields.KnownEntries; i++)
        {
            Log.ReadingMessage(Logger, InvocationId);
            var msg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                      ?? throw new ProtocolException("Stream ended during replay");
            Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);

            if (msg.Header.Type.IsCommand())
            {
                var entry = JournalEntry.Completed(
                    MapMessageTypeToEntry(msg.Header.Type),
                    msg.PayloadMemory.ToArray());
                _journal.Append(entry);
            }
            else if (msg.Header.Type.IsNotification())
            {
                HandleIncomingMessage(msg);
            }

            msg.Dispose();
        }

        // After reading all known entries, transition to Processing
        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }

        return new StartInfo(
            fields.InvocationId,
            fields.Key,
            (int)fields.KnownEntries,
            fields.RandomSeed,
            input);
    }

    public async Task ProcessIncomingMessagesAsync(CancellationToken ct)
    {
        while (State != InvocationState.Closed)
        {
            Log.ReadingMessage(Logger, InvocationId);
            var message = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
            if (message is null)
            {
                Log.StreamEnded(Logger, InvocationId);
                break;
            }

            var msg = message.Value;
            Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);
            HandleIncomingMessage(msg);
            msg.Dispose();
        }
    }

    private void HandleIncomingMessage(RawMessage message)
    {
        var type = message.Header.Type;

        if (type == MessageType.EntryAck)
            return;

        // Signal notifications (awakeable completions delivered via the signal mechanism)
        if (type == MessageType.SignalNotification)
        {
            if (!message.HasPayload)
                return;

            var signal = ProtobufCodec.ParseSignalNotification(message.Payload);
            if (signal.Idx is not null)
            {
                var signalIndex = (int)signal.Idx.Value;
                Log.NotificationReceived(Logger, InvocationId, MessageType.SignalNotification, signalIndex, signal.IsFailure);

                if (signal.IsFailure)
                {
                    _signalCompletions.TryFail(signalIndex, signal.FailureCode!.Value, signal.FailureMessage!);
                }
                else
                {
                    var result = signal.Value is not null
                        ? CompletionResult.Success(signal.Value.Value)
                        : CompletionResult.Success(ReadOnlyMemory<byte>.Empty);
                    _signalCompletions.TryComplete(signalIndex, result);
                }

                Log.CompletionReceived(Logger, InvocationId, signalIndex);
            }

            return;
        }

        if (type.IsNotification())
        {
            if (!message.HasPayload)
                return;

            var notification = ProtobufCodec.ParseCompletionNotification(message.Payload);
            var entryIndex = (int)notification.CompletionId;
            Log.NotificationReceived(Logger, InvocationId, type, entryIndex, notification.IsFailure);

            // Invocation ID notification (field 16) — complete with the ID as UTF-8 bytes
            if (notification.InvocationId is not null)
            {
                _completions.TryComplete(entryIndex,
                    CompletionResult.SuccessString(notification.InvocationId));
                Log.CompletionReceived(Logger, InvocationId, entryIndex);
                return;
            }

            if (notification.IsFailure)
            {
                _completions.TryFail(entryIndex, notification.FailureCode!.Value, notification.FailureMessage!);
            }
            else
            {
                var result = notification.Value is not null
                    ? CompletionResult.Success(notification.Value.Value)
                    : CompletionResult.Success(ReadOnlyMemory<byte>.Empty);
                _completions.TryComplete(entryIndex, result);
            }

            Log.CompletionReceived(Logger, InvocationId, entryIndex);
        }
    }

    /// <summary>
    ///     Reads messages until the next journal command is appended at the expected index.
    ///     Notifications (completions, acks) are handled inline but don't advance the journal —
    ///     the loop continues until a Command message arrives or the stream ends.
    ///     Cancellation token guards against indefinite waits.
    /// </summary>
    private async ValueTask<JournalEntry> ReplayNextEntryAsync(
        JournalEntryType expectedType, string? name, CancellationToken ct)
    {
        var index = _journal.Count;

        while (index >= _journal.Count)
        {
            var msg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                      ?? throw new ProtocolException("Stream ended during replay");

            if (msg.Header.Type.IsCommand())
            {
                var entry = JournalEntry.Completed(
                    MapMessageTypeToEntry(msg.Header.Type),
                    msg.PayloadMemory.ToArray(),
                    name);
                _journal.Append(entry);
            }
            else if (msg.Header.Type.IsNotification())
            {
                HandleIncomingMessage(msg);
            }

            msg.Dispose();
        }

        var replayEntry = _journal[index];

        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }

        return replayEntry;
    }

    private void AdvanceReplayIndex(JournalEntryType type)
    {
        _journal.Append(JournalEntry.Completed(type, ReadOnlyMemory<byte>.Empty));
        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }
    }

    private static JournalEntryType MapMessageTypeToEntry(MessageType type)
    {
        return type switch
        {
            MessageType.InputCommand => JournalEntryType.Input,
            MessageType.OutputCommand => JournalEntryType.Output,
            MessageType.GetLazyStateCommand or MessageType.GetEagerStateCommand => JournalEntryType.GetState,
            MessageType.SetStateCommand => JournalEntryType.SetState,
            MessageType.ClearStateCommand => JournalEntryType.ClearState,
            MessageType.ClearAllStateCommand => JournalEntryType.ClearAllState,
            MessageType.GetLazyStateKeysCommand or MessageType.GetEagerStateKeysCommand =>
                JournalEntryType.GetStateKeys,
            MessageType.SleepCommand => JournalEntryType.Sleep,
            MessageType.CallCommand => JournalEntryType.Call,
            MessageType.OneWayCallCommand => JournalEntryType.OneWayCall,
            MessageType.CompleteAwakeableCommand => JournalEntryType.CompleteAwakeable,
            MessageType.RunCommand => JournalEntryType.Run,
            MessageType.GetPromiseCommand => JournalEntryType.GetPromise,
            MessageType.PeekPromiseCommand => JournalEntryType.PeekPromise,
            MessageType.CompletePromiseCommand => JournalEntryType.CompletePromise,
            MessageType.AttachInvocationCommand => JournalEntryType.AttachInvocation,
            MessageType.GetInvocationOutputCommand => JournalEntryType.GetInvocationOutput,
            _ => throw new ProtocolException($"Unknown command type: {type}")
        };
    }
}