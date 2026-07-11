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
            fields.EagerState,
            fields.IsPartialState);

        // V7 scope fields — captured internally, no public API yet.
        Scope = fields.Scope;
        LimitKey = fields.LimitKey;
        IdempotencyKey = fields.IdempotencyKey;

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
        _nextCompletionId = (uint)_journal.Count;
        inputMsg.Dispose();

        // Drain the remaining known entries. known_entries counts commands AND notifications
        // (a resumed journal always contains notifications), but only commands advance the
        // handler's replay progress: commands are staged for in-order consumption while
        // notifications route into the completion manager as (early) results.
        var commandCount = 1; // The InputCommand consumed above is command 0.
        for (var i = 1; i < (int)fields.KnownEntries; i++)
        {
            Log.ReadingMessage(Logger, InvocationId);
            var msg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                      ?? throw new ProtocolException("Stream ended during replay");
            Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);

            if (msg.Header.Type.IsCommand())
            {
                var (detachedBuf, detachedMem) = msg.DetachPayload();
                _journal.TrackPooledBuffer(detachedBuf);
                _journal.StageReplay(JournalEntry.Replayed(
                    MapMessageTypeToEntry(msg.Header.Type), msg.Header.Type, detachedMem));
                commandCount++;

                // Keep the live counter ahead of every completion id consumed by the replayed
                // prefix so post-replay commands never collide with replayed ones. For calls the
                // result id is allocated after the invocation-id index, so it covers both.
                if (ProtobufCodec.CommandHasCompletionId(msg.Header.Type))
                {
                    var replayedId = ProtobufCodec.ParseCommandCompletionId(msg.Header.Type, detachedMem.Span);
                    if (replayedId >= _nextCompletionId)
                        _nextCompletionId = replayedId + 1;
                }
            }
            else if (msg.Header.Type.IsNotification())
            {
                HandleIncomingMessage(msg);
            }

            msg.Dispose();
        }

        // The replay boundary is the command count, not known_entries: notifications in the
        // replayed prefix must not keep the state machine in Replaying once every command
        // has been re-traversed.
        _journal.SetReplayBoundary(commandCount);

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

                // EOF: the runtime half-closed the request stream. Every buffered frame has
                // already been delivered (ReadMessageAsync only returns null once the pipe is
                // drained), so no completion can ever arrive again. Poison both completion
                // managers so pending — and future — durable waits unwind with
                // SuspensionException and the invocation suspends.
                if (State != InvocationState.Closed)
                {
                    Log.InputStreamClosed(Logger, InvocationId);
                    InputClosed = true;
                    _completions.Poison();
                    _signalCompletions.Poison();
                }

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

        // ProposeRunCompletionAck is only sent when the SDK sets the REQUIRES_ACK flag on
        // ProposeRunCompletion, which this SDK never does — ignore it defensively.
        if (type is MessageType.EntryAck or MessageType.ProposeRunCompletionAck)
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
    ///     Consumes the next replayed command entry (staged during the StartAsync drain) and
    ///     transitions to Processing when the replay boundary is reached.
    /// </summary>
    private JournalEntry TakeReplayEntry()
    {
        var entry = _journal.TakeReplayEntry();

        if (!_journal.IsReplaying)
        {
            State = InvocationState.Processing;
            Log.ReplayCompleted(Logger, InvocationId);
        }

        return entry;
    }

    /// <summary>
    ///     Skips over a replayed non-completable command (e.g. SetState) that produces no result.
    /// </summary>
    private void AdvanceReplayIndex()
    {
        _ = TakeReplayEntry();
    }

    /// <summary>
    ///     Registers for the result of a replayed completable command. The completion id is parsed
    ///     from the replayed command payload; the result itself was (or will be) delivered as a
    ///     notification — replayed notifications are already stored as early results, live ones
    ///     resolve the returned source when they arrive.
    /// </summary>
    private TaskCompletionSource<CompletionResult> RegisterReplayCompletion(in JournalEntry replay)
    {
        var completionId = (int)ProtobufCodec.ParseCommandCompletionId(replay.CommandType, replay.Result.Span);
        Log.AwaitingCompletion(Logger, InvocationId, completionId);
        return _completions.GetOrRegister(completionId);
    }

    /// <summary>
    ///     Awaits the result of a replayed completable command (see <see cref="RegisterReplayCompletion" />).
    /// </summary>
    private async ValueTask<CompletionResult> AwaitReplayCompletionAsync(JournalEntry replay)
    {
        var tcs = RegisterReplayCompletion(in replay);
        return await tcs.Task.ConfigureAwait(false);
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