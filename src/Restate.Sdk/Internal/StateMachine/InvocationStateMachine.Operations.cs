using System.Text;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.StateMachine;

internal sealed partial class InvocationStateMachine
{
    // ------- Side effects -------

    /// <summary>Runs a synchronous side effect without closure/Task overhead.</summary>
    public ValueTask<T> RunSync<T>(string name, Func<T> action, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
            return RunSyncReplayAsync<T>(name, ct);

        var result = action();
        var serialized = Serialize(result);

        WriteRunCommand(name);
        WriteRunProposal(serialized.Span);

        var flushTask = FlushAsync(ct);
        // ToArray: serialized references _serializeBuffer which is reused across Serialize calls.
        var serializedCopy = serialized.ToArray();
        if (flushTask.IsCompletedSuccessfully)
        {
            _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
            Log.SideEffectExecuted(Logger, name, InvocationId);
            return new ValueTask<T>(result);
        }

        return RunSyncAwaitFlush(flushTask, result, serializedCopy, name);
    }

    private async ValueTask<T> RunSyncReplayAsync<T>(string name, CancellationToken ct)
    {
        var replay = await ReplayNextEntryAsync(JournalEntryType.Run, name, ct).ConfigureAwait(false);
        return Deserialize<T>(replay.Result);
    }

