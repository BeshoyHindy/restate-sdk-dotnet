using System.Diagnostics;
using System.Text;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Internal.StateMachine;

internal sealed partial class InvocationStateMachine
{
    // ------- Shared replay helpers -------
    //
    // Replayed command payloads never contain the operation's result value — results arrive as
    // notifications keyed by the command's completion id. Every completable replay branch parses
    // that id from the replayed command and resolves through the completion manager, where the
    // replayed notification was already stored (or a live one will land).

    /// <summary>Replays a completable command whose result is required (Run, Call, Attach, GetPromise).</summary>
    private async ValueTask<T> ReplayResultAsync<T>(JournalEntryType expected)
    {
        var replay = TakeReplayEntry(expected);
        var completion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    /// <summary>Replays a one-way call; the result is the target invocation id notification.</summary>
    private async ValueTask<InvocationHandle> ReplaySendAsync()
    {
        var replay = TakeReplayEntry(JournalEntryType.OneWayCall);
        var completion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
        var invocationId = completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
        return new InvocationHandle(invocationId);
    }

    // ------- Run replay -------
    //
    // A replayed RunCommand does not guarantee a stored completion: the previous attempt may
    // have crashed after the runtime persisted the command but before it persisted the
    // ProposeRunCompletion. The runtime can never complete that id — only the SDK can, by
    // re-executing the closure and proposing its result again. Awaiting would suspend forever.

    /// <summary>
    ///     Takes the next replayed entry as a Run command. Returns <see langword="true" /> when
    ///     the replayed prefix stored a genuine completion for it; <see langword="false" /> when
    ///     the closure must be re-executed and its result proposed for <paramref name="completionId" />.
    /// </summary>
    private bool TryTakeReplayedRunResult(out TaskCompletionSource<CompletionResult> tcs, out uint completionId)
    {
        var replay = TakeReplayEntry(JournalEntryType.Run);
        completionId = ProtobufCodec.ParseCommandCompletionId(replay.CommandType, replay.Result.Span);
        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        tcs = _completions.GetOrRegister((int)completionId);
        return HasStoredCompletion(tcs.Task);
    }

    /// <summary>
    ///     True when the task settled with a genuine journaled outcome — a completion result or a
    ///     terminal failure. Pending and suspension-poisoned tasks mean no notification for this
    ///     id was (or ever will be) delivered by the runtime.
    /// </summary>
    private static bool HasStoredCompletion(Task<CompletionResult> task)
    {
        if (task.IsCompletedSuccessfully)
            return true;
        if (!task.IsFaulted)
            return false;

        foreach (var inner in task.Exception!.InnerExceptions)
        {
            if (inner is TerminalException)
                return true;
        }

        return false;
    }

    /// <summary>Awaits and deserializes a stored Run completion.</summary>
    private async ValueTask<T> AwaitRunResultAsync<T>(TaskCompletionSource<CompletionResult> tcs)
    {
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    /// <summary>
    ///     Re-executes a replayed Run closure whose proposal was lost and proposes the result for
    ///     the replayed completion id. Mirrors the live path, except the RunCommand itself is not
    ///     re-sent — it is already part of the journal.
    /// </summary>
    private async ValueTask<T> ReExecuteRunAsync<T>(string name, Func<Task<T>> action, uint completionId,
        TaskCompletionSource<CompletionResult> tcs, CancellationToken ct, RetryPolicy? retryPolicy = null)
    {
        Log.SideEffectReExecuting(Logger, name, InvocationId);

        var result = retryPolicy is not null
            ? await ExecuteWithRetryAsync(name, action, retryPolicy, ct, new ReplayedRun(completionId, tcs))
                .ConfigureAwait(false)
            : await action().ConfigureAwait(false);
        var serialized = Serialize(result);
        WriteRunProposal(completionId, serialized.Span);
        await FlushAsync(ct).ConfigureAwait(false);

        // Resolve the registered wait locally (a no-op if EOF poisoned it meanwhile) so
        // combinators observing this run's future see the result immediately, like the live path.
        tcs.TrySetResult(CompletionResult.Success(CopyToPooled(serialized)));
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return result;
    }

    /// <summary>Re-executes a replayed void Run closure (see <see cref="ReExecuteRunAsync{T}" />).</summary>
    private async ValueTask ReExecuteRunAsync(string name, Func<Task> action, uint completionId,
        TaskCompletionSource<CompletionResult> tcs, CancellationToken ct, RetryPolicy? retryPolicy = null)
    {
        Log.SideEffectReExecuting(Logger, name, InvocationId);

        if (retryPolicy is not null)
            await ExecuteWithRetryAsync(name, action, retryPolicy, ct, new ReplayedRun(completionId, tcs))
                .ConfigureAwait(false);
        else
            await action().ConfigureAwait(false);

        WriteRunProposal(completionId, ReadOnlySpan<byte>.Empty);
        await FlushAsync(ct).ConfigureAwait(false);

        tcs.TrySetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));
        Log.SideEffectExecuted(Logger, name, InvocationId);
    }

