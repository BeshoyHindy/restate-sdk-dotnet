using System.Text;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.StateMachine;

internal sealed partial class InvocationStateMachine
{
    // ------- Side effects (Run) -------
    //
    // Every blocking Run variant is Template D (1.7): a synchronous locked prefix allocates the
    // completion id and either journals the RunCommand (Processing) or dequeues+validates it
    // (Replaying); the closure executes iff this attempt is the live path or the claimed replay
    // frontier; the value/failure is resolved exclusively from the RunCompletionNotification (the
    // ACK BARRIER) via the park API — never by rethrowing the closure's TerminalException (B10b).

    /// <summary>Runs a synchronous side effect without closure/Task overhead. Delegates to the Run core.</summary>
    public ValueTask<T> RunSync<T>(string name, Func<T> action, CancellationToken ct) =>
        RunAsync(name, () => Task.FromResult(action()), ct);

    public async ValueTask<T> RunAsync<T>(string name, Func<Task<T>> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();
        var (completionId, executesLocally, replaying) = RunPrefix(name);
        if (!executesLocally && !replaying)
            await FlushGatedAsync(ct).ConfigureAwait(false);

        var (hasValue, value) = executesLocally
            ? await ExecuteAndProposeRunAsync(name, action, completionId, retryPolicy, ct).ConfigureAwait(false)
            : (false, default(T)!);

        // The notification await is the ACK BARRIER in both directions: success returns only after
        // the runtime acked durable storage; terminal failure surfaces HERE (ThrowIfFailure), never
        // by rethrowing the closure's exception.
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return hasValue ? value : Deserialize<T>(completion.Value);
    }

    public async ValueTask RunAsync(string name, Func<Task> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();
        var (completionId, executesLocally, replaying) = RunPrefix(name);
        if (!executesLocally && !replaying)
            await FlushGatedAsync(ct).ConfigureAwait(false);

        if (executesLocally)
            await ExecuteAndProposeRunVoidAsync(name, action, completionId, retryPolicy, ct).ConfigureAwait(false);

        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();   // B10b: failure re-raises here, AFTER durable storage
        Log.SideEffectExecuted(Logger, name, InvocationId);
    }

    /// <summary>
    ///     Template D locked prefix shared by every Run variant. Allocates the deterministic
    ///     completion id; in Processing journals the RunCommand; in Replaying dequeues+validates and
    ///     applies the frontier rules (1.7 cases 1/2/3). Returns the id, whether THIS attempt must
    ///     execute the closure locally (live path, or the claimed replay frontier), and whether the
    ///     SM was replaying (so the caller can skip the live flush).
    /// </summary>
    private (uint CompletionId, bool ExecutesLocally, bool Replaying) RunPrefix(string name)
    {
        uint completionId;
        bool replaying, stillReplaying = false;
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            completionId = NextCompletionId();
            replaying = State == InvocationState.Replaying;
            if (replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.Run, name);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
                stillReplaying = _journal.IsReplaying;   // false ⇔ this Run was the replay frontier
            }
            else
            {
                WriteRunCommand(name, completionId);
                _journal.RecordCommand(JournalEntryType.Run, name);
            }
        }

        if (replaying)
        {
            if (stillReplaying && !_completions.HasResultFor((int)completionId))
                throw new ProtocolException(
                    $"Uncompleted Run '{name}' during replay with later journaled commands — " +
                    "journal mutation (UncompletedDoProgressDuringReplay parity); refusing to " +
                    "re-execute a side effect mid-replay");   // 1.7 case 3

            // 1.7 case 2 (frontier resume): execute iff the atomic claim wins; otherwise case 1 —
            // a buffered/raced completion is consumed below without executing.
            var executesAtFrontier = !stillReplaying && _completions.TryClaimForExecution((int)completionId);
            return (completionId, executesAtFrontier, true);
        }

        return (completionId, true, false);   // live path always executes
    }

    /// <summary>
    ///     Executes the closure and proposes its outcome. NEVER rethrows TerminalException — the
    ///     failure travels through the proposal → notification → ThrowIfFailure path (1.7).
    /// </summary>
    private async Task<(bool HasValue, T Value)> ExecuteAndProposeRunAsync<T>(string name,
        Func<Task<T>> action, uint completionId, RetryPolicy? retryPolicy, CancellationToken ct)
    {
        lock (_commandLock) _executingRuns++;   // any_executing guard for suspension
        try
        {
            T value;
            try
            {
                value = retryPolicy is not null
                    ? await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false)
                    : await action().ConfigureAwait(false);
            }
            catch (TerminalException ex)
            {
                // Terminal failure (thrown directly or via retry exhaustion): propose FAILURE and
                // fall through — the caller's notification await re-raises after durability.
                lock (_commandLock)
                {
                    ThrowIfClosedLocked();
                    WriteCommand(MessageType.ProposeRunCompletion,
                        ProtobufCodec.CreateRunProposalFailure(completionId, ex.Code, ex.Message));
                }

                await FlushGatedAsync(ct).ConfigureAwait(false);
                return (false, default!);
            }

            // Success: serialize INSIDE the lock (shared _serializeBuffer), propose by captured id.
            lock (_commandLock)
            {
                ThrowIfClosedLocked();
                var serialized = Serialize(value);
                WriteRunProposal(completionId, serialized.Span);
            }

            await FlushGatedAsync(ct).ConfigureAwait(false);
            return (true, value);
        }
        finally
        {
            lock (_commandLock) _executingRuns--;
            // Run-epilogue trigger site: with the run no longer executing, a closed-input invocation
            // whose handler is parked on this run's notification (or any other id) can now suspend.
            await TrySuspendAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteAndProposeRunVoidAsync(string name, Func<Task> action, uint completionId,
        RetryPolicy? retryPolicy, CancellationToken ct)
    {
        lock (_commandLock) _executingRuns++;
        try
        {
            try
            {
                if (retryPolicy is not null)
                    await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false);
                else
                    await action().ConfigureAwait(false);
            }
            catch (TerminalException ex)
            {
                lock (_commandLock)
                {
                    ThrowIfClosedLocked();
                    WriteCommand(MessageType.ProposeRunCompletion,
                        ProtobufCodec.CreateRunProposalFailure(completionId, ex.Code, ex.Message));
                }

                await FlushGatedAsync(ct).ConfigureAwait(false);
                return;
            }

            lock (_commandLock)
            {
                ThrowIfClosedLocked();
                WriteRunProposal(completionId, ReadOnlySpan<byte>.Empty);
            }

            await FlushGatedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_commandLock) _executingRuns--;
            await TrySuspendAsync().ConfigureAwait(false);
        }
    }

    // ------- Retry logic -------
    //
    // On exhaustion these throw TerminalException; ExecuteAndProposeRun* catches it and proposes the
    // failure with the captured id (the command itself is already journaled by the prefix).

    private async Task<T> ExecuteWithRetryAsync<T>(string name, Func<Task<T>> action, RetryPolicy policy,
        CancellationToken ct)
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
                    throw new TerminalException(
                        $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}", 500);

                var delay = policy.GetDelay(attempt);
                Log.SideEffectRetrying(Logger, name, attempt + 1, delay, InvocationId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private async Task ExecuteWithRetryAsync(string name, Func<Task> action, RetryPolicy policy,
        CancellationToken ct)
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
                    throw new TerminalException(
                        $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}", 500);

                var delay = policy.GetDelay(attempt);
                Log.SideEffectRetrying(Logger, name, attempt + 1, delay, InvocationId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private void WriteRunCommand(string name, uint completionId) =>
        WriteCommand(MessageType.RunCommand, ProtobufCodec.CreateRunCommand(name, completionId));

    private void WriteRunProposal(uint completionId, ReadOnlySpan<byte> serialized) =>
        WriteCommand(MessageType.ProposeRunCompletion, ProtobufCodec.CreateRunProposal(completionId, serialized));

    // ------- Non-blocking Run (RunFuture) -------

    /// <summary>
    ///     Fully synchronous journaling prefix (so fan-out creation order == journal order even when
    ///     the closure runs detached); the closure executes on a detached task; the future ALWAYS
    ///     resolves from the notification (the ack barrier) via the returned resolve thunk.
    /// </summary>
    public Func<ValueTask<CompletionResult>> RunFutureAsync<T>(
        string name, Func<Task<T>> action, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, executesLocally, _) = RunPrefix(name);

        // Flush the prefix before user code proceeds (matches the blocking path); cheap.
        _ = FlushGatedAsync(ct);

        if (executesLocally)
        {
            // Detached execution with infrastructure-failure containment: a write failure (NOT a
            // TerminalException, which is proposed) must fault the slot so the future's awaiter
            // observes it and no task exception goes unobserved.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteAndProposeRunAsync(name, action, completionId, null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _completions.TryFail((int)completionId, 500, ex.Message);
                }
            }, ct);
        }

        return () => AwaitNotificationAsync(completionId, NotificationKind.Completion);
    }

    // ------- Calls -------

    private void WriteCallCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        uint invocationIdNotificationIdx, uint completionId)
    {
        var msg = ProtobufCodec.CreateCallCommand(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx);
        WriteCommand(MessageType.CallCommand, msg);
    }

    private void WriteCallCommandMessageWithOptions(string service, string handler, string? key,
        ReadOnlyMemory<byte> requestBytes, uint invocationIdNotificationIdx, uint completionId, string? idempotencyKey)
    {
        var msg = ProtobufCodec.CreateCallCommandWithOptions(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx, idempotencyKey);
        WriteCommand(MessageType.CallCommand, msg);
    }

    private void WriteSendCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        TimeSpan? delay, string? idempotencyKey, uint notificationIdx)
    {
        var invokeTime = delay.HasValue && delay.Value > TimeSpan.Zero
            ? (ulong)DateTimeOffset.UtcNow.Add(delay.Value).ToUnixTimeMilliseconds()
            : 0UL;
        var msg = ProtobufCodec.CreateSendCommand(
            service, handler, key, requestBytes.Span, invokeTime, idempotencyKey, notificationIdx);
        WriteCommand(MessageType.OneWayCallCommand, msg);
    }

    /// <summary>
    ///     Template C — Call (two ids: invocationIdNotificationIdx then resultCompletionId, sys_call
    ///     order vm/mod.rs:742-744). The dummy-journal-slot hack is gone; the request is serialized
    ///     inside _commandLock and the result is resolved from the notification via the park API.
    /// </summary>
    private async ValueTask<uint> CallPrefixAsync(string service, string? key, string handler,
        ReadOnlyMemory<byte> requestBytes, string? idempotencyKey, CancellationToken ct)
    {
        uint invocationIdCompletionId, resultCompletionId;
        bool replaying;
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            invocationIdCompletionId = NextCompletionId();   // FIRST
            resultCompletionId = NextCompletionId();         // SECOND
            replaying = State == InvocationState.Replaying;
            if (replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.Call, expectedName: null,
                    expectedService: service, expectedHandler: handler, expectedKey: key);
                ValidateReplayCompletionId(cmd.InvocationIdNotificationIdx, invocationIdCompletionId);
                ValidateReplayCompletionId(cmd.ResultCompletionId, resultCompletionId);
            }
            else if (idempotencyKey is not null)
            {
                WriteCallCommandMessageWithOptions(service, handler, key, requestBytes,
                    invocationIdCompletionId, resultCompletionId, idempotencyKey);
                _journal.RecordCommand(JournalEntryType.Call);
            }
            else
            {
                WriteCallCommandMessage(service, handler, key, requestBytes,
                    invocationIdCompletionId, resultCompletionId);
                _journal.RecordCommand(JournalEntryType.Call);
            }
        }

        if (!replaying) await FlushGatedAsync(ct).ConfigureAwait(false);
        return resultCompletionId;
    }

    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var resultCompletionId = await CallPrefixAsync(service, key, handler, requestBytes, null, ct)
            .ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
            .ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, string? idempotencyKey, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var resultCompletionId = await CallPrefixAsync(service, key, handler, requestBytes, idempotencyKey, ct)
            .ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
            .ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string service, string handler, TRequest request, string? key, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = CopyToPooled(Serialize(request));
        var resultCompletionId = await CallPrefixAsync(service, key, handler, requestBytes, null, ct)
            .ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
            .ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    // ------- Non-blocking Call (CallFuture) -------

    public Func<ValueTask<CompletionResult>> CallFutureAsync(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var resultIdTask = CallPrefixAsync(service, key, handler, requestBytes, null, ct);
        // The prefix journals synchronously inside CallPrefixAsync before its first await, so the
        // command order is fixed; we capture the result id once the (cheap) flush completes.
        return async () =>
        {
            var resultCompletionId = await resultIdTask.ConfigureAwait(false);
            return await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
                .ConfigureAwait(false);
        };
    }

    // ------- Sends -------

    public async ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        TimeSpan? delay, string? idempotencyKey, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var invocationIdCompletionId = SendPrefix(service, key, handler, requestBytes, delay, idempotencyKey);
        await FlushGatedAsync(ct).ConfigureAwait(false);
        // Fire-and-forget: the handle resolves the invocation id lazily through the park API, so an
        // unawaited handle never parks/suspends (Rust sys_send returns SendHandle immediately).
        return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));
    }

    public async ValueTask<InvocationHandle> SendAsync<TRequest>(
        string service, string handler, TRequest request, string? key, TimeSpan? delay, string? idempotencyKey,
        CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = CopyToPooled(Serialize(request));
        var invocationIdCompletionId = SendPrefix(service, key, handler, requestBytes, delay, idempotencyKey);
        await FlushGatedAsync(ct).ConfigureAwait(false);
        return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));
    }

    private uint SendPrefix(string service, string? key, string handler, ReadOnlyMemory<byte> requestBytes,
        TimeSpan? delay, string? idempotencyKey)
    {
        uint invocationIdCompletionId;
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            invocationIdCompletionId = NextCompletionId();
            if (State == InvocationState.Replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.OneWayCall, expectedName: null,
                    expectedService: service, expectedHandler: handler, expectedKey: key);
                ValidateReplayCompletionId(cmd.InvocationIdNotificationIdx, invocationIdCompletionId);
            }
            else
            {
                WriteSendCommandMessage(service, handler, key, requestBytes, delay, idempotencyKey,
                    invocationIdCompletionId);
                _journal.RecordCommand(JournalEntryType.OneWayCall);
            }
        }

        return invocationIdCompletionId;
    }

    private async Task<string> ResolveInvocationIdAsync(uint invocationIdCompletionId)
    {
        var completion = await AwaitNotificationAsync(invocationIdCompletionId, NotificationKind.Completion)
            .ConfigureAwait(false);
        completion.ThrowIfFailure();
        return completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
    }

    // ------- Sleep / Timer -------

    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SleepPrefix(duration, ct);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
    }

    public Func<ValueTask<CompletionResult>> SleepFutureAsync(TimeSpan duration, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SleepPrefix(duration, ct);
        return () => AwaitNotificationAsync(completionId, NotificationKind.Completion);
    }

    private uint SleepPrefix(TimeSpan duration, CancellationToken ct)
    {
        uint completionId;
        bool replaying;
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            completionId = NextCompletionId();
            replaying = State == InvocationState.Replaying;
            if (replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.Sleep);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
            }
            else
            {
                var wakeUpTime = (ulong)DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
                WriteCommand(MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId));
                _journal.RecordCommand(JournalEntryType.Sleep);
            }
        }

        if (!replaying) _ = FlushGatedAsync(ct);
        return completionId;
    }

    // ------- Attach / GetInvocationOutput -------

    public async ValueTask<TResponse> AttachInvocationAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SimpleCompletablePrefix(JournalEntryType.AttachInvocation,
            id => ProtobufCodec.CreateAttachInvocationCommand(invocationId, id), MessageType.AttachInvocationCommand, ct);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<TResponse?> GetInvocationOutputAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SimpleCompletablePrefix(JournalEntryType.GetInvocationOutput,
            id => ProtobufCodec.CreateGetInvocationOutputCommand(invocationId, id),
            MessageType.GetInvocationOutputCommand, ct);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        // Void/empty completion means not yet completed — return default
        return completion.Value.IsEmpty ? default : Deserialize<TResponse>(completion.Value);
    }

    /// <summary>
    ///     Template A locked prefix for completable ops whose only parameters are a key/id and the
    ///     completion id (Attach, GetInvocationOutput, GetPromise, PeekPromise). Allocates the id in
    ///     BOTH branches; replay dequeues+validates, processing writes+records+flushes.
    /// </summary>
    private uint SimpleCompletablePrefix(JournalEntryType type,
        Func<uint, Google.Protobuf.IMessage> create, MessageType wireType, CancellationToken ct, string? name = null)
    {
        uint completionId;
        bool replaying;
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            completionId = NextCompletionId();
            replaying = State == InvocationState.Replaying;
            if (replaying)
            {
                var cmd = DequeueReplayCommand(type, name);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
            }
            else
            {
                WriteCommand(wireType, create(completionId));
                _journal.RecordCommand(type, name);
            }
        }

        if (!replaying) _ = FlushGatedAsync(ct);
        return completionId;
    }

    // ------- State -------

    /// <summary>
    ///     ONE decode rule for state values, applied IDENTICALLY on every path — fresh eager hit,
    ///     replayed GetEagerStateCommand, lazy notification — so fresh and replayed runs can never
    ///     diverge on the same payload. Void/cleared (known-absent) → default. Empty Value payload →
    ///     default as well (documented §5 normalization, matching the fork's existing lazy behavior).
    /// </summary>
    private T? DeserializeStateValue<T>(bool isVoid, ReadOnlyMemory<byte> value) =>
        isVoid || value.IsEmpty ? default : Deserialize<T>(value);

    public async ValueTask<T?> GetStateAsync<T>(string key, CancellationToken ct)
    {
        EnsureActive();
        uint completionId;
        ReplayCommand? replayed = null;
        var wroteLazy = false;
        (bool IsVoid, ReadOnlyMemory<byte> Value)? eagerHit = null;

        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            completionId = NextCompletionId();   // SysStateGet allocates unconditionally (journal.rs:301)
            if (State == InvocationState.Replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.GetState, key);  // GetEagerState OR GetLazyState
                if (!cmd.HasEagerResult) ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
                replayed = cmd;
            }
            else if (_eagerState.TryGetValue(key, out var eager))   // Value or cleared-marker (null)
            {
                WriteCommand(MessageType.GetEagerStateCommand,
                    ProtobufCodec.CreateGetEagerStateCommand(key, eager));
                _journal.RecordCommand(JournalEntryType.GetState, key);
                eagerHit = (eager is null, eager ?? ReadOnlyMemory<byte>.Empty);
            }
            else if (!_eagerStateIsPartial)                          // complete map: absent == known-empty
            {
                WriteCommand(MessageType.GetEagerStateCommand,
                    ProtobufCodec.CreateGetEagerStateCommand(key, null));
                _journal.RecordCommand(JournalEntryType.GetState, key);
                eagerHit = (true, ReadOnlyMemory<byte>.Empty);
            }
            else                                                     // Unknown under partial → real roundtrip
            {
                WriteCommand(MessageType.GetLazyStateCommand,
                    ProtobufCodec.CreateGetStateCommand(key, completionId));
                _journal.RecordCommand(JournalEntryType.GetState, key);
                wroteLazy = true;
            }
        }

        if (replayed is { HasEagerResult: true } eagerCmd)
            return DeserializeStateValue<T>(eagerCmd.EagerIsVoid, eagerCmd.EagerValue);
        if (eagerHit is { } hit)
            return DeserializeStateValue<T>(hit.IsVoid, hit.Value);

        if (wroteLazy) await FlushGatedAsync(ct).ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return DeserializeStateValue<T>(isVoid: false, completion.Value);
    }

    public void SetState<T>(string key, T value)
    {
        EnsureActive();
        lock (_commandLock)   // sync ops take the SAME Monitor as async prefixes — no barging
        {
            ThrowIfClosedLocked();
            var serialized = Serialize(value);                 // shared buffer — inside the lock
            _eagerState[key] = CopyToPooled(serialized);       // unconditional, replay included
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.SetState, key);
            }
            else
            {
                WriteCommand(MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand(key, serialized.Span));
                _journal.RecordCommand(JournalEntryType.SetState, key);
            }
        }
    }

    public void ClearState(string key)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            _eagerState[key] = null;   // cleared marker — NOT Remove (EagerState::clear), replay included
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.ClearState, key);
            }
            else
            {
                WriteCommand(MessageType.ClearStateCommand, ProtobufCodec.CreateClearStateCommand(key));
                _journal.RecordCommand(JournalEntryType.ClearState, key);
            }
        }
    }

    public void ClearAllState()
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            _eagerState.Clear();
            _eagerStateIsPartial = false;   // EagerState::clear_all
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.ClearAllState);
            }
            else
            {
                WriteCommand(MessageType.ClearAllStateCommand, ProtobufCodec.CreateClearAllStateCommand());
                _journal.RecordCommand(JournalEntryType.ClearAllState);
            }
        }
    }

    public async ValueTask<string[]> GetStateKeysAsync(CancellationToken ct)
    {
        EnsureActive();
        uint completionId;
        ReplayCommand? replayed = null;
        string[]? eagerKeys = null;
        var wroteLazy = false;

        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            completionId = NextCompletionId();
            if (State == InvocationState.Replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.GetStateKeys);
                if (!cmd.HasEagerResult) ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
                replayed = cmd;
            }
            else if (!_eagerStateIsPartial)
            {
                eagerKeys = _eagerState.Where(p => p.Value is not null).Select(p => p.Key)
                    .Order(StringComparer.Ordinal).ToArray();
                WriteCommand(MessageType.GetEagerStateKeysCommand,
                    ProtobufCodec.CreateGetEagerStateKeysCommand(eagerKeys));
                _journal.RecordCommand(JournalEntryType.GetStateKeys);
            }
            else
            {
                WriteCommand(MessageType.GetLazyStateKeysCommand, ProtobufCodec.CreateGetStateKeysCommand(completionId));
                _journal.RecordCommand(JournalEntryType.GetStateKeys);
                wroteLazy = true;
            }
        }

        if (replayed is { HasEagerResult: true } eagerCmd)
            return Deserialize<string[]>(eagerCmd.EagerValue) ?? [];
        if (eagerKeys is not null)
            return eagerKeys;

        if (wroteLazy) await FlushGatedAsync(ct).ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<string[]>(completion.Value) ?? [];
    }

    // ------- Awakeable -------

    /// <summary>
    ///     Creates an awakeable. In V4 protocol this is a local operation — no command is sent; the
    ///     SDK registers a signal handle (id 17+) and waits for a SignalNotification (type 0xFBFF).
    /// </summary>
    public (string Id, uint SignalId) Awakeable()
    {
        EnsureActive();
        uint signalId;
        lock (_commandLock) signalId = NextSignalId();   // first = 17 (B4)
        _signalCompletions.GetOrRegister((int)signalId);
        return (BuildAwakeableId((int)signalId), signalId);
    }

    /// <summary>
    ///     Builds an awakeable ID in the V4 signal format:
    ///     "sign_1" + Base64UrlSafe(rawInvocationId + BigEndian32(signalId)).
    /// </summary>
    private string BuildAwakeableId(int signalId)
    {
        var rawId = RawInvocationId;
        var bufferLength = rawId.Length + 4;
        Span<byte> byteBuffer = bufferLength <= 256 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        rawId.CopyTo(byteBuffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(byteBuffer[rawId.Length..], (uint)signalId);

        Span<char> charBuffer = stackalloc char[6 + System.Buffers.Text.Base64Url.GetEncodedLength(bufferLength)];
        "sign_1".CopyTo(charBuffer);
        System.Buffers.Text.Base64Url.EncodeToChars(byteBuffer, charBuffer[6..], out _, out var charsWritten);
        return new string(charBuffer[..(6 + charsWritten)]);
    }

    public void ResolveAwakeable(string id, ReadOnlyMemory<byte> payload)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.CompleteAwakeable);
                return;
            }

            WriteCommand(MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableSuccess(id, payload.Span));
            _journal.RecordCommand(JournalEntryType.CompleteAwakeable);
        }
    }

    public void RejectAwakeable(string id, string reason)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.CompleteAwakeable);
                return;
            }

            WriteCommand(MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableFailure(id, 500, reason));
            _journal.RecordCommand(JournalEntryType.CompleteAwakeable);
        }
    }

    // ------- Promises -------

    public async ValueTask<T> GetPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SimpleCompletablePrefix(JournalEntryType.GetPromise,
            id => ProtobufCodec.CreateGetPromiseCommand(name, id), MessageType.GetPromiseCommand, ct, name);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    public async ValueTask<T?> PeekPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();
        var completionId = SimpleCompletablePrefix(JournalEntryType.PeekPromise,
            id => ProtobufCodec.CreatePeekPromiseCommand(name, id), MessageType.PeekPromiseCommand, ct, name);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return completion.Value.IsEmpty ? default : Deserialize<T>(completion.Value);
    }

    public void ResolvePromise<T>(string name, T payload)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            var completionId = NextCompletionId();   // burns an id in BOTH branches (proto field 11)
            if (State == InvocationState.Replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.CompletePromise, name);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
                return;
            }

            var serialized = Serialize(payload);
            WriteCommand(MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess(name, serialized.Span, completionId));
            _journal.RecordCommand(JournalEntryType.CompletePromise, name);
        }
    }

    public void RejectPromise(string name, string reason)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            var completionId = NextCompletionId();
            if (State == InvocationState.Replaying)
            {
                var cmd = DequeueReplayCommand(JournalEntryType.CompletePromise, name);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
                return;
            }

            WriteCommand(MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseFailure(name, 500, reason, completionId));
            _journal.RecordCommand(JournalEntryType.CompletePromise, name);
        }
    }

    // ------- Cancel invocation -------

    public async ValueTask CancelInvocationAsync(string targetInvocationId, CancellationToken ct)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            if (State == InvocationState.Replaying)
            {
                DequeueReplayCommand(JournalEntryType.SendSignal);
                return;
            }

            WriteCommand(MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand(targetInvocationId));
            _journal.RecordCommand(JournalEntryType.SendSignal);
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
        Log.CancellingInvocation(Logger, InvocationId, targetInvocationId);
    }

    // ------- Output / Error (terminal) -------
    //
    // Each is ONE locked section with an in-lock State re-check, so a frame can never land AFTER a
    // concurrent Suspension frame (the Suspension write also happens under _commandLock).

    /// <summary>
    ///     Serializes the handler result INSIDE the lock (closing the torn-OutputCommand race with a
    ///     straggler Run proposal sharing _serializeBuffer) and writes OutputCommand + End.
    /// </summary>
    public async ValueTask CompleteAsync(object? result, CancellationToken ct)
    {
        EnsureActive();
        lock (_commandLock)
        {
            if (State == InvocationState.Closed)
            {
                if (_suspended) throw new SuspendedException();
                return;   // raced normal close
            }

            if (State == InvocationState.Replaying)
            {
                // Rust SysWriteOutput pops the journaled Output; SysEnd then succeeds only in
                // Processing — commands left AFTER the Output mean the journal recorded more than the
                // code produced (terminal.rs:56-73).
                DequeueReplayCommand(JournalEntryType.Output);
                if (_journal.IsReplaying)
                    throw new ProtocolException(
                        "Journal contains commands after Output — non-deterministic replay");
            }
            else
            {
                var output = result is null ? ReadOnlyMemory<byte>.Empty : SerializeObject(result);
                WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(output.Span));
            }

            _writer.WriteHeaderOnly(MessageType.End);
            State = InvocationState.Closed;
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a terminal failure as an OutputCommand with the failure oneof (non-retryable).
    /// </summary>
    public async ValueTask FailTerminalAsync(ushort code, string message, CancellationToken ct)
    {
        lock (_commandLock)
        {
            if (State == InvocationState.Closed)
            {
                if (_suspended) throw new SuspendedException();
                return;
            }

            WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputFailure(code, message));
            _writer.WriteHeaderOnly(MessageType.End);
            State = InvocationState.Closed;
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a transient error as an ErrorMessage (retryable). An optional
    ///     <paramref name="nextRetryDelay" /> overrides the runtime's delay before the next retry.
    /// </summary>
    public async ValueTask FailAsync(ushort code, string message, CancellationToken ct,
        TimeSpan? nextRetryDelay = null)
    {
        lock (_commandLock)
        {
            if (State == InvocationState.Closed)
            {
                if (_suspended) throw new SuspendedException();
                return;
            }

            var nextRetryDelayMs = nextRetryDelay is { } delay && delay > TimeSpan.Zero
                ? (ulong)delay.TotalMilliseconds
                : (ulong?)null;
            WriteCommand(MessageType.Error, ProtobufCodec.CreateErrorMessage(code, message, nextRetryDelayMs));
            _writer.WriteHeaderOnly(MessageType.End);
            State = InvocationState.Closed;
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
    }

    // ------- Serialization helpers (object request → pooled bytes, inside-lock-safe) -------

    private ReadOnlyMemory<byte> SerializeRequest(object? request) =>
        request is null ? ReadOnlyMemory<byte>.Empty : CopyToPooled(SerializeObject(request));
}
