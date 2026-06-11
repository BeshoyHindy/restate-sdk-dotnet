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
            fields.EagerState,
            fields.PartialState);

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

        _journal.RecordCommand(JournalEntryType.Input);
        inputMsg.Dispose();

        // Read remaining known entries: buffer commands into the replay queue, route notifications
        // into the CompletionManager by WIRE id (early-completion slots). known_entries counts BOTH
        // commands and notifications (protocol.proto:60-61) — Rust input.rs:79-148 PostReceiveEntry.
        for (var i = 1; i < (int)fields.KnownEntries; i++)
        {
            Log.ReadingMessage(Logger, InvocationId);
            var msg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                      ?? throw new ProtocolException("Stream ended while reading known entries");
            Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);

            if (msg.Header.Type.IsCommand())
                _journal.EnqueueReplay(ProtobufCodec.ParseReplayCommand(msg.Header.Type, msg.Payload));
            else if (msg.Header.Type.IsNotification())
                HandleIncomingMessage(msg);
            else
                throw new ProtocolException(
                    $"Unexpected {msg.Header.Type} inside the known-entries replay batch");

            msg.Dispose();
        }

        // Replay is in progress iff buffered commands remain — Rust !commands.is_empty().
        State = _journal.IsReplaying ? InvocationState.Replaying : InvocationState.Processing;
        if (State == InvocationState.Processing) Log.ReplayCompleted(Logger, InvocationId);

        return new StartInfo(
            fields.InvocationId,
            fields.Key,
            (int)fields.KnownEntries,
            fields.RandomSeed,
            input);
    }

    public async Task ProcessIncomingMessagesAsync(CancellationToken ct)
    {
        try
        {
            while (State != InvocationState.Closed)
            {
                Log.ReadingMessage(Logger, InvocationId);
                var message = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    Log.StreamEnded(Logger, InvocationId);
                    MarkInputClosed();
                    await TrySuspendAsync().ConfigureAwait(false);   // EOF-after-park trigger site
                    break;
                }

                var msg = message.Value;
                Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);
                HandleIncomingMessage(msg);
                msg.Dispose();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RST_STREAM / teardown: unpark any waiter so HandleAsync can unwind (defensive leak-stop).
            _completions.FailAll(new OperationCanceledException(ct));
            _signalCompletions.FailAll(new OperationCanceledException(ct));
            throw;
        }
        catch (Exception ex)
        {
            // Pump death (ProtocolException from the command-after-preflight guard, parse error,
            // reader IO failure) must fault every parked waiter — Rust parity: any do_transition
            // error moves the VM to a terminal error state observed by every subsequent poll. The
            // handler unwinds with this exception and HandleAsync's catch arms emit the ErrorMessage.
            _completions.FailAll(ex);
            _signalCompletions.FailAll(ex);
            throw;
        }
    }

    private void HandleIncomingMessage(RawMessage message)
    {
        var type = message.Header.Type;

        if (type == MessageType.EntryAck)
            return;

        // A command-typed message after preflight is a protocol violation (previously dropped silently).
        if (type.IsCommand())
            throw new ProtocolException($"Unexpected command {type} outside the replay batch");

        // Signal notifications (awakeable completions delivered via the signal mechanism)
        if (type == MessageType.SignalNotification)
        {
            if (!message.HasPayload)
                return;

            var signal = ProtobufCodec.ParseSignalNotification(message.Payload);
            if (signal.Idx is not null)
            {
                var signalIndex = (int)signal.Idx.Value;

                // Reserved built-in range (1..16) is not consumed as a user signal — CANCEL handling
                // and named-signal consumption remain out of scope; log-and-ignore (§5 divergence).
                if (signalIndex < FirstUserSignalId)
                {
                    Log.BuiltInSignalIgnored(Logger, InvocationId, signalIndex);
                    return;
                }

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
            else
            {
                Log.BuiltInSignalIgnored(Logger, InvocationId, -1);   // named signal — not yet consumed
            }

            return;
        }

        if (type.IsNotification())
        {
            if (!message.HasPayload)
                return;

            var notification = ProtobufCodec.ParseCompletionNotification(message.Payload);
            var completionId = (int)notification.CompletionId;
            Log.NotificationReceived(Logger, InvocationId, type, completionId, notification.IsFailure);

            // Invocation ID notification (field 16) — complete with the ID as UTF-8 bytes
            if (notification.InvocationId is not null)
            {
                _completions.TryComplete(completionId,
                    CompletionResult.SuccessString(notification.InvocationId));
                Log.CompletionReceived(Logger, InvocationId, completionId);
                return;
            }

            if (notification.IsFailure)
            {
                _completions.TryFail(completionId, notification.FailureCode!.Value, notification.FailureMessage!);
            }
            else
            {
                var result = notification.Value is not null
                    ? CompletionResult.Success(notification.Value.Value)
                    : CompletionResult.Success(ReadOnlyMemory<byte>.Empty);
                _completions.TryComplete(completionId, result);
            }

            Log.CompletionReceived(Logger, InvocationId, completionId);
        }
    }

    /// <summary>
    ///     Synchronous replay-buffer pop (the B3 fix — no wire access). Pops + validates one
    ///     buffered command and flips State to Processing when the queue drains. Caller MUST hold
    ///     _commandLock (id alloc, dequeue, and the State flip are one atomic section).
    /// </summary>
    private ReplayCommand DequeueReplayCommand(JournalEntryType expectedType, string? expectedName = null)
    {
        var command = _journal.DequeueReplay(expectedType, expectedName);
        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }

        return command;
    }

    /// <summary>Call/OneWayCall replay-pop with target-triple validation. Caller MUST hold _commandLock.</summary>
    private ReplayCommand DequeueReplayCommand(JournalEntryType expectedType, string? expectedName,
        string expectedService, string expectedHandler, string? expectedKey)
    {
        var command = _journal.DequeueReplay(expectedType, expectedName, expectedService, expectedHandler, expectedKey);
        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }

        return command;
    }

    /// <summary>
    ///     Non-determinism check: a replayed command's wire id must equal the locally re-allocated
    ///     one (counters advance identically across attempts). STRICT: every completable V4 command
    ///     carries an id &gt;= 1 — counters start at 1 precisely so 0 means field-unset
    ///     (context.rs:106-107) and Rust's header_eq compares the field unconditionally. This is
    ///     only called for completable commands, so replayed == 0 means a corrupted/foreign journal,
    ///     never a benign skip.
    /// </summary>
    private void ValidateReplayCompletionId(uint replayed, uint allocated)
    {
        if (replayed == 0)
            throw new ProtocolException(
                $"Corrupt journal at command index {_journal.CommandIndex}: " +
                "completable command missing its completion id");
        if (replayed != allocated)
            throw new ProtocolException(
                $"Non-deterministic replay at command index {_journal.CommandIndex}: " +
                $"journaled completion id {replayed}, locally allocated {allocated}");
    }
}