    /// <summary>Re-executes a replayed synchronous Run closure (see <see cref="ReExecuteRunAsync{T}" />).</summary>
    private async ValueTask<T> ReExecuteRunSyncAsync<T>(string name, Func<T> action, uint completionId,
        TaskCompletionSource<CompletionResult> tcs, CancellationToken ct)
    {
        Log.SideEffectReExecuting(Logger, name, InvocationId);

        var result = action();
        var serialized = Serialize(result);
        WriteRunProposal(completionId, serialized.Span);
        await FlushAsync(ct).ConfigureAwait(false);

        tcs.TrySetResult(CompletionResult.Success(CopyToPooled(serialized)));
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return result;
    }

    // ------- Side effects -------

    /// <summary>Runs a synchronous side effect without closure/Task overhead.</summary>
    public ValueTask<T> RunSync<T>(string name, Func<T> action, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            return TryTakeReplayedRunResult(out var replayTcs, out var replayId)
                ? AwaitRunResultAsync<T>(replayTcs)
                : ReExecuteRunSyncAsync(name, action, replayId, replayTcs, ct);
        }

        var activity = StartOperationActivity("restate.run");
        activity?.SetTag("restate.run.name", name);
        try
        {
            var result = action();
            var serialized = Serialize(result);

            var completionId = NextCompletionId();
            WriteRunCommand(name, completionId);
            WriteRunProposal(completionId, serialized.Span);

            var flushTask = FlushAsync(ct);
            var serializedCopy = CopyToPooled(serialized);
            if (flushTask.IsCompletedSuccessfully)
            {
                _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
                Log.SideEffectExecuted(Logger, name, InvocationId);
                return new ValueTask<T>(result);
            }

            var pending = RunSyncAwaitFlush(flushTask, result, serializedCopy, name, activity);

            // RunSync executes synchronously on the caller's execution context, so
            // StartActivity made this span Activity.Current *in the caller*. The span is
            // disposed by RunSyncAwaitFlush on a forked context, where the automatic
            // Current restore is invisible to the caller — restore it here so subsequent
            // operations don't parent under a stopped "restate.run" span.
            if (activity is not null && ReferenceEquals(Activity.Current, activity))
                Activity.Current = activity.Parent;

            activity = null; // Ownership transferred to RunSyncAwaitFlush.
            return pending;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async ValueTask<T> RunSyncAwaitFlush<T>(ValueTask flushTask, T result, ReadOnlyMemory<byte> serializedCopy, string name, Activity? activity)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
            _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
            Log.SideEffectExecuted(Logger, name, InvocationId);
            return result;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    public async ValueTask<T> RunAsync<T>(string name, Func<Task<T>> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            return TryTakeReplayedRunResult(out var replayTcs, out var replayId)
                ? await AwaitRunResultAsync<T>(replayTcs).ConfigureAwait(false)
                : await ReExecuteRunAsync(name, action, replayId, replayTcs, ct, retryPolicy).ConfigureAwait(false);
        }

        using var activity = StartOperationActivity("restate.run");
        activity?.SetTag("restate.run.name", name);

        T result;
        if (retryPolicy is not null)
            result = await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false);
        else
            result = await action().ConfigureAwait(false);

        var serialized = Serialize(result);

        var completionId = NextCompletionId();
        WriteRunCommand(name, completionId);
        WriteRunProposal(completionId, serialized.Span);

