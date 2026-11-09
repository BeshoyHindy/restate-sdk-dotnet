using System.Diagnostics.CodeAnalysis;
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
    /// <summary>
    ///     The first journal mismatch this attempt hit, or null. Recorded when the exception is
    ///     thrown, so the attempt still ends with the mismatch error even when handler code
    ///     swallows it in a bare <c>catch</c>.
    /// </summary>
    public JournalMismatchException? JournalMismatch { get; private set; }

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
                // A command with an empty payload (ClearAllStateCommand) rented no buffer.
                if (detachedBuf is not null)
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
            // The effective seed, which below V6 is derived from the invocation id rather than
            // read from the StartMessage.
            RandomSeed,
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
                        _namedSignals.Poison();
                    }

                    break;
                }

                var msg = message.Value;
                Log.MessageRead(Logger, InvocationId, msg.Header.Type, msg.Header.Length);
                HandleIncomingMessage(msg);
                msg.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // The reader was cancelled: either HandleAsync is unwinding after the outcome was
            // decided, or the request aborted mid-flight. Cancel every pending wait so a handler
            // parked on an un-cancellable `await tcs.Task` always unwinds instead of leaking.
            _completions.CancelAll();
            _signalCompletions.CancelAll();
            _namedSignals.CancelAll();
            throw;
        }
        catch
        {
            // The reader faulted (transport failure or malformed frame): stream integrity is
            // gone and no completion can ever arrive. Poison pending — and future — waits so
            // parked handlers unwind with SuspensionException and the invocation suspends.
            InputClosed = true;
            _completions.Poison();
            _signalCompletions.Poison();
            _namedSignals.Poison();
            throw;
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

                if (signalIndex < FirstUserSignalIndex)
                {
                    HandleBuiltInSignal(signalIndex);
                    return;
                }

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
            else if (signal.Name is not null)
            {
                // Named signals carry the name instead of an index, so they resolve through the
                // name-keyed manager. Replayed ones land here too and are stored as early results.
                if (signal.IsFailure)
                {
                    _namedSignals.TryFail(signal.Name, signal.FailureCode!.Value, signal.FailureMessage!);
                }
                else
                {
                    var result = signal.Value is not null
                        ? CompletionResult.Success(signal.Value.Value)
                        : CompletionResult.Success(ReadOnlyMemory<byte>.Empty);
                    _namedSignals.TryComplete(signal.Name, result);
                }
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
    ///     Handles a signal in the reserved built-in range (0-16). CANCEL asks this invocation
    ///     to stop: every pending durable wait — and every wait registered afterwards — fails
    ///     with a terminal 409 so parked handlers unwind and compensation logic
    ///     (try/finally, sagas) runs. The calls the invocation is blocked on are cancelled too,
    ///     before its next command (see <see cref="PropagateCancellationBefore" />). Other
    ///     built-ins carry no SDK semantics yet and are ignored; they must never resolve a user
    ///     awakeable.
    /// </summary>
    private void HandleBuiltInSignal(int signalIndex)
    {
        if (signalIndex != CancelSignalIndex)
            return;

        Log.InvocationCancelRequested(Logger, InvocationId);
        var cancellation = new TerminalException("The invocation was cancelled.", 409);
        // Set before the waits fail, so the handler they wake already sees it.
        _cancellation = cancellation;
        _completions.FailAllWith(cancellation);
        _signalCompletions.FailAllWith(cancellation);
        _namedSignals.FailAllWith(cancellation);
    }

    // ------- Cancelling child calls -------
    //
    // A cancelled invocation also cancels the request-response calls it is still waiting on, as
    // the official SDKs do; one-way sends are left running. The cancel signals are journaled
    // commands, so they are written on the handler's side, right before the first command the
    // handler writes after the CANCEL arrived (in practice its failed output), never from the
    // incoming-message loop where they would interleave with the handler's own commands.

    private void TrackChildCall(Task<CompletionResult> result, uint invocationIdIndex)
    {
        (_childCalls ??= []).Add((result, invocationIdIndex));
    }

    /// <summary>
    ///     True when the runtime delivered the call's outcome. A wait failed locally by the
    ///     cancellation, or poisoned when the input closed, does not count: the child is still
    ///     running.
    /// </summary>
    private bool HasCompleted(Task<CompletionResult> call)
    {
        if (call.IsCompletedSuccessfully)
            return true;

        return call.IsFaulted && call.Exception!.InnerException is TerminalException failure
                              && !ReferenceEquals(failure, _cancellation);
    }

    /// <summary>
    ///     Sends the CANCEL signal to every child call that has not completed, once, before the
    ///     first command written after this invocation was cancelled. A child whose invocation id
    ///     has not been delivered yet cannot be addressed and is skipped.
    /// </summary>
    private void PropagateCancellationBefore(MessageType type)
    {
        if (_cancellation is null || _cancelPropagated || !type.IsCommand() || State == InvocationState.Replaying)
            return;

        _cancelPropagated = true;
        if (_childCalls is null)
            return;

        foreach (var (result, invocationIdIndex) in _childCalls)
        {
            if (HasCompleted(result)
                || TryReadInvocationId(invocationIdIndex) is not { } childId
                || _cancelledChildren?.Contains(childId) == true)
                continue;

            WriteCommand(MessageType.SendSignalCommand, ProtobufCodec.CreateCancelInvocationCommand(childId));
            _journal.Append(JournalEntry.Completed(JournalEntryType.SendSignal, ReadOnlyMemory<byte>.Empty));
            Log.CancellingInvocation(Logger, InvocationId, childId);
        }
    }

    /// <summary>
    ///     Consumes the cancel signals an earlier attempt sent to its child calls, which follow
    ///     the command replay just matched. The handler never issues them itself, so leaving them
    ///     in place would fail its next operation with a journal mismatch. A cancel the handler
    ///     sent explicitly to a child that was already cancelled is left for that operation.
    /// </summary>
    private void SkipReplayedChildCancellations()
    {
        while (_cancellation is not null && _childCalls is not null && _journal.TryPeekReplayEntry(out var next)
               && next.CommandType == MessageType.SendSignalCommand)
        {
            var fields = ProtobufCodec.ParseCommandFields(next.CommandType, next.Result.Span);
            if (fields.SignalIndex != CancelSignalIndex || fields.Target is not { } target
                || _cancelledChildren?.Contains(target) == true || !IsChildCall(target))
                return;

            _ = _journal.TryTakeReplayEntry(out _);
            (_cancelledChildren ??= []).Add(target);
            _cancelPropagated = true;
        }
    }

    private bool IsChildCall(string invocationId)
    {
        foreach (var (_, invocationIdIndex) in _childCalls!)
        {
            if (TryReadInvocationId(invocationIdIndex) == invocationId)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Consumes the next replayed command entry (staged during the StartAsync drain), checks
    ///     it against the operation the handler is performing, and transitions to Processing when
    ///     the replay boundary is reached. Fails the attempt with a retryable journal-mismatch
    ///     error when the recorded command is of another kind, when it targets something else
    ///     (<paramref name="attempted" />), or when the replayed prefix ran out.
    /// </summary>
    /// <param name="expected">The kind of operation the handler is performing.</param>
    /// <param name="attempted">
    ///     The identifying fields of that operation, as the journal would have recorded them.
    ///     <see cref="CommandFields.None" /> for commands that have none.
    /// </param>
    private JournalEntry TakeReplayEntry(JournalEntryType expected, in CommandFields attempted)
    {
        var index = _journal.Count;

        if (!_journal.TryTakeReplayEntry(out var entry))
            ThrowJournalMismatch(index, expected, "no command at that index");

        if (entry.Type != expected)
            ThrowJournalMismatch(index, expected, $"{entry.Type} ({entry.CommandType})");

        var recorded = ProtobufCodec.ParseCommandFields(entry.CommandType, entry.Result.Span);
        if (CommandFields.FindDifference(entry.CommandType, recorded, attempted) is { } difference)
            ThrowFieldMismatch(index, expected, difference);

        if (entry.CommandType == MessageType.CallCommand)
        {
            // The same wait the replayed call registers, so its outcome is visible here.
            var completionId = (int)ProtobufCodec.ParseCommandCompletionId(entry.CommandType, entry.Result.Span);
            TrackChildCall(_completions.GetOrRegister(completionId).Task,
                ProtobufCodec.ParseCallInvocationIdIndex(entry.Result.Span));
        }

        SkipReplayedChildCancellations();

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
    private void AdvanceReplayIndex(JournalEntryType expected, in CommandFields attempted)
    {
        _ = TakeReplayEntry(expected, attempted);
    }

    /// <summary>
    ///     Records and throws a journal mismatch: the replayed journal does not match what the
    ///     handler is doing. Retryable rather than terminal — the usual cause is a deploy that
    ///     changed handler code under in-flight invocations, and redeploying the previous code
    ///     has to be able to recover them.
    ///     <paramref name="expected" /> is the operation this attempt's handler issues,
    ///     <paramref name="recorded" /> the command the previous execution journaled; the message
    ///     never swaps the two.
    /// </summary>
    [DoesNotReturn]
    private void ThrowJournalMismatch(int index, JournalEntryType expected, string recorded)
    {
        var mismatch = new JournalMismatchException(index,
            $"journal mismatch (RT0016) at journal index {index}: the handler expects {expected} " +
            $"but the previous execution recorded {recorded}. Either the handler code changed without " +
            "deploying a new deployment version, or the handler is non-deterministic.");

        JournalMismatch ??= mismatch;
        throw mismatch;
    }

    /// <summary>
    ///     Records and throws a journal mismatch for a replayed command of the right kind that
    ///     targets something else — another service, another state key, another promise. Without
    ///     it the handler would be handed the recorded result of an operation it never performed.
    ///     The value the journal holds is labelled recorded and the one this attempt uses
    ///     attempted; the message never swaps the two.
    /// </summary>
    [DoesNotReturn]
    private void ThrowFieldMismatch(int index, JournalEntryType expected, in CommandFieldDifference difference)
    {
        var mismatch = new JournalMismatchException(index,
            $"journal mismatch (RT0016) at journal index {index}: the {expected} command differs on " +
            $"{difference.Field}: recorded {Display(difference.Recorded)}, attempted " +
            $"{Display(difference.Attempted)}. Either the handler code changed without deploying " +
            "a new deployment version, or the handler is non-deterministic.");

        JournalMismatch ??= mismatch;
        throw mismatch;

        static string Display(string? value) => string.IsNullOrEmpty(value) ? "<empty>" : $"'{value}'";
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
            MessageType.SendSignalCommand => JournalEntryType.SendSignal,
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