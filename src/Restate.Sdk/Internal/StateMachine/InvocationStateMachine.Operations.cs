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
                    "re-execute a side effect mid-replay",   // 1.7 case 3
                    ProtocolException.JournalMismatchCode);   // UncompletedDoProgress → 570 (errors.rs:391)

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
            catch (OperationCanceledException) when (_cancelled)
            {
                // Inbound CANCEL fired while the Run closure observed the handler token. Translate the
                // OCE into the SAME terminal cancel signal the parked-await path uses, so the handler
                // deterministically unwinds through `catch (TerminalException)` → 409 Output, never the
                // generic 500 Error arm (which would mis-classify cancel as a retryable failure).
                throw new TerminalException("cancelled", CancelledStatusCode);
            }
            catch (TerminalException ex)
            {
                // Terminal failure (thrown directly or via retry exhaustion): propose FAILURE and
                // fall through — the caller's notification await re-raises after durability.
                lock (_commandLock)
                {
                    ThrowIfClosedLocked();
                    // G11: ride V6 Failure.metadata on the run's terminal proposal (gated to sub-V6 drop).
                    WriteCommand(MessageType.ProposeRunCompletion,
                        ProtobufCodec.CreateRunProposalFailure(
                            completionId, ex.Code, ex.Message, OutgoingFailureMetadata(ex)));
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
            catch (OperationCanceledException) when (_cancelled)
            {
                // Inbound CANCEL during a void Run closure: translate to the terminal cancel signal
                // (see the generic ExecuteAndProposeRunAsync for the full rationale).
                throw new TerminalException("cancelled", CancelledStatusCode);
            }
            catch (TerminalException ex)
            {
                lock (_commandLock)
                {
                    ThrowIfClosedLocked();
                    // G11: ride V6 Failure.metadata on the run's terminal proposal (gated to sub-V6 drop).
                    WriteCommand(MessageType.ProposeRunCompletion,
                        ProtobufCodec.CreateRunProposalFailure(
                            completionId, ex.Code, ex.Message, OutgoingFailureMetadata(ex)));
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

    /// <summary>
    ///     Observes a fire-and-forget prefix flush issued from a SYNCHRONOUS future-creating method
    ///     (which cannot await it) and routes any flush fault into the future's completion slot via
    ///     TryFail, so the failure surfaces through the future's await path rather than being lost on
    ///     a discarded ValueTask. The fast path (flush already completed synchronously, e.g. the pipe
    ///     had buffer capacity) costs nothing. A success leaves the slot untouched — the real
    ///     notification resolves it. (Issue 1 hardening: surface, don't swallow, prefix-flush faults.)
    /// </summary>
    private void ObserveFlushFault(ValueTask flush, uint completionId)
    {
        if (flush.IsCompletedSuccessfully) return;
        _ = RouteFlushFaultAsync(flush, completionId);
    }

    private async Task RouteFlushFaultAsync(ValueTask flush, uint completionId)
    {
        try
        {
            await flush.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Same containment as the detached-execute catch: a write/flush failure (NOT a
            // TerminalException, which travels via the proposal) faults the slot so the future's
            // awaiter observes it and nothing goes unobserved.
            _completions.TryFail((int)completionId, 500, ex.Message);
        }
    }

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

        // Flush the prefix before user code proceeds (matches the blocking path). RunFutureAsync is
        // SYNCHRONOUS (it returns a resolve thunk, so it cannot await the flush). We therefore OBSERVE
        // the discarded flush ValueTask's fault and route any exception into THIS run's completion
        // slot, so a flush failure surfaces through the future's await path (completion.ThrowIfFailure
        // after AwaitNotificationAsync) instead of being silently swallowed by a discarded ValueTask.
        // This is the same infrastructure-failure containment the detached-execute catch below uses,
        // and it keeps wire order and the synchronous return intact (Issue 1 hardening).
        ObserveFlushFault(FlushGatedAsync(ct), completionId);

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
        ReadOnlyMemory<byte> requestBytes, uint invocationIdNotificationIdx, uint completionId, string? idempotencyKey,
        IReadOnlyDictionary<string, string>? headers)
    {
        var msg = ProtobufCodec.CreateCallCommandWithOptions(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx, idempotencyKey,
            headers);
        WriteCommand(MessageType.CallCommand, msg);
    }

    private void WriteSendCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        TimeSpan? delay, string? idempotencyKey, uint notificationIdx,
        IReadOnlyDictionary<string, string>? headers)
    {
        var invokeTime = delay.HasValue && delay.Value > TimeSpan.Zero
            ? (ulong)DateTimeOffset.UtcNow.Add(delay.Value).ToUnixTimeMilliseconds()
            : 0UL;
        var msg = ProtobufCodec.CreateSendCommandWithOptions(
            service, handler, key, requestBytes.Span, invokeTime, idempotencyKey, notificationIdx, headers);
        WriteCommand(MessageType.OneWayCallCommand, msg);
    }

    /// <summary>
    ///     Template C — Call (two ids: invocationIdNotificationIdx then resultCompletionId, sys_call
    ///     order vm/mod.rs:742-744). The dummy-journal-slot hack is gone; the request is serialized
    ///     inside _commandLock and the result is resolved from the notification via the park API.
    /// </summary>
    private async ValueTask<(uint ResultCompletionId, uint InvocationIdCompletionId)> CallPrefixAsync(
        string service, string? key, string handler, ReadOnlyMemory<byte> requestBytes,
        string? idempotencyKey, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        // G8 — empty-idempotency-key parity (shared-core EMPTY_IDEMPOTENCY_KEY, mod.rs:735-740). An
        // empty (but non-null) key is a caller bug: Rust transitions to HitError BEFORE journaling, so
        // we throw a non-retryable TerminalException here, BEFORE NextCompletionId / RecordCommand, to
        // avoid mutating the journal. A null key (no idempotency) is the normal path and is allowed.
        ThrowIfEmptyIdempotencyKey(idempotencyKey);

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
            else if (idempotencyKey is not null || headers is not null)
            {
                // Any optional command field (idempotency key OR custom headers) routes through the
                // options writer; both default-absent when null so the emitted command stays minimal.
                WriteCallCommandMessageWithOptions(service, handler, key, requestBytes,
                    invocationIdCompletionId, resultCompletionId, idempotencyKey, headers);
                _journal.RecordCommand(JournalEntryType.Call);
            }
            else
            {
                WriteCallCommandMessage(service, handler, key, requestBytes,
                    invocationIdCompletionId, resultCompletionId);
                _journal.RecordCommand(JournalEntryType.Call);
            }

            // Track this child for implicit cancel-on-CANCEL — Rust sys_call gates this push on
            // cancel_children_calls, true by default (mod.rs:766-777), which we bake in by ALWAYS
            // appending here (one-way Sends are intentionally untracked). The append is INSIDE
            // _commandLock in BOTH replay and processing branches so the registry rebuilds identically
            // across attempts (same id, same order) — that determinism is what lets the terminal
            // child-cancel emission dequeue-match its journaled SendSignals on replay. We track the
            // invocation-id completion id (NOT the result id): the resolved id string lands in
            // _completions under THIS id (HandleIncomingMessage InvocationId branch).
            _trackedChildren.Add(invocationIdCompletionId);
        }

        if (!replaying) await FlushGatedAsync(ct).ConfigureAwait(false);
        return (resultCompletionId, invocationIdCompletionId);
    }

    /// <summary>
    ///     G8 — rejects a supplied-but-empty idempotency key before any journal mutation, matching
    ///     shared-core EMPTY_IDEMPOTENCY_KEY (errors.rs:114, codes::INTERNAL=500). A null key means
    ///     "no idempotency" and is permitted; only an empty string is the programming error.
    /// </summary>
    private static void ThrowIfEmptyIdempotencyKey(string? idempotencyKey)
    {
        if (idempotencyKey is { Length: 0 })
            throw new TerminalException(
                "Trying to execute an idempotent request with an empty idempotency key. " +
                "The idempotency key must be non-empty.");
    }

    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        return await CallAsync<TResponse>(service, key, handler, request, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Full-option blocking Call: idempotency key (G8-validated) + custom headers (G5). All other
    ///     CallAsync overloads funnel through here so the option-threading lives in one place.
    /// </summary>
    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, string? idempotencyKey,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var (resultCompletionId, _) = await CallPrefixAsync(
            service, key, handler, requestBytes, idempotencyKey, headers, ct).ConfigureAwait(false);
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
        var (resultCompletionId, _) = await CallPrefixAsync(service, key, handler, requestBytes, null, null, ct)
            .ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
            .ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    /// <summary>
    ///     G4 — get_call_invocation_id: a blocking Call that exposes BOTH the result and the child's
    ///     invocation id. The prefix already allocates the invocation-id completion id alongside the
    ///     result id; we hand both to a <see cref="CallHandle{TResponse}" /> so the handler can await
    ///     the response AND resolve the id (mirroring the Send lazy-id round trip). The id thunk is
    ///     lazy: asking only for the response never parks on the id slot.
    /// </summary>
    public CallHandle<TResponse> CallHandleAsync<TResponse>(
        string service, string? key, string handler, object? request, string? idempotencyKey,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        // Journal synchronously inside the prefix before its first await so the command order is fixed;
        // both completion ids are captured once the (cheap) flush completes. The same prefix Task is
        // shared by both thunks, so the command is emitted exactly once regardless of which (or both)
        // the caller awaits.
        var prefixTask = CallPrefixAsync(service, key, handler, requestBytes, idempotencyKey, headers, ct);
        return new CallHandle<TResponse>(
            async () =>
            {
                var (resultCompletionId, _) = await prefixTask.ConfigureAwait(false);
                var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
                    .ConfigureAwait(false);
                completion.ThrowIfFailure();
                return Deserialize<TResponse>(completion.Value);
            },
            async () =>
            {
                var (_, invocationIdCompletionId) = await prefixTask.ConfigureAwait(false);
                return await ResolveInvocationIdAsync(invocationIdCompletionId).ConfigureAwait(false);
            });
    }

    // ------- Non-blocking Call (CallFuture) -------

    public Func<ValueTask<CompletionResult>> CallFutureAsync(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var resultIdTask = CallPrefixAsync(service, key, handler, requestBytes, null, null, ct);
        // The prefix journals synchronously inside CallPrefixAsync before its first await, so the
        // command order is fixed; we capture the result id once the (cheap) flush completes.
        return async () =>
        {
            var (resultCompletionId, _) = await resultIdTask.ConfigureAwait(false);
            return await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion)
                .ConfigureAwait(false);
        };
    }

    // ------- Sends -------

    public async ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        TimeSpan? delay, string? idempotencyKey, CancellationToken ct)
    {
        return await SendAsync(service, key, handler, request, delay, idempotencyKey, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Full-option send: idempotency key (G8-validated) + delay + custom headers (G5). The
    ///     object-request and typed-request send overloads funnel through here.
    /// </summary>
    public async ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        TimeSpan? delay, string? idempotencyKey, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = SerializeRequest(request);
        var invocationIdCompletionId = SendPrefix(service, key, handler, requestBytes, delay, idempotencyKey, headers);
        await FlushGatedAsync(ct).ConfigureAwait(false);
        // Fire-and-forget: the handle resolves the invocation id lazily through the park API, so an
        // unawaited handle never parks/suspends (Rust sys_send returns SendHandle immediately).
        return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));
    }

    public async ValueTask<InvocationHandle> SendAsync<TRequest>(
        string service, string handler, TRequest request, string? key, TimeSpan? delay, string? idempotencyKey,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        EnsureActive();
        var requestBytes = CopyToPooled(Serialize(request));
        var invocationIdCompletionId = SendPrefix(service, key, handler, requestBytes, delay, idempotencyKey, headers);
        await FlushGatedAsync(ct).ConfigureAwait(false);
        return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));
    }

    private uint SendPrefix(string service, string? key, string handler, ReadOnlyMemory<byte> requestBytes,
        TimeSpan? delay, string? idempotencyKey, IReadOnlyDictionary<string, string>? headers)
    {
        // G8 — same empty-idempotency-key guard as the call path (mod.rs:810-815). Thrown BEFORE the
        // lock / NextCompletionId / RecordCommand so an invalid key never mutates the journal.
        ThrowIfEmptyIdempotencyKey(idempotencyKey);

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
                    invocationIdCompletionId, headers);
                _journal.RecordCommand(JournalEntryType.OneWayCall);
            }

            // One-way (Send) children are NOT tracked: VMOptions::default sets
            // cancel_children_one_way_calls=false (lib.rs:257), so Rust's sys_send push (mod.rs:843-854)
            // is gated off — we omit the append entirely (scope-limitation #2).
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
        var (completionId, needsFlush) = SleepPrefix(duration);
        // Issue 2 hardening: SleepAsync is already async, so AWAIT the prefix flush BEFORE parking.
        // A flush fault now surfaces here instead of being masked by the subsequent park (where the
        // discarded fire-and-forget flush would have left the failure unobserved).
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
    }

    public Func<ValueTask<CompletionResult>> SleepFutureAsync(TimeSpan duration, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = SleepPrefix(duration);
        // SleepFutureAsync is SYNCHRONOUS (returns a resolve thunk) so it cannot await the flush;
        // observe the flush ValueTask's fault and route it into the slot, mirroring RunFutureAsync.
        if (needsFlush) ObserveFlushFault(FlushGatedAsync(ct), completionId);
        return () => AwaitNotificationAsync(completionId, NotificationKind.Completion);
    }

    /// <summary>
    ///     Sleep locked prefix. Allocates the deterministic completion id in both branches; replay
    ///     dequeues+validates, processing writes+records. Returns the id and whether a live flush is
    ///     owed (Processing only) so the async caller can AWAIT it before parking and a sync
    ///     future-creating caller can observe its fault.
    /// </summary>
    private (uint CompletionId, bool NeedsFlush) SleepPrefix(TimeSpan duration)
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

        return (completionId, !replaying);
    }

    // ------- Attach / GetInvocationOutput -------

    public ValueTask<TResponse> AttachInvocationAsync<TResponse>(string invocationId, CancellationToken ct) =>
        AttachInvocationAsync<TResponse>(AttachTarget.InvocationId(invocationId), ct);

    /// <summary>
    ///     G6 — attach by an arbitrary <see cref="AttachTarget" /> (invocation id, workflow id, or
    ///     idempotency id). The string-id overload delegates here with the InvocationId variant so the
    ///     command-build/replay path is shared; the target only changes which oneof the codec sets.
    /// </summary>
    public async ValueTask<TResponse> AttachInvocationAsync<TResponse>(AttachTarget target, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = SimpleCompletablePrefix(JournalEntryType.AttachInvocation,
            id => ProtobufCodec.CreateAttachInvocationCommand(target, id), MessageType.AttachInvocationCommand);
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // await the flush before parking (Issue 2)
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public ValueTask<TResponse?> GetInvocationOutputAsync<TResponse>(string invocationId, CancellationToken ct) =>
        GetInvocationOutputAsync<TResponse>(AttachTarget.InvocationId(invocationId), ct);

    /// <summary>
    ///     G7 — get-output by an arbitrary <see cref="AttachTarget" /> (the get-output twin of
    ///     <see cref="AttachInvocationAsync{TResponse}(AttachTarget, CancellationToken)" />).
    /// </summary>
    public async ValueTask<TResponse?> GetInvocationOutputAsync<TResponse>(AttachTarget target, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = SimpleCompletablePrefix(JournalEntryType.GetInvocationOutput,
            id => ProtobufCodec.CreateGetInvocationOutputCommand(target, id),
            MessageType.GetInvocationOutputCommand);
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // await the flush before parking (Issue 2)
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        // Void/empty completion means not yet completed — return default
        return completion.Value.IsEmpty ? default : Deserialize<TResponse>(completion.Value);
    }

    /// <summary>
    ///     Template A locked prefix for completable ops whose only parameters are a key/id and the
    ///     completion id (Attach, GetInvocationOutput, GetPromise, PeekPromise). Allocates the id in
    ///     BOTH branches; replay dequeues+validates, processing writes+records. Returns the id and
    ///     whether a live flush is owed (Processing only); every caller is already async and AWAITS
    ///     that flush before parking, so a flush fault surfaces instead of being masked by the park
    ///     (Issue 2 hardening).
    /// </summary>
    private (uint CompletionId, bool NeedsFlush) SimpleCompletablePrefix(JournalEntryType type,
        Func<uint, Google.Protobuf.IMessage> create, MessageType wireType, string? name = null)
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

        return (completionId, !replaying);
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
        var (completionId, needsFlush) = SimpleCompletablePrefix(JournalEntryType.GetPromise,
            id => ProtobufCodec.CreateGetPromiseCommand(name, id), MessageType.GetPromiseCommand, name);
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // await the flush before parking (Issue 2)
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    public async ValueTask<T?> PeekPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = SimpleCompletablePrefix(JournalEntryType.PeekPromise,
            id => ProtobufCodec.CreatePeekPromiseCommand(name, id), MessageType.PeekPromiseCommand, name);
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // await the flush before parking (Issue 2)
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return completion.Value.IsEmpty ? default : Deserialize<T>(completion.Value);
    }

    public async ValueTask ResolvePromise<T>(string name, T payload, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = CompletePromisePrefix(name,
            id => ProtobufCodec.CreateCompletePromiseSuccess(name, Serialize(payload).Span, id));
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // command on the wire before parking
        // CompletePromiseCommand is COMPLETABLE (proto field 11): await the
        // CompletePromiseCompletionNotification. The Failure arm (resolving an already-completed
        // promise, async_results parity) surfaces as a TerminalException via ThrowIfFailure.
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
    }

    public async ValueTask RejectPromise(string name, string reason, CancellationToken ct)
    {
        EnsureActive();
        var (completionId, needsFlush) = CompletePromisePrefix(name,
            id => ProtobufCodec.CreateCompletePromiseFailure(name, 500, reason, id));
        if (needsFlush) await FlushGatedAsync(ct).ConfigureAwait(false);   // command on the wire before parking
        var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
        completion.ThrowIfFailure();
    }

    /// <summary>
    ///     Locked prefix for the two CompletePromise variants. Allocates the completion id (proto
    ///     field 11) in BOTH branches; replay dequeues+validates the journaled command, processing
    ///     writes+records. Unlike the Template A helper, the command bytes depend on a per-variant
    ///     payload, so the factory takes only the id and the caller closes over the value/reason.
    ///     Both branches fall through to a single await of the buffered/live completion — on replay
    ///     the known-entries batch parked the CompletePromiseCompletion as an early-completion slot,
    ///     and a journaled-but-uncompleted promise raises the UncompletedDoProgressDuringReplay guard
    ///     exactly like every other completable.
    /// </summary>
    private (uint CompletionId, bool NeedsFlush) CompletePromisePrefix(string name,
        Func<uint, Google.Protobuf.IMessage> create)
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
                var cmd = DequeueReplayCommand(JournalEntryType.CompletePromise, name);
                ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
            }
            else
            {
                WriteCommand(MessageType.CompletePromiseCommand, create(completionId));
                _journal.RecordCommand(JournalEntryType.CompletePromise, name);
            }
        }

        return (completionId, !replaying);
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
                // Cancel = CANCEL built-in idx (signal_id = Idx oneof); no signal name.
                DequeueReplayCommand(JournalEntryType.SendSignal,
                    targetInvocationId, ProtobufCodec.CancelSignalId, expectedSignalName: null);
                return;
            }

            WriteCommand(MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand(targetInvocationId));
            _journal.RecordCommand(JournalEntryType.SendSignal);
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
        Log.CancellingInvocation(Logger, InvocationId, targetInvocationId);
    }

    // ------- Named signal (send side) -------

    /// <summary>
    ///     Sends a NAMED signal to a target invocation (Rust sys_complete_signal, mod.rs:955-979). The
    ///     wire shape is a SendSignalCommandMessage with the NAME oneof + a value/failure result — the
    ///     same non-completable journaled command class as CancelInvocationAsync, only with a name and
    ///     payload instead of the CANCEL built-in idx. Replay-safe by the identical pattern: one
    ///     journal entry (JournalEntryType.SendSignal) written under _commandLock, dequeued+validated
    ///     on replay; deterministic across attempts because the name and ordering are fixed by call
    ///     order. <paramref name="failure" /> null = success value; non-null = failure result.
    /// </summary>
    public async ValueTask SendSignalAsync(string targetInvocationId, string name,
        ReadOnlyMemory<byte> value, CancellationToken ct, (ushort Code, string Message)? failure = null)
    {
        EnsureActive();
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            if (State == InvocationState.Replaying)
            {
                // Named signal = Name oneof (signal_id); no idx. Payload bytes are NOT compared (§5).
                DequeueReplayCommand(JournalEntryType.SendSignal,
                    targetInvocationId, expectedSignalIdx: null, name);
                return;
            }

            var command = failure is { } f
                ? ProtobufCodec.CreateSendNamedSignalFailure(targetInvocationId, name, f.Code, f.Message)
                : ProtobufCodec.CreateSendNamedSignalSuccess(targetInvocationId, name, value.Span);
            WriteCommand(MessageType.SendSignalCommand, command);
            _journal.RecordCommand(JournalEntryType.SendSignal);
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
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
                    // Journaled-more-than-the-code-produced is a recorded-vs-current divergence →
                    // JOURNAL_MISMATCH (570), the terminal.rs:56-73 analogue.
                    throw new ProtocolException(
                        "Journal contains commands after Output — non-deterministic replay",
                        ProtocolException.JournalMismatchCode);
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
    public async ValueTask FailTerminalAsync(ushort code, string message, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        // G11: emit V6 Failure.metadata on the terminal OutputCommand only when the negotiated
        // protocol version supports it AND there is any to emit (null/empty otherwise).
        var emittedMetadata = SupportsErrorMetadata && metadata is { Count: > 0 } ? metadata : null;
        lock (_commandLock)
        {
            if (State == InvocationState.Closed)
            {
                if (_suspended) throw new SuspendedException();
                return;
            }

            // Implicit child-cancel runs FIRST, only on the inbound-CANCEL path (_cancelled). The
            // child-cancel SendSignals must strictly PRECEDE the terminal Output in the wire/journal —
            // matching Rust, where sys_cancel_invocation (mod.rs:470-476) runs inside do_progress
            // before sys_end. The whole block is one atomic _commandLock section, so no fan-out
            // straggler can interleave a frame between the child-cancels and the Output.
            if (_cancelled)
                EmitChildCancelsLocked();

            WriteCommand(MessageType.OutputCommand,
                ProtobufCodec.CreateOutputFailure(code, message, emittedMetadata));
            _writer.WriteHeaderOnly(MessageType.End);
            State = InvocationState.Closed;
        }

        await FlushGatedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Emits one cancel SendSignalCommand(idx=1) per tracked child whose invocation-id notification
    ///     had RESOLVED at CANCEL time — the .NET analogue of Rust do_progress's cancel loop
    ///     (vm/mod.rs:445-476). Drains the snapshot built by TriggerCancellation (registry == journal
    ///     order), reusing the EXACT machinery CancelInvocationAsync uses to write each SendSignal +
    ///     record its journal entry. Caller MUST hold _commandLock; called only from FailTerminalAsync's
    ///     single terminal-writer block, so it shares CancelInvocationAsync's single-writer discipline.
    ///
    ///     REPLAY SAFETY — always Processing, never Replaying. To reach FailTerminalAsync on the cancel
    ///     path the handler must have PARKED (e.g. on Sleep) and then had that await faulted by inbound
    ///     CANCEL. Parking is only legal once replay has drained (AwaitNotificationAsync's replay-
    ///     mutation guard throws otherwise), so State == Processing whenever this runs. The child-cancel
    ///     SendSignals are therefore always written FRESH and journaled in a fixed position
    ///     ([Call commands][child-cancel SendSignals][Output{409}][End]); a 409 terminal Output ends the
    ///     invocation permanently, so these SendSignals never re-enter a later replay batch. They are
    ///     still deterministic across the (re)attempt that produces them: the snapshot reflects the SAME
    ///     resolved-child set because each child's invocation-id is a journaled CallInvocationIdCompletion
    ///     replayed in the StartMessage known-entries on every attempt.
    ///
    ///     SCOPE / DOCUMENTED DIVERGENCE from Rust:
    ///       1. ONLY children whose invocation-id is ALREADY resolved are cancelled. Rust suspends
    ///          do_progress to fetch unresolved ids (test call_then_cancel_without_invocation_id), but
    ///          here the cancel fires on the unwinding terminal path where suspending is impossible, so
    ///          an unresolved child is deterministically SKIPPED (the snapshot omits it).
    ///       2. Only the Call path cancels children: one-way Sends are not tracked, matching
    ///          VMOptions::default's cancel_children_one_way_calls=false (lib.rs:257).
    ///       3. Fires ONLY on the inbound-CANCEL terminal path (_cancelled) — never on caller-ct
    ///          teardown or a generic 500 failure.
    /// </summary>
    private void EmitChildCancelsLocked()
    {
        // The resolved child ids were snapshotted at CANCEL time (TriggerCancellation), in registry
        // (== journal) order, BEFORE FailAll cleared _completions. Null means no tracked child resolved
        // (or none was tracked) — nothing to emit.
        if (_cancelledChildInvocationIds is not { } childIds)
            return;

        foreach (var childId in childIds)
        {
            WriteCommand(MessageType.SendSignalCommand,
                ProtobufCodec.CreateCancelInvocationCommand(childId));
            _journal.RecordCommand(JournalEntryType.SendSignal);
        }
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