        await FlushAsync(ct).ConfigureAwait(false);

        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, CopyToPooled(serialized), name));
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return result;
    }

    public async ValueTask RunAsync(string name, Func<Task> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            if (TryTakeReplayedRunResult(out var replayTcs, out var replayId))
                _ = await replayTcs.Task.ConfigureAwait(false);
            else
                await ReExecuteRunAsync(name, action, replayId, replayTcs, ct, retryPolicy).ConfigureAwait(false);
            return;
        }

        using var activity = StartOperationActivity("restate.run");
        activity?.SetTag("restate.run.name", name);

        if (retryPolicy is not null)
            await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false);
        else
            await action().ConfigureAwait(false);

        var completionId = NextCompletionId();
        WriteRunCommand(name, completionId);
        WriteRunProposal(completionId, ReadOnlySpan<byte>.Empty);

        await FlushAsync(ct).ConfigureAwait(false);

        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, ReadOnlyMemory<byte>.Empty, name));
        Log.SideEffectExecuted(Logger, name, InvocationId);
    }

    // ------- Retry logic -------

    /// <summary>
    ///     The already-journaled RunCommand a re-executed closure belongs to: the completion id
    ///     parsed from the replayed command, and the wait replay registered for it.
    /// </summary>
    private readonly record struct ReplayedRun(uint CompletionId, TaskCompletionSource<CompletionResult> Wait);

    private async Task<T> ExecuteWithRetryAsync<T>(string name, Func<Task<T>> action, RetryPolicy policy,
        CancellationToken ct, ReplayedRun? replayed = null)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempt = 0;

        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (TerminalException)
            {
                throw; // Never retry terminal exceptions
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var elapsed = DateTimeOffset.UtcNow - startTime;
                if (!policy.ShouldRetry(attempt + 1, elapsed))
                    throw await ExhaustRetriesAsync(name, attempt + 1, ex, replayed, ct).ConfigureAwait(false);

                var delay = policy.GetDelay(attempt);
                Log.SideEffectRetrying(Logger, name, attempt + 1, delay, InvocationId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private async Task ExecuteWithRetryAsync(string name, Func<Task> action, RetryPolicy policy,
        CancellationToken ct, ReplayedRun? replayed = null)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempt = 0;

        while (true)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (TerminalException)
            {
                throw; // Never retry terminal exceptions
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var elapsed = DateTimeOffset.UtcNow - startTime;
                if (!policy.ShouldRetry(attempt + 1, elapsed))
                    throw await ExhaustRetriesAsync(name, attempt + 1, ex, replayed, ct).ConfigureAwait(false);

                var delay = policy.GetDelay(attempt);
                Log.SideEffectRetrying(Logger, name, attempt + 1, delay, InvocationId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>
    ///     Records a Run whose retries ran out as a terminal failure and returns the exception the
    ///     caller throws. On the live path the RunCommand has not been written yet, so a fresh
    ///     completion id, its command and a journal entry are added. On the re-execution path the
    ///     command is already journaled under the replayed id: only the proposal is written, and the
    ///     replayed wait is failed so anything observing that run's future sees the terminal error
    ///     instead of waiting for a completion the runtime can never deliver.
    /// </summary>
    private async Task<TerminalException> ExhaustRetriesAsync(string name, int attempts, Exception cause,
        ReplayedRun? replayed, CancellationToken ct)
    {
        var message = $"Run '{name}' failed after {attempts} attempt(s): {cause.Message}";
        var completionId = replayed?.CompletionId ?? NextCompletionId();

        if (replayed is null)
            WriteRunCommand(name, completionId);

        WriteCommand(MessageType.ProposeRunCompletion,
            ProtobufCodec.CreateRunProposalFailure(completionId, 500, message));
        await FlushAsync(ct).ConfigureAwait(false);

        var terminal = new TerminalException(message, 500);

        if (replayed is { } run)
        {
            // Mark the fault observed: the wait belongs to a replayed command that nothing else
            // has to await, and an unobserved faulted task raises UnobservedTaskException.
            if (run.Wait.TrySetException(terminal))
                _ = run.Wait.Task.Exception;
        }
        else
        {
            // Append journal entry so _journal.Count advances — subsequent operations
            // (e.g. saga compensations catching this TerminalException) use correct indices.
            _journal.Append(JournalEntry.Completed(JournalEntryType.Run, ReadOnlyMemory<byte>.Empty, name));
        }

        return terminal;
    }

    private void WriteRunCommand(string name, uint completionId)
    {
        var msg = ProtobufCodec.CreateRunCommand(name, completionId);
        WriteCommand(MessageType.RunCommand, msg);
    }

    private void WriteRunProposal(uint completionId, ReadOnlySpan<byte> serialized)
    {
        var msg = ProtobufCodec.CreateRunProposal(completionId, serialized);
        WriteCommand(MessageType.ProposeRunCompletion, msg);
    }

    // ------- Non-blocking Run (RunAsync) -------

    public async ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> RunFutureAsync<T>(
        string name, Func<Task<T>> action, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            if (TryTakeReplayedRunResult(out var replayTcs, out var replayId))
            {
                var replayCompletion = await replayTcs.Task.ConfigureAwait(false);
                replayCompletion.ThrowIfFailure();
                return (replayTcs, Deserialize<T>(replayCompletion.Value));
            }

            var reExecuted = await ReExecuteRunAsync(name, action, replayId, replayTcs, ct).ConfigureAwait(false);
            return (replayTcs, reExecuted);
        }

        var value = await action().ConfigureAwait(false);
        var serialized = Serialize(value);

        var completionId = NextCompletionId();
        WriteRunCommand(name, completionId);
        WriteRunProposal(completionId, serialized.Span);

        await FlushAsync(ct).ConfigureAwait(false);

        var serializedCopy = CopyToPooled(serialized);
        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
        Log.SideEffectExecuted(Logger, name, InvocationId);

        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Success(serializedCopy));
        return (tcs, value);
    }

    // ------- Calls and sends -------
    //
    // Every entry point below — typed, untyped, and future — writes the same command and differs
    // only in how the request was serialized and what it does with the result, so they all funnel
    // through BeginCallAsync / SendCoreAsync.

    /// <summary>
    ///     CallCommand includes invocation_id_notification_idx (field 10).
    ///     The invocation-id notification gets its own completion id, which the SDK ignores for
    ///     request/response calls. The idempotency key, scope and limit key are written only when
    ///     the options carry them.
    /// </summary>
    private void WriteCallCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        uint invocationIdNotificationIdx, uint completionId, in CallOptions options)
    {
        var msg = ProtobufCodec.CreateCallCommand(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx,
            options.IdempotencyKey, options.Scope, options.LimitKey);
        WriteCommand(MessageType.CallCommand, msg);
    }

    private void WriteSendCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        in SendOptions options, uint notificationIdx)
    {
        var invokeTime = options.Delay is { } delay && delay > TimeSpan.Zero
            ? (ulong)DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds()
            : 0UL;
        var msg = ProtobufCodec.CreateSendCommand(
            service, handler, key, requestBytes.Span, invokeTime, options.IdempotencyKey, notificationIdx,
            options.Scope, options.LimitKey);
        WriteCommand(MessageType.OneWayCallCommand, msg);
    }

    /// <summary>
    ///     Writes the CallCommand for a live call and returns the source that resolves with its
    ///     result, plus the completion id it is registered under. <paramref name="requestBytes" />
    ///     must already be serialized: the shared serialization buffer is only valid until the
    ///     next Serialize call, and this copies it into the command before the first await.
    /// </summary>
    private async ValueTask<(TaskCompletionSource<CompletionResult> Tcs, uint CompletionId)> BeginCallAsync(
        string service, string? key, string handler, ReadOnlyMemory<byte> requestBytes, CallOptions options,
        CancellationToken ct)
    {
        // Allocate the invocation-id slot. Request/response calls never await it, so no TCS is
        // registered: the live notification is stored as an early result by TryComplete, and a
        // pending registration would otherwise be poisoned on EOF and advertised in the
        // SuspensionMessage, triggering an immediate spurious resume of every parked call.
        var invocationIdNotificationIdx = NextCompletionId();

        var completionId = NextCompletionId();
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId,
            options);

        await FlushAsync(ct).ConfigureAwait(false);

        return (tcs, completionId);
    }

    /// <summary>Awaits a live call started by <see cref="BeginCallAsync" /> and deserializes its result.</summary>
    private async ValueTask<TResponse> CallCoreAsync<TResponse>(string service, string? key, string handler,
        ReadOnlyMemory<byte> requestBytes, CallOptions options, CancellationToken ct)
    {
        using var activity = StartCallActivity(service, handler);

        var (tcs, completionId) =
            await BeginCallAsync(service, key, handler, requestBytes, options, ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    /// <summary>Writes the OneWayCallCommand for a live send and awaits the target invocation id.</summary>
    private async ValueTask<InvocationHandle> SendCoreAsync(string service, string? key, string handler,
        ReadOnlyMemory<byte> requestBytes, SendOptions options, CancellationToken ct)
    {
        var invocationIdNotificationIdx = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.OneWayCall));
        var tcs = _completions.GetOrRegister((int)invocationIdNotificationIdx);

        WriteSendCommandMessage(service, handler, key, requestBytes, options, invocationIdNotificationIdx);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)invocationIdNotificationIdx);
        var completion = await tcs.Task.ConfigureAwait(false);
        var invocationId = completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
        return new InvocationHandle(invocationId);
    }

    /// <summary>Calls a handler, serializing the request as an untyped object.</summary>
    public ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, CallOptions options, CancellationToken ct)
    {
        EnsureActive();
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));

        if (State == InvocationState.Replaying)
            return ReplayResultAsync<TResponse>(JournalEntryType.Call);

        return CallCoreAsync<TResponse>(service, key, handler, SerializeObject(request), options, ct);
    }

    /// <summary>Calls a handler, serializing the request with its static type.</summary>
    public ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string service, string handler, TRequest request, string? key, CallOptions options, CancellationToken ct)
    {
        EnsureActive();
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));

        if (State == InvocationState.Replaying)
            return ReplayResultAsync<TResponse>(JournalEntryType.Call);

        return CallCoreAsync<TResponse>(service, key, handler, Serialize(request), options, ct);
    }

    /// <summary>Starts an opt-in child activity for an outgoing call, tagged with the RPC target.</summary>
    private Activity? StartCallActivity(string service, string handler)
    {
        var activity = StartOperationActivity("restate.call");
        if (activity is not null)
        {
            activity.SetTag("rpc.service", service);
            activity.SetTag("rpc.method", handler);
        }

        return activity;
    }

    /// <summary>Sends a one-way invocation, serializing the request as an untyped object.</summary>
    public ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        SendOptions options, CancellationToken ct)
    {
        EnsureActive();
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));

        if (State == InvocationState.Replaying)
            return ReplaySendAsync();

        return SendCoreAsync(service, key, handler, SerializeObject(request), options, ct);
    }

    /// <summary>Sends a one-way invocation, serializing the request with its static type.</summary>
    public ValueTask<InvocationHandle> SendAsync<TRequest>(
        string service, string handler, TRequest request, string? key, SendOptions options, CancellationToken ct)
    {
        EnsureActive();
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));

        if (State == InvocationState.Replaying)
            return ReplaySendAsync();

        return SendCoreAsync(service, key, handler, Serialize(request), options, ct);
    }

    // ------- Non-blocking Call (CallFuture) -------

    public ValueTask<TaskCompletionSource<CompletionResult>> CallFutureAsync(
        string service, string? key, string handler, object? request, CallOptions options, CancellationToken ct)
    {
        EnsureActive();
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.Call);
            return new ValueTask<TaskCompletionSource<CompletionResult>>(RegisterReplayCompletion(in replay));
        }

        return BeginCallFutureAsync(service, key, handler, SerializeObject(request), options, ct);
    }

    private async ValueTask<TaskCompletionSource<CompletionResult>> BeginCallFutureAsync(string service, string? key,
        string handler, ReadOnlyMemory<byte> requestBytes, CallOptions options, CancellationToken ct)
    {
        var (tcs, _) = await BeginCallAsync(service, key, handler, requestBytes, options, ct).ConfigureAwait(false);
        return tcs;
    }

    // ------- Non-blocking Sleep (Timer) -------

    public async ValueTask<TaskCompletionSource<CompletionResult>> SleepFutureAsync(TimeSpan duration,
        CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.Sleep);
            return RegisterReplayCompletion(in replay);
        }

        var wakeUpTime = (ulong)DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Sleep));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        return tcs;
    }

    // ------- Attach / GetInvocationOutput -------

    public async ValueTask<TResponse> AttachInvocationAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<TResponse>(JournalEntryType.AttachInvocation).ConfigureAwait(false);

        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.AttachInvocation));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.AttachInvocationCommand,
            ProtobufCodec.CreateAttachInvocationCommand(invocationId, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<TResponse?> GetInvocationOutputAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.GetInvocationOutput);
            var replayCompletion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            replayCompletion.ThrowIfFailure();
            return replayCompletion.Value.IsEmpty ? default : Deserialize<TResponse>(replayCompletion.Value);
        }

        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.GetInvocationOutput));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.GetInvocationOutputCommand,
            ProtobufCodec.CreateGetInvocationOutputCommand(invocationId, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        // Void/empty completion means not yet completed — return default
        if (completion.Value.IsEmpty) return default;
        return Deserialize<TResponse>(completion.Value);
    }

    // ------- State -------

    public async ValueTask<T?> GetStateAsync<T>(string key, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.GetState);
            if (replay.CommandType == MessageType.GetEagerStateCommand)
            {
                // Eager-state commands embed the result in the command payload itself.
                var eagerValue = ProtobufCodec.ParseEagerStateValue(replay.Result.Span);
                return eagerValue is { IsEmpty: false } value ? Deserialize<T>(value) : default;
            }

            var replayCompletion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            replayCompletion.ThrowIfFailure();
            return replayCompletion.Value.IsEmpty ? default : Deserialize<T>(replayCompletion.Value);
        }

        // Eager path: the value is locally known — present in the state map, or absent from a
        // complete map (absence is then definitive). The read is journaled as a
        // GetEagerStateCommand with the result embedded; like SetState it needs no flush.
        if (_initialState is not null && _initialState.TryGetValue(key, out var eager))
        {
            WriteCommand(MessageType.GetEagerStateCommand,
                ProtobufCodec.CreateGetEagerStateCommand(key, eager));
            _journal.Append(JournalEntry.Completed(JournalEntryType.GetState, ReadOnlyMemory<byte>.Empty, key));
            return eager.Length > 0 ? Deserialize<T>(eager) : default;
        }

        if (!_stateIsPartial)
        {
            WriteCommand(MessageType.GetEagerStateCommand,
                ProtobufCodec.CreateGetEagerStateCommand(key, null));
            _journal.Append(JournalEntry.Completed(JournalEntryType.GetState, ReadOnlyMemory<byte>.Empty, key));
            return default;
        }

        // Lazy path: the state map is partial and does not contain the key — only the
        // runtime knows whether it exists.
        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.GetState, key));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.GetLazyStateCommand, ProtobufCodec.CreateGetStateCommand(key, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return completion.Value.IsEmpty ? default : Deserialize<T>(completion.Value);
    }

    // State mutation commands write to the pipe buffer without flushing.
    // The next async operation (Call, Run, Sleep, etc.) will flush the buffer.
    // This is safe because state commands are small and Kestrel's pipe buffer is large.
    public void SetState<T>(string key, T value)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.SetState);
            return;
        }

        var serialized = Serialize(value);

        WriteCommand(MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand(key, serialized.Span));

        _journal.Append(JournalEntry.Completed(JournalEntryType.SetState, ReadOnlyMemory<byte>.Empty, key));

        _initialState ??= new Dictionary<string, ReadOnlyMemory<byte>>(4);
        _initialState[key] = CopyToPooled(serialized);
    }

    public void ClearState(string key)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.ClearState);
            return;
        }

        WriteCommand(MessageType.ClearStateCommand, ProtobufCodec.CreateClearStateCommand(key));

        _journal.Append(JournalEntry.Completed(JournalEntryType.ClearState, ReadOnlyMemory<byte>.Empty, key));

        _initialState?.Remove(key);
    }

    public void ClearAllState()
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.ClearAllState);
            return;
        }

        WriteCommand(MessageType.ClearAllStateCommand, ProtobufCodec.CreateClearAllStateCommand());
        _journal.Append(JournalEntry.Completed(JournalEntryType.ClearAllState, ReadOnlyMemory<byte>.Empty));

        // After clearing everything, the (empty) local view is definitively complete —
        // subsequent reads can resolve eagerly even if the StartMessage map was partial.
        _initialState?.Clear();
        _stateIsPartial = false;
    }

    public async ValueTask<string[]> GetStateKeysAsync(CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.GetStateKeys);
            if (replay.CommandType == MessageType.GetEagerStateKeysCommand)
            {
                // Eager-state-keys commands embed the result in the command payload itself.
                return ProtobufCodec.ParseEagerStateKeys(replay.Result.Span);
            }

            var replayCompletion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            replayCompletion.ThrowIfFailure();
            return Deserialize<string[]>(replayCompletion.Value) ?? [];
        }

        // Eager path: a complete local view knows the full key set. The read is journaled as a
        // GetEagerStateKeysCommand with the keys embedded; like SetState it needs no flush.
        if (!_stateIsPartial)
        {
            string[] localKeys;
            if (_initialState is { Count: > 0 })
            {
                localKeys = new string[_initialState.Count];
                _initialState.Keys.CopyTo(localKeys, 0);
            }
            else
            {
                localKeys = [];
            }

            WriteCommand(MessageType.GetEagerStateKeysCommand,
                ProtobufCodec.CreateGetEagerStateKeysCommand(localKeys));
            _journal.Append(JournalEntry.Completed(JournalEntryType.GetStateKeys, ReadOnlyMemory<byte>.Empty));
            return localKeys;
        }

        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.GetStateKeys));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.GetLazyStateKeysCommand, ProtobufCodec.CreateGetStateKeysCommand(completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<string[]>(completion.Value) ?? [];
    }

    // ------- Sleep -------

    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.Sleep);
            _ = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            return;
        }

        using var activity = StartOperationActivity("restate.sleep");
        activity?.SetTag("restate.sleep.duration_ms", duration.TotalMilliseconds);

        var wakeUpTime = (ulong)DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Sleep));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        await tcs.Task.ConfigureAwait(false);
    }

    // ------- Signals and awakeables -------
    //
    // Both wait for a SignalNotification (type 0xFBFF) and neither writes a command: awaiting is
    // a local registration. An unnamed signal is addressed by an index allocated from
    // FirstUserSignalIndex, which is the same allocator awakeables use — an awakeable is an
    // unnamed signal plus the opaque id that addresses it. A named signal is addressed by name,
    // so it needs no id and no index.

    /// <summary>
    ///     Registers a wait on the next unnamed signal index. The index is allocated from the
    ///     single user-signal counter shared with awakeables, so the two never collide.
    /// </summary>
    public (int Index, TaskCompletionSource<CompletionResult> Tcs) RegisterSignal()
    {
        EnsureActive();

        // Allocate the next signal index (separate from journal indices)
        var signalIndex = _nextSignalIndex++;
        return (signalIndex, _signalCompletions.GetOrRegister(signalIndex));
    }

    /// <summary>
    ///     Registers a wait on the named signal. Awaiting the same name twice returns the same
    ///     wait, and a notification replayed ahead of the handler resolves it immediately.
    /// </summary>
    public TaskCompletionSource<CompletionResult> RegisterSignal(string name)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(name);

        return _namedSignals.GetOrRegister(name);
    }

    /// <summary>
    ///     Creates an awakeable: an unnamed signal plus the id an external system uses to
    ///     resolve it. This is purely a local operation — no command is sent to the server.
    /// </summary>
    public (string Id, TaskCompletionSource<CompletionResult> Tcs) Awakeable()
    {
        var (signalIndex, tcs) = RegisterSignal();
        return (BuildAwakeableId(signalIndex), tcs);
    }

    /// <summary>
    ///     Builds an awakeable ID in the signal format:
    ///     "sign_1" + Base64UrlSafe(rawInvocationId + BigEndian32(signalIndex))
    ///     Uses System.Buffers.Text.Base64Url for single-allocation encoding (no intermediate strings).
    /// </summary>
    private string BuildAwakeableId(int signalIndex)
    {
        var rawId = RawInvocationId;
        var bufferLength = rawId.Length + 4;
        Span<byte> byteBuffer = bufferLength <= 256 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        rawId.CopyTo(byteBuffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(byteBuffer[rawId.Length..], (uint)signalIndex);

        Span<char> charBuffer = stackalloc char[6 + System.Buffers.Text.Base64Url.GetEncodedLength(bufferLength)];
        "sign_1".CopyTo(charBuffer);
        System.Buffers.Text.Base64Url.EncodeToChars(byteBuffer, charBuffer[6..], out _, out int charsWritten);
        return new string(charBuffer[..(6 + charsWritten)]);
    }

    public void ResolveAwakeable(string id, ReadOnlyMemory<byte> payload)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.CompleteAwakeable);
            return;
        }

        WriteCommand(MessageType.CompleteAwakeableCommand,
            ProtobufCodec.CreateCompleteAwakeableSuccess(id, payload.Span));

        _journal.Append(JournalEntry.Completed(JournalEntryType.CompleteAwakeable, ReadOnlyMemory<byte>.Empty));
    }

    public void RejectAwakeable(string id, string reason)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.CompleteAwakeable);
            return;
        }

        WriteCommand(MessageType.CompleteAwakeableCommand,
            ProtobufCodec.CreateCompleteAwakeableFailure(id, 500, reason));

        _journal.Append(JournalEntry.Completed(JournalEntryType.CompleteAwakeable, ReadOnlyMemory<byte>.Empty));
    }

    // ------- Promises -------

    public async ValueTask<T> GetPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<T>(JournalEntryType.GetPromise).ConfigureAwait(false);

        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.GetPromise, name));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.GetPromiseCommand, ProtobufCodec.CreateGetPromiseCommand(name, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    public async ValueTask<T?> PeekPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry(JournalEntryType.PeekPromise);
            var replayCompletion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            return replayCompletion.Value.IsEmpty ? default : Deserialize<T>(replayCompletion.Value);
        }

        var completionId = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.PeekPromise, name));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCommand(MessageType.PeekPromiseCommand, ProtobufCodec.CreatePeekPromiseCommand(name, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        return completion.Value.IsEmpty ? default : Deserialize<T>(completion.Value);
    }

    public void ResolvePromise<T>(string name, T payload)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.CompletePromise);
            return;
        }

        var serialized = Serialize(payload);
        var completionId = NextCompletionId();

        WriteCommand(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseSuccess(name, serialized.Span, completionId));

        _journal.Append(JournalEntry.Completed(JournalEntryType.CompletePromise, ReadOnlyMemory<byte>.Empty, name));
    }

    public void RejectPromise(string name, string reason)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.CompletePromise);
            return;
        }

        var completionId = NextCompletionId();

        WriteCommand(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseFailure(name, 500, reason, completionId));

        _journal.Append(JournalEntry.Completed(JournalEntryType.CompletePromise, ReadOnlyMemory<byte>.Empty, name));
    }

    // ------- Cancel invocation -------

    public async ValueTask CancelInvocationAsync(string targetInvocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.SendSignal);
            return;
        }

        var msg = ProtobufCodec.CreateCancelInvocationCommand(targetInvocationId);
        WriteCommand(MessageType.SendSignalCommand, msg);

        _journal.Append(JournalEntry.Completed(JournalEntryType.SendSignal, ReadOnlyMemory<byte>.Empty));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.CancellingInvocation(Logger, InvocationId, targetInvocationId);
    }

    // ------- Resolving signals on other invocations -------
    //
    // Both directions are journaled SendSignalCommands, so a replayed attempt re-traverses them
    // without sending the signal twice.

    /// <summary>Resolves a signal on another invocation with a serialized value.</summary>
    public ValueTask ResolveSignalAsync(string targetInvocationId, string? name, uint? index,
        ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.SendSignal);
            return ValueTask.CompletedTask;
        }

        return SendSignalAsync(
            ProtobufCodec.CreateResolveSignalCommand(targetInvocationId, name, index, value.Span), ct);
    }

    /// <summary>
    ///     Rejects a signal on another invocation: the awaiting handler sees a terminal failure
    ///     carrying <paramref name="reason" />.
    /// </summary>
    public ValueTask RejectSignalAsync(string targetInvocationId, string? name, uint? index, string reason,
        ushort code, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex(JournalEntryType.SendSignal);
            return ValueTask.CompletedTask;
        }

        return SendSignalAsync(
            ProtobufCodec.CreateRejectSignalCommand(targetInvocationId, name, index, code, reason), ct);
    }

    private async ValueTask SendSignalAsync(Gen.SendSignalCommandMessage msg, CancellationToken ct)
    {
        WriteCommand(MessageType.SendSignalCommand, msg);
        _journal.Append(JournalEntry.Completed(JournalEntryType.SendSignal, ReadOnlyMemory<byte>.Empty));
        await FlushAsync(ct).ConfigureAwait(false);
    }

    // ------- Output / Error -------

    /// <summary>
    ///     Completes the invocation. OutputCommand always sets the Value oneof, even for empty
    ///     content (void handlers).
    /// </summary>
    public async ValueTask CompleteAsync(ReadOnlyMemory<byte> output, CancellationToken ct)
    {
        EnsureActive();

        // Set Closed BEFORE flushing to prevent re-entry if FlushAsync throws.
        State = InvocationState.Closed;

        WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(output.Span));

        _writer.WriteHeaderOnly(MessageType.End);
        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a terminal failure as an OutputCommand with the failure oneof.
    ///     Restate treats this as non-retryable — the invocation fails permanently.
    /// </summary>
    public async ValueTask FailTerminalAsync(ushort code, string message, CancellationToken ct)
    {
        if (State is InvocationState.Closed or InvocationState.Suspended)
            return;

        State = InvocationState.Closed;

        WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputFailure(code, message));

        _writer.WriteHeaderOnly(MessageType.End);
        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a transient error as an ErrorMessage with the default
    ///     <see cref="Gen.ErrorBehavior.Retry" /> behavior — the invocation will be retried.
    /// </summary>
    public ValueTask FailAsync(ushort code, string message, CancellationToken ct)
    {
        return FailAsync(code, message, Gen.ErrorBehavior.Retry, ct);
    }

    /// <summary>
    ///     Sends an error as an ErrorMessage. <paramref name="behavior" /> (V7, field 9) tells the
    ///     runtime what to do with the failed invocation; <see cref="Gen.ErrorBehavior.Retry" /> is
    ///     wire value 0 (not serialized) and matches the semantics of every previous protocol
    ///     version, so it is always safe to send.
    /// </summary>
    public async ValueTask FailAsync(ushort code, string message, Gen.ErrorBehavior behavior, CancellationToken ct)
    {
        if (State is InvocationState.Closed or InvocationState.Suspended)
            return;

        State = InvocationState.Closed;

        WriteCommand(MessageType.Error, ProtobufCodec.CreateErrorMessage(code, message, behavior));

        _writer.WriteHeaderOnly(MessageType.End);
        await FlushAsync(ct).ConfigureAwait(false);
    }

    // ------- Suspension -------

    /// <summary>
    ///     Suspends the invocation: writes a SuspensionMessage listing every pending completion
    ///     id and signal index so the runtime can resume the invocation once one of them is
    ///     resolvable. The wire encoding is version-dependent: V5/V6 use the legacy
    ///     <c>waiting_completions</c>/<c>waiting_signals</c> lists, V7 wraps the same ids in an
    ///     <c>awaiting_on</c> Future (flat FIRST_COMPLETED leaf — conservative: a spurious resume
    ///     replays and re-suspends, but a wake-up is never missed). SuspensionMessage is terminal
    ///     on its own — no End frame follows. If nothing is pending, the protocol's "at least one
    ///     element" requirement cannot be met and the invocation fails retryably instead.
    /// </summary>
    public async ValueTask SuspendAsync(CancellationToken ct)
    {
        if (State is InvocationState.Closed or InvocationState.Suspended)
            return;

        var completionIds = _completions.CollectPendingIds();
        var signalIds = _signalCompletions.CollectPendingIds();
        var namedSignals = _namedSignals.CollectPendingIds();

        if (completionIds.Count == 0 && signalIds.Count == 0 && namedSignals.Count == 0)
        {
            await FailAsync(500,
                "Input stream closed but no durable operation is pending — nothing to suspend on",
                ct).ConfigureAwait(false);
            return;
        }

        // Set Suspended BEFORE flushing to prevent re-entry if FlushAsync throws.
        State = InvocationState.Suspended;

        Log.InvocationSuspended(Logger, InvocationId, completionIds.Count, signalIds.Count + namedSignals.Count);
        WriteCommand(MessageType.Suspension,
            ProtobufCodec.CreateSuspensionMessage(NegotiatedVersion, completionIds, signalIds, namedSignals));
        await FlushAsync(ct).ConfigureAwait(false);
    }
}