    private async ValueTask<T> RunSyncAwaitFlush<T>(ValueTask flushTask, T result, byte[] serializedCopy, string name)
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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.Run, name, ct).ConfigureAwait(false);
            return Deserialize<T>(replay.Result);
        }

        T result;
        if (retryPolicy is not null)
            result = await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false);
        else
            result = await action().ConfigureAwait(false);

        var serialized = Serialize(result);

        WriteRunCommand(name);
        WriteRunProposal(serialized.Span);

        await FlushAsync(ct).ConfigureAwait(false);

        // ToArray: serialized references _serializeBuffer which is reused across Serialize calls.
        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serialized.ToArray(), name));
        Log.SideEffectExecuted(Logger, name, InvocationId);
        return result;
    }

    public async ValueTask RunAsync(string name, Func<Task> action, CancellationToken ct,
        RetryPolicy? retryPolicy = null)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            await ReplayNextEntryAsync(JournalEntryType.Run, name, ct).ConfigureAwait(false);
            return;
        }

        if (retryPolicy is not null)
            await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false);
        else
            await action().ConfigureAwait(false);

        WriteRunCommand(name);
        WriteRunProposal(ReadOnlySpan<byte>.Empty);

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
                    // Exhausted retries — propose failure
                    var completionId = (uint)_journal.Count;
                    var failureMsg = ProtobufCodec.CreateRunProposalFailure(
                        completionId, 500, $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}");
                    WriteRunCommand(name);
                    WriteCommand(MessageType.ProposeRunCompletion, failureMsg);
                    await FlushAsync(ct).ConfigureAwait(false);

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
                    var completionId = (uint)_journal.Count;
                    var failureMsg = ProtobufCodec.CreateRunProposalFailure(
                        completionId, 500, $"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}");
                    WriteRunCommand(name);
                    WriteCommand(MessageType.ProposeRunCompletion, failureMsg);
                    await FlushAsync(ct).ConfigureAwait(false);

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

    private void WriteRunCommand(string name)
    {
        var completionId = (uint)_journal.Count;
        var msg = ProtobufCodec.CreateRunCommand(name, completionId);
        WriteCommand(MessageType.RunCommand, msg);
    }

    private void WriteRunProposal(ReadOnlySpan<byte> serialized)
    {
        var completionId = (uint)_journal.Count;
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
            var replay = await ReplayNextEntryAsync(JournalEntryType.Run, name, ct).ConfigureAwait(false);
            var result = Deserialize<T>(replay.Result);
            var replayTcs = new TaskCompletionSource<CompletionResult>();
            replayTcs.SetResult(CompletionResult.Success(replay.Result));
            return (replayTcs, result);
        }

        var value = await action().ConfigureAwait(false);
        var serialized = Serialize(value);

        WriteRunCommand(name);
        WriteRunProposal(serialized.Span);

        await FlushAsync(ct).ConfigureAwait(false);

        // ToArray: serialized references _serializeBuffer which is reused across Serialize calls.
        var serializedCopy = serialized.ToArray();
        _journal.Append(JournalEntry.Completed(JournalEntryType.Run, serializedCopy, name));
        Log.SideEffectExecuted(Logger, name, InvocationId);

        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Success(serializedCopy));
        return (tcs, value);
    }

    // ------- Calls -------

    /// <summary>
    ///     BUG 1 FIX: CallCommand now includes invocation_id_notification_idx (field 10).
    ///     We allocate a dummy journal slot for the invocation ID notification that the SDK ignores for calls.
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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.Call, null, ct).ConfigureAwait(false);
            return Deserialize<TResponse>(replay.Result);
        }

        var requestBytes = SerializeObject(request);

        // BUG 1 FIX: Allocate dummy slot for invocation ID notification (SDK ignores it for calls)
        var invocationIdNotificationIdx = (uint)_journal.Count;
        _journal.Append(JournalEntry.Completed(JournalEntryType.Call, ReadOnlyMemory<byte>.Empty));
        _completions.GetOrRegister((int)invocationIdNotificationIdx); // Register so CompletionManager doesn't complain

        var completionId = (uint)_journal.Count;
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<InvocationHandle> SendAsync(string service, string? key, string handler, object? request,
        TimeSpan? delay, string? idempotencyKey, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.OneWayCall, null, ct).ConfigureAwait(false);
            var replayId = replay.Result.IsEmpty ? "" : Encoding.UTF8.GetString(replay.Result.Span);
            return new InvocationHandle(replayId);
        }

        var requestBytes = SerializeObject(request);
        var invocationIdNotificationIdx = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.OneWayCall));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteSendCommandMessage(service, handler, key, requestBytes, delay, idempotencyKey,
            invocationIdNotificationIdx);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
            var replay = await ReplayNextEntryAsync(JournalEntryType.Call, null, ct).ConfigureAwait(false);
            var replayTcs = new TaskCompletionSource<CompletionResult>();
            if (replay.IsCompleted)
            {
                replayTcs.SetResult(CompletionResult.Success(replay.Result));
            }
            else
            {
                // Pending replay entry — register for completion
                var idx = _journal.Count - 1;
                return _completions.GetOrRegister(idx);
            }

            return replayTcs;
        }

        var requestBytes = SerializeObject(request);

        // BUG 1 FIX: Allocate dummy slot for invocation ID notification
        var invocationIdNotificationIdx = (uint)_journal.Count;
        _journal.Append(JournalEntry.Completed(JournalEntryType.Call, ReadOnlyMemory<byte>.Empty));
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = (uint)_journal.Count;
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister(entryIndex);

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
            var replayIndex = _journal.Count;
            await ReplayNextEntryAsync(JournalEntryType.Sleep, null, ct).ConfigureAwait(false);
            // During replay, the entry might be pending (not yet completed)
            return _completions.GetOrRegister(replayIndex);
        }

        var wakeUpTime = (ulong)DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Sleep));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        return tcs;
    }

    // ------- Attach / GetInvocationOutput -------

    public async ValueTask<TResponse> AttachInvocationAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.AttachInvocation, null, ct).ConfigureAwait(false);
            return Deserialize<TResponse>(replay.Result);
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.AttachInvocation));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.AttachInvocationCommand,
            ProtobufCodec.CreateAttachInvocationCommand(invocationId, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<TResponse>(completion.Value);
    }

    public async ValueTask<TResponse?> GetInvocationOutputAsync<TResponse>(string invocationId, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.GetInvocationOutput, null, ct)
                .ConfigureAwait(false);
            return replay.Result.IsEmpty ? default : Deserialize<TResponse>(replay.Result);
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.GetInvocationOutput));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.GetInvocationOutputCommand,
            ProtobufCodec.CreateGetInvocationOutputCommand(invocationId, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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

        if (_initialState is not null)
        {
            if (_initialState.TryGetValue(key, out var eager))
                return eager.Length > 0 ? Deserialize<T>(eager) : default;
            return default;
        }

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.GetState, key, ct).ConfigureAwait(false);
            return replay.Result.IsEmpty ? default : Deserialize<T>(replay.Result);
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.GetState, key));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.GetLazyStateCommand, ProtobufCodec.CreateGetStateCommand(key, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
        _initialState[key] = serialized.ToArray();
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

        _initialState?.Clear();
    }

    public async ValueTask<string[]> GetStateKeysAsync(CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.GetStateKeys, null, ct).ConfigureAwait(false);
            return Deserialize<string[]>(replay.Result) ?? [];
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.GetStateKeys));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.GetLazyStateKeysCommand, ProtobufCodec.CreateGetStateKeysCommand(completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
            await ReplayNextEntryAsync(JournalEntryType.Sleep, null, ct).ConfigureAwait(false);
            return;
        }

        var wakeUpTime = (ulong)DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Sleep));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
    /// </summary>
    private string BuildAwakeableId(int signalIndex)
    {
        var rawId = RawInvocationId;
        var bufferLength = rawId.Length + 4;
        Span<byte> buffer = bufferLength <= 256 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        rawId.CopyTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer[rawId.Length..], (uint)signalIndex);
        return $"sign_1{Convert.ToBase64String(buffer, Base64FormattingOptions.None).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.GetPromise, name, ct).ConfigureAwait(false);
            return Deserialize<T>(replay.Result);
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.GetPromise, name));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.GetPromiseCommand, ProtobufCodec.CreateGetPromiseCommand(name, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
        var completion = await tcs.Task.ConfigureAwait(false);
        completion.ThrowIfFailure();
        return Deserialize<T>(completion.Value);
    }

    public async ValueTask<T?> PeekPromiseAsync<T>(string name, CancellationToken ct)
    {
        EnsureActive();

        if (State == InvocationState.Replaying)
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.PeekPromise, name, ct).ConfigureAwait(false);
            return replay.Result.IsEmpty ? default : Deserialize<T>(replay.Result);
        }

        var completionId = (uint)_journal.Count;

        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.PeekPromise, name));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCommand(MessageType.PeekPromiseCommand, ProtobufCodec.CreatePeekPromiseCommand(name, completionId));

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
        var completionId = (uint)_journal.Count;

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

        var completionId = (uint)_journal.Count;

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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.Call, null, ct).ConfigureAwait(false);
            return Deserialize<TResponse>(replay.Result);
        }

        var requestBytes = Serialize(request);

        // BUG 1 FIX: Allocate dummy slot for invocation ID notification
        var invocationIdNotificationIdx = (uint)_journal.Count;
        _journal.Append(JournalEntry.Completed(JournalEntryType.Call, ReadOnlyMemory<byte>.Empty));
        _completions.GetOrRegister((int)invocationIdNotificationIdx);

        var completionId = (uint)_journal.Count;
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCallCommandMessage(service, handler, key, requestBytes, invocationIdNotificationIdx, completionId);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.OneWayCall, null, ct).ConfigureAwait(false);
            var replayId = replay.Result.IsEmpty ? "" : Encoding.UTF8.GetString(replay.Result.Span);
            return new InvocationHandle(replayId);
        }

        var requestBytes = Serialize(request);
        var invocationIdNotificationIdx = (uint)_journal.Count;

        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.OneWayCall));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteSendCommandMessage(service, handler, key, requestBytes, delay, idempotencyKey,
            invocationIdNotificationIdx);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
            AdvanceReplayIndex(JournalEntryType.SendSignal);
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
        {
            var replay = await ReplayNextEntryAsync(JournalEntryType.Call, null, ct).ConfigureAwait(false);
            return Deserialize<TResponse>(replay.Result);
        }

        var requestBytes = SerializeObject(request);

        // BUG 1 FIX: Allocate dummy slot for invocation ID notification (SDK ignores it for calls)
        var invocationIdNotificationIdx = (uint)_journal.Count;
        _journal.Append(JournalEntry.Completed(JournalEntryType.Call, ReadOnlyMemory<byte>.Empty));
        _completions.GetOrRegister((int)invocationIdNotificationIdx); // Register so CompletionManager doesn't complain

        var completionId = (uint)_journal.Count;
        // Register journal entry and TCS before flush to prevent race with incoming notifications.
        var entryIndex = _journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        var tcs = _completions.GetOrRegister(entryIndex);

        WriteCallCommandMessageWithOptions(service, handler, key, requestBytes, invocationIdNotificationIdx,
            completionId, idempotencyKey);

        await FlushAsync(ct).ConfigureAwait(false);

        Log.AwaitingCompletion(Logger, InvocationId, entryIndex);
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
        if (State == InvocationState.Closed)
            return;

        State = InvocationState.Closed;

        WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputFailure(code, message));

        _writer.WriteHeaderOnly(MessageType.End);
        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends a transient error as an ErrorMessage.
    ///     Restate treats this as retryable — the invocation will be retried.
    /// </summary>
    public async ValueTask FailAsync(ushort code, string message, CancellationToken ct)
    {
        if (State == InvocationState.Closed)
            return;

        State = InvocationState.Closed;

        WriteCommand(MessageType.Error, ProtobufCodec.CreateErrorMessage(code, message));

        _writer.WriteHeaderOnly(MessageType.End);
        await FlushAsync(ct).ConfigureAwait(false);
    }
}
