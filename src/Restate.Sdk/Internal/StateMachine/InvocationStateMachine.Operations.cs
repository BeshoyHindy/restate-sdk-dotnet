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
    private async ValueTask<T> ReplayResultAsync<T>()
    {
        var replay = TakeReplayEntry();
        var completion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    /// <summary>Replays a one-way call; the result is the target invocation id notification.</summary>
    private async ValueTask<InvocationHandle> ReplaySendAsync()
    {
        var replay = TakeReplayEntry();
        var completion = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
        var invocationId = completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
        return new InvocationHandle(invocationId);
    }

    // ------- Side effects -------

    /// <summary>Runs a synchronous side effect without closure/Task overhead.</summary>
    public ValueTask<T> RunSync<T>(string name, Func<T> action, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return ReplayResultAsync<T>();

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

        return RunSyncAwaitFlush(flushTask, result, serializedCopy, name);
    }

    private async ValueTask<T> RunSyncAwaitFlush<T>(ValueTask flushTask, T result, ReadOnlyMemory<byte> serializedCopy, string name)
    {
        await flushTask.ConfigureAwait(false);
        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return result;
    }

    public async ValueTask<T> RunAsync<T>(string name, Func<Task<T>> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<T>().ConfigureAwait(false);

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
            var replay = TakeReplayEntry();
            _ = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            return;
        }

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
                {
                    // Exhausted retries — propose failure and record journal entry
                    var completionId = NextCompletionId();
                    var failureMsg = ProtobufCodec.CreateRunProposalFailure(
                        completionId, 500, $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}");
                    WriteRunCommand(name, completionId);
                    WriteCommand(MessageType.ProposeRunCompletion, failureMsg);
                    await FlushAsync(ct).ConfigureAwait(false);

                    // Append journal entry so _journal.Count advances — subsequent operations
                    // (e.g. saga compensations catching this TerminalException) use correct indices.
                    _journal.Append(JournalEntry.Completed(JournalEntryType.Run, ReadOnlyMemory<byte>.Empty, name));

                    throw new TerminalException(
                        $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}", 500);
                }

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
                {
                    var completionId = NextCompletionId();
                    var failureMsg = ProtobufCodec.CreateRunProposalFailure(
                        completionId, 500, $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}");
                    WriteRunCommand(name, completionId);
                    WriteCommand(MessageType.ProposeRunCompletion, failureMsg);
                    await FlushAsync(ct).ConfigureAwait(false);

                    _journal.Append(JournalEntry.Completed(JournalEntryType.Run, ReadOnlyMemory<byte>.Empty, name));

                    throw new TerminalException(
                        $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}", 500);
                }

                var delay = policy.GetDelay(attempt);
                Log.SideEffectRetrying(Logger, name, attempt + 1, delay, InvocationId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                attempt++;
            }
        }
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
            var replay = TakeReplayEntry();
            var replayTcs = RegisterReplayCompletion(in replay);
            var replayCompletion = await replayTcs.Task.ConfigureAwait(false);
            replayCompletion.ThrowIfFailure();
            return (replayTcs, Deserialize<T>(replayCompletion.Value));
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

    // ------- Calls -------

    /// <summary>
    ///     BUG 1 FIX: CallCommand now includes invocation_id_notification_idx (field 10).
    ///     The invocation-id notification gets its own completion id, which the SDK ignores for
    ///     request/response calls.
    /// </summary>
    private void WriteCallCommandMessage(string service, string handler, string? key, ReadOnlyMemory<byte> requestBytes,
        uint invocationIdNotificationIdx, uint completionId)
    {
        var msg = ProtobufCodec.CreateCallCommand(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx);
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

    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<TResponse>().ConfigureAwait(false);

        var requestBytes = SerializeObject(request);

        // Allocate the invocation-id slot (ignored for calls, but the notification needs a home).
        var invocationIdNotificationIdx = NextCompletionId();
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = NextCompletionId();
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        TimeSpan? delay, string? idempotencyKey, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplaySendAsync().ConfigureAwait(false);

        var requestBytes = SerializeObject(request);
        var invocationIdNotificationIdx = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.OneWayCall));
        var tcs = _completions.GetOrRegister((int)invocationIdNotificationIdx);

        WriteSendCommandMessage(service, handler, key, requestBytes, delay, idempotencyKey,
            invocationIdNotificationIdx);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)invocationIdNotificationIdx);
        var completion = await tcs.Task.ConfigureAwait(false);
        var invocationId = completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
        return new InvocationHandle(invocationId);
    }

    // ------- Non-blocking Call (CallFuture) -------

    public async ValueTask<TaskCompletionSource<CompletionResult>> CallFutureAsync(
        string service, string? key, string handler, object? request, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry();
            return RegisterReplayCompletion(in replay);
        }

        var requestBytes = SerializeObject(request);

        // Allocate the invocation-id slot (ignored for calls, but the notification needs a home).
        var invocationIdNotificationIdx = NextCompletionId();
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = NextCompletionId();
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId);

        await FlushAsync(ct).ConfigureAwait(false);

        return tcs;
    }

    // ------- Non-blocking Sleep (Timer) -------

    public async ValueTask<TaskCompletionSource<CompletionResult>> SleepFutureAsync(TimeSpan duration,
        CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = TakeReplayEntry();
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
            return await ReplayResultAsync<TResponse>().ConfigureAwait(false);

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
            var replay = TakeReplayEntry();
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
            var replay = TakeReplayEntry();
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
            AdvanceReplayIndex();
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
            AdvanceReplayIndex();
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
            AdvanceReplayIndex();
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
            var replay = TakeReplayEntry();
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
            var replay = TakeReplayEntry();
            _ = await AwaitReplayCompletionAsync(replay).ConfigureAwait(false);
            return;
        }

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

    // ------- Awakeable -------

    /// <summary>
    ///     Creates an awakeable. In V4 protocol, this is purely a local operation —
    ///     no command is sent to the server. The SDK registers a signal handle and
    ///     waits for a <c>SignalNotification</c> (type 0xFBFF) from the server.
    /// </summary>
    public (string Id, TaskCompletionSource<CompletionResult> Tcs) Awakeable()
    {
        EnsureActive();

        // Allocate the next signal index (separate from journal indices)
        var signalIndex = _nextSignalIndex++;
        var tcs = _signalCompletions.GetOrRegister(signalIndex);

        return (BuildAwakeableId(signalIndex), tcs);
    }

    /// <summary>
    ///     Builds an awakeable ID in the V4 signal format:
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
            AdvanceReplayIndex();
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
            AdvanceReplayIndex();
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
            return await ReplayResultAsync<T>().ConfigureAwait(false);

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
            var replay = TakeReplayEntry();
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
            AdvanceReplayIndex();
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
            AdvanceReplayIndex();
            return;
        }

        var completionId = NextCompletionId();

        WriteCommand(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseFailure(name, 500, reason, completionId));

        _journal.Append(JournalEntry.Completed(JournalEntryType.CompletePromise, ReadOnlyMemory<byte>.Empty, name));
    }

    // ------- Generic Calls (typed serialization by name) -------

    public async ValueTask<TResponse> CallAsync<TRequest, TResponse>(
        string service, string handler, TRequest request, string? key, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<TResponse>().ConfigureAwait(false);

        var requestBytes = Serialize(request);

        // Allocate the invocation-id slot (ignored for calls, but the notification needs a home).
        var invocationIdNotificationIdx = NextCompletionId();
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = NextCompletionId();
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<InvocationHandle> SendAsync<TRequest>(
        string service, string handler, TRequest request, string? key, TimeSpan? delay, string? idempotencyKey,
        CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplaySendAsync().ConfigureAwait(false);

        var requestBytes = Serialize(request);
        var invocationIdNotificationIdx = NextCompletionId();

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.OneWayCall));
        var tcs = _completions.GetOrRegister((int)invocationIdNotificationIdx);

        WriteSendCommandMessage(service, handler, key, requestBytes, delay, idempotencyKey,
            invocationIdNotificationIdx);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)invocationIdNotificationIdx);
        var completion = await tcs.Task.ConfigureAwait(false);
        var invocationIdStr = completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
        return new InvocationHandle(invocationIdStr);
    }

    // ------- Cancel invocation -------

    public async ValueTask CancelInvocationAsync(string targetInvocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            AdvanceReplayIndex();
            return;
        }

        var msg = ProtobufCodec.CreateCancelInvocationCommand(targetInvocationId);
        WriteCommand(MessageType.SendSignalCommand, msg);

        _journal.Append(JournalEntry.Completed(JournalEntryType.SendSignal, ReadOnlyMemory<byte>.Empty));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.CancellingInvocation(Logger, InvocationId, targetInvocationId);
    }

    // ------- Calls with idempotency key -------

    private void WriteCallCommandMessageWithOptions(string service, string handler, string? key,
        ReadOnlyMemory<byte> requestBytes,
        uint invocationIdNotificationIdx, uint completionId, string? idempotencyKey)
    {
        var msg = ProtobufCodec.CreateCallCommandWithOptions(
            service, handler, key, requestBytes.Span, completionId, invocationIdNotificationIdx, idempotencyKey);
        WriteCommand(MessageType.CallCommand, msg);
    }

    public async ValueTask<TResponse> CallAsync<TResponse>(
        string service, string? key, string handler, object? request, string? idempotencyKey, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return await ReplayResultAsync<TResponse>().ConfigureAwait(false);

        var requestBytes = SerializeObject(request);

        // Allocate the invocation-id slot (ignored for calls, but the notification needs a home).
        var invocationIdNotificationIdx = NextCompletionId();
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = NextCompletionId();
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister((int)completionId);

        WriteCallCommandMessageWithOptions(service, handler, key, requestBytes, invocationIdNotificationIdx,
            completionId, idempotencyKey);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, (int)completionId);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    // ------- Output / Error -------

    /// <summary>
    ///     BUG 2 FIX: OutputCommand always sets the Value oneof, even for empty content (void handlers).
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

        if (completionIds.Count == 0 && signalIds.Count == 0)
        {
            await FailAsync(500,
                "Input stream closed but no durable operation is pending — nothing to suspend on",
                ct).ConfigureAwait(false);
            return;
        }

        // Set Suspended BEFORE flushing to prevent re-entry if FlushAsync throws.
        State = InvocationState.Suspended;

        Log.InvocationSuspended(Logger, InvocationId, completionIds.Count, signalIds.Count);
        WriteCommand(MessageType.Suspension,
            ProtobufCodec.CreateSuspensionMessage(NegotiatedVersion, completionIds, signalIds));
        await FlushAsync(ct).ConfigureAwait(false);
    }
}
