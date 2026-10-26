using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal;

/// <summary>
///     A durable future that exposes the runtime's delivery of its result before anything is
///     deserialized, and where that delivery sits in the invocation's completion order (see
///     <see cref="ArrivalClock" />). The combinators park on <see cref="Completion" />, rank the
///     inputs that settled by <see cref="ArrivalOrder" /> and deserialize only the input they
///     hand back, so a replayed invocation takes the branch the original attempt took instead of
///     the one argument order or the thread pool happens to favour.
/// </summary>
internal interface IArrivalOrderedFuture
{
    /// <summary>The arrival position of the delivered result, or 0 when none was delivered.</summary>
    long ArrivalOrder { get; }

    /// <summary>The runtime's delivery for this future, before any deserialization.</summary>
    RawCompletion Completion { get; }
}

/// <summary>
///     The runtime's delivery for one durable future, before any deserialization: whether it
///     happened, the failure it carried, and a wait the combinators park on until it does.
///     <para>
///         <see cref="IsSettled" /> and <see cref="Failure" /> read the delivery itself, never
///         the wait. A wait resumes on the thread pool some time after the delivery it tracks, so
///         a combinator woken by one input could otherwise miss a result the runtime delivered
///         earlier whose wait had not resumed yet, and hand back the later delivery. Reading the
///         deliveries directly lets every wake-up see everything the runtime has delivered so far
///         and rank it by arrival position.
///     </para>
///     <para>
///         <see cref="Settled" /> never faults. It awaits the delivery and keeps nothing, so a
///         branch a combinator abandons still has its fault observed and never surfaces as
///         <see cref="TaskScheduler.UnobservedTaskException" /> at collection.
///     </para>
///     <para>
///         One instance per future, created on first use and shared by every combinator the
///         future is handed to.
///     </para>
/// </summary>
internal abstract class RawCompletion
{
    private readonly Lock _failureLock = new();
    private Exception? _failure;
    private bool _failureRead;

    private RawCompletion()
    {
    }

    /// <summary>A delivery that already happened and carried a value: a future resolved locally.</summary>
    internal static RawCompletion Resolved { get; } = new ResolvedCompletion();

    /// <summary>The wait to park on; completes once the delivery happened, and never faults.</summary>
    internal abstract Task Settled { get; }

    /// <summary>Whether the delivery already happened; read from the delivery, not from <see cref="Settled" />.</summary>
    internal abstract bool IsSettled { get; }

    /// <summary>
    ///     The failure the delivery carried, or null when it carried a value or has not happened
    ///     yet. Reading it deserializes nothing, and every read returns the same instance, also
    ///     when two first reads race on different threads.
    /// </summary>
    internal Exception? Failure
    {
        get
        {
            if (Volatile.Read(ref _failureRead)) return _failure;
            if (!IsSettled) return null;

            // Read once under a lock: a combinator woken by the delivery and an await it resumed
            // can both land here first, and each must hand back the one object the other saw.
            lock (_failureLock)
            {
                if (!_failureRead)
                {
                    _failure = ReadFailure();
                    Volatile.Write(ref _failureRead, true);
                }

                return _failure;
            }
        }
    }

    /// <summary>Tracks a wait the runtime completes with a result or a failure.</summary>
    internal static RawCompletion OfDelivery(Task<CompletionResult> delivery)
    {
        return new DeliveredCompletion(delivery);
    }

    /// <summary>Tracks a delivery whose wait only exists once an asynchronous setup completes.</summary>
    internal static RawCompletion OfDelivery(ValueTask<TaskCompletionSource<CompletionResult>> setup)
    {
        return new SetUpCompletion(setup);
    }

    /// <summary>
    ///     Tracks any other wait that settles the future: the setup of a side effect, which
    ///     carries its own result, or the decoded result of a future the SDK did not create.
    /// </summary>
    internal static RawCompletion OfWait(Task wait)
    {
        return new AwaitedCompletion(wait);
    }

    /// <summary>The failure of a delivery <see cref="IsSettled" /> reports as happened.</summary>
    protected abstract Exception? ReadFailure();

    /// <summary>What awaiting the completed task would throw: its fault, a cancellation, or nothing.</summary>
    private static Exception? FaultOf(Task task)
    {
        if (task.IsCanceled)
            return new TaskCanceledException(task);
        return task.IsFaulted ? task.Exception!.InnerException ?? task.Exception : null;
    }

    /// <summary>The fault of a delivered wait, or the failure the result it delivered carries.</summary>
    private static Exception? FailureOf(Task<CompletionResult> delivery)
    {
        return FaultOf(delivery) ?? delivery.Result.FailureException;
    }

    /// <summary>Awaits <paramref name="wait" /> without ever faulting.</summary>
    private static async Task Observe(Task wait)
    {
        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The failure is read from the delivery itself; awaiting it here only marks it observed.
        }
    }

    private sealed class ResolvedCompletion : RawCompletion
    {
        internal override Task Settled => Task.CompletedTask;

        internal override bool IsSettled => true;

        protected override Exception? ReadFailure()
        {
            return null;
        }
    }

    private sealed class DeliveredCompletion(Task<CompletionResult> delivery) : RawCompletion
    {
        private Task? _settled;

        internal override Task Settled => _settled ??= Observe(delivery);

        internal override bool IsSettled => delivery.IsCompleted;

        protected override Exception? ReadFailure()
        {
            return FailureOf(delivery);
        }
    }

    private sealed class SetUpCompletion(ValueTask<TaskCompletionSource<CompletionResult>> setup) : RawCompletion
    {
        private Task? _settled;

        internal override Task Settled => _settled ??= Observe(Deliver());

        // Settled once the setup failed, or once the wait it produced completed.
        internal override bool IsSettled =>
            setup.IsCompleted && (!setup.IsCompletedSuccessfully || setup.Result.Task.IsCompleted);

        protected override Exception? ReadFailure()
        {
            return setup.IsCompletedSuccessfully ? FailureOf(setup.Result.Task) : FaultOf(setup.AsTask());
        }

        private async Task Deliver()
        {
            var tcs = await setup.ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
        }
    }

    private sealed class AwaitedCompletion(Task wait) : RawCompletion
    {
        private Task? _settled;

        internal override Task Settled => _settled ??= Observe(wait);

        internal override bool IsSettled => wait.IsCompleted;

        protected override Exception? ReadFailure()
        {
            return FaultOf(wait);
        }
    }
}

/// <summary>
///     Where a future started by a call reads the invocation id of that call: the completion slot
///     the runtime delivers the invocation-id notification to. Reading is a peek — it neither
///     blocks nor registers a wait, so the slot stays out of the <c>SuspensionMessage</c> — and
///     yields null until the notification arrives. The default value belongs to futures that
///     started no invocation (timers, runs, signals) and always reads as null.
/// </summary>
internal readonly struct InvocationIdSlot
{
    private readonly InvocationStateMachine? _stateMachine;
    private readonly uint _notificationIndex;

    internal InvocationIdSlot(InvocationStateMachine stateMachine, uint notificationIndex)
    {
        _stateMachine = stateMachine;
        _notificationIndex = notificationIndex;
    }

    /// <summary>The invocation id the runtime delivered, or null while it has not arrived.</summary>
    internal string? Read()
    {
        return _stateMachine?.TryReadInvocationId(_notificationIndex);
    }
}

[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class DurableFuture<T> : IDurableFuture<T>, IArrivalOrderedFuture
{
    private readonly T? _completedValue;
    private readonly Lazy<Task<T>> _decoded;
    private readonly InvocationIdSlot _invocationId;
    private readonly bool _isPreCompleted;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly ISerde<T>? _serde;
    private readonly TaskCompletionSource<CompletionResult>? _tcs;
    private RawCompletion? _completion;

    /// <param name="tcs">The wait the runtime delivers this future's result to.</param>
    /// <param name="jsonOptions">The options the payload is decoded with when there is no serde.</param>
    /// <param name="invocationId">The slot the id of the invocation this future started arrives in.</param>
    /// <param name="serde">The serde the caller brought for the payload, or null to decode it as JSON.</param>
    internal DurableFuture(TaskCompletionSource<CompletionResult> tcs, JsonSerializerOptions jsonOptions,
        InvocationIdSlot invocationId = default, ISerde<T>? serde = null)
    {
        _tcs = tcs;
        _jsonOptions = jsonOptions;
        _invocationId = invocationId;
        _serde = serde;
        _decoded = new Lazy<Task<T>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private DurableFuture(T value)
    {
        _completedValue = value;
        _isPreCompleted = true;
        _decoded = new Lazy<Task<T>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult>? Task => _tcs?.Task;

    public long ArrivalOrder => (_tcs as CompletionSource)?.ArrivalOrder ?? 0;

    public RawCompletion Completion => _completion ??=
        _isPreCompleted ? RawCompletion.Resolved : RawCompletion.OfDelivery(_tcs!.Task);

    public string? InvocationId => _invocationId.Read();

    public ValueTask<T> GetResult()
    {
        if (_isPreCompleted) return new ValueTask<T>(_completedValue!);

        // Decoded on the first await and kept: a future handed to two combinators, or awaited
        // again by the handler, observes the same value and the same failure every time. The
        // memo runs the decode once even when two first awaits race on different threads, so
        // both of them hand back the one object it produced.
        return new ValueTask<T>(_decoded.Value);
    }

    private async Task<T> Decode()
    {
        var result = await _tcs!.Task.ConfigureAwait(false);
        // Thrown from the delivery's own failure rather than built here, so the exception a
        // combinator reports for this input is the one an await of it throws.
        if (Completion.Failure is { } failure)
            throw failure;
        if (_serde is not null)
            return _serde.Deserialize(new ReadOnlySequence<byte>(result.Value));
        // A handler without a return value completes with an empty payload, which is not a
        // JSON document; decode it as default like the synchronous call paths do.
        if (result.Value.IsEmpty)
            return default!;
        var reader = new Utf8JsonReader(result.Value.Span);
        return JsonSerializer.Deserialize<T>(ref reader, _jsonOptions!)!;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates a future that is already completed with the given value.
    ///     Used during replay when the journal entry is already resolved.
    /// </summary>
    internal static DurableFuture<T> Completed(T value)
    {
        return new DurableFuture<T>(value);
    }
}

/// <summary>
///     A non-generic void future for operations that complete without a value (e.g., Sleep/Timer).
/// </summary>
internal sealed class VoidDurableFuture : IDurableFuture<bool>, IDurableFuture, IArrivalOrderedFuture
{
    private static readonly VoidDurableFuture CachedCompleted = CreateCompleted();
    private readonly Lazy<Task<bool>> _decoded;
    private readonly TaskCompletionSource<CompletionResult> _tcs;
    private RawCompletion? _completion;

    internal VoidDurableFuture(TaskCompletionSource<CompletionResult> tcs)
    {
        _tcs = tcs;
        _decoded = new Lazy<Task<bool>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult> Task => _tcs.Task;

    public long ArrivalOrder => (_tcs as CompletionSource)?.ArrivalOrder ?? 0;

    public RawCompletion Completion => _completion ??= RawCompletion.OfDelivery(_tcs.Task);

    public string? InvocationId => null;

    public ValueTask<bool> GetResult()
    {
        // Nothing to decode, but the failure a delivery carries is built on every read, so the
        // outcome is kept here too: every await throws the same exception instance.
        return new ValueTask<bool>(_decoded.Value);
    }

    private async Task<bool> Decode()
    {
        await _tcs.Task.ConfigureAwait(false);
        if (Completion.Failure is { } failure)
            throw failure;
        return true;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        await GetResult().ConfigureAwait(false);
        return null;
    }

    internal static VoidDurableFuture Completed()
    {
        return CachedCompleted;
    }

    private static VoidDurableFuture CreateCompleted()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));
        return new VoidDurableFuture(tcs);
    }
}

/// <summary>
///     Lazy wrapper for RunAsync futures where the state machine operation itself is async.
///     Awaits the state machine's ValueTask, then provides the result as a completed future.
/// </summary>
internal sealed class LazyRunFuture<T> : IDurableFuture<T>, IArrivalOrderedFuture
{
    // Preserved at construction: a ValueTask may only be awaited once, but a future can be handed
    // to two combinators or awaited again by the handler. Preserve() materialises the underlying
    // Task when the operation is still in flight (the async state machine already allocated it,
    // so this costs nothing extra) and is a no-op for one that completed synchronously.
    private readonly Lazy<Task<T>> _decoded;
    private readonly ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> _initTask;
    private RawCompletion? _completion;

    internal LazyRunFuture(ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> initTask)
    {
        _initTask = initTask.Preserve();
        _decoded = new Lazy<Task<T>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result.Tcs as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

    // The side effect carries its own result, so the setup completing is the whole delivery.
    public RawCompletion Completion => _completion ??= RawCompletion.OfWait(_initTask.AsTask());

    public string? InvocationId => null;

    public ValueTask<T> GetResult()
    {
        // Kept on the first await, so both combinators a future is handed to, and the handler
        // awaiting it afterwards, see the one object the side effect produced.
        return new ValueTask<T>(_decoded.Value);
    }

    private async Task<T> Decode()
    {
        var (_, result) = await _initTask.ConfigureAwait(false);
        return result;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }
}

/// <summary>
///     Lazy wrapper for Timer (non-blocking sleep) futures.
/// </summary>
internal sealed class LazyTimerFuture : IDurableFuture, IArrivalOrderedFuture
{
    // Preserved at construction: a ValueTask may only be awaited once, but a future can be handed
    // to two combinators or awaited again by the handler. Preserve() materialises the underlying
    // Task when the operation is still in flight (the async state machine already allocated it,
    // so this costs nothing extra) and is a no-op for one that completed synchronously.
    private readonly Lazy<Task<object?>> _decoded;
    private readonly ValueTask<TaskCompletionSource<CompletionResult>> _initTask;
    private RawCompletion? _completion;

    internal LazyTimerFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask)
    {
        _initTask = initTask.Preserve();
        _decoded = new Lazy<Task<object?>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

    public RawCompletion Completion => _completion ??= RawCompletion.OfDelivery(_initTask);

    public ValueTask<object?> GetResult()
    {
        // Nothing to decode, but the failure a delivery carries is built on every read, so the
        // outcome is kept here too: every await throws the same exception instance.
        return new ValueTask<object?>(_decoded.Value);
    }

    private async Task<object?> Decode()
    {
        var tcs = await _initTask.ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
        if (Completion.Failure is { } failure)
            throw failure;
        return null;
    }
}

/// <summary>
///     Lazy wrapper for a future whose result arrives as a runtime notification and whose state
///     machine setup is async: a call future and a signal, both of which flush the buffered
///     commands before the wait they hand back can resolve.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class LazyNotificationFuture<T> : IDurableFuture<T>, IArrivalOrderedFuture
{
    // Preserved at construction: a ValueTask may only be awaited once, but a future can be handed
    // to two combinators or awaited again by the handler. Preserve() materialises the underlying
    // Task when the operation is still in flight (the async state machine already allocated it,
    // so this costs nothing extra) and is a no-op for one that completed synchronously.
    private readonly Lazy<Task<T>> _decoded;
    private readonly ValueTask<TaskCompletionSource<CompletionResult>> _initTask;
    private readonly InvocationIdSlot _invocationId;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ISerde<T>? _serde;
    private RawCompletion? _completion;

    /// <param name="initTask">The setup that produces the wait the runtime delivers the result to.</param>
    /// <param name="jsonOptions">The options the payload is decoded with when there is no serde.</param>
    /// <param name="invocationId">The slot the id of the invocation this future started arrives in.</param>
    /// <param name="serde">The serde the caller brought for the payload, or null to decode it as JSON.</param>
    internal LazyNotificationFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask,
        JsonSerializerOptions jsonOptions, InvocationIdSlot invocationId = default, ISerde<T>? serde = null)
    {
        _initTask = initTask.Preserve();
        _jsonOptions = jsonOptions;
        _invocationId = invocationId;
        _serde = serde;
        _decoded = new Lazy<Task<T>>(Decode, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

    public RawCompletion Completion => _completion ??= RawCompletion.OfDelivery(_initTask);

    // Read from the completion slot, not from the pending setup: the slot index is known before
    // the command is flushed, so a lazily created call future reports the same id an eagerly
    // created one does. Null for a signal future, which started no invocation.
    public string? InvocationId => _invocationId.Read();

    public ValueTask<T> GetResult()
    {
        // Decoded on the first await and kept, as in DurableFuture<T>.
        return new ValueTask<T>(_decoded.Value);
    }

    private async Task<T> Decode()
    {
        var tcs = await _initTask.ConfigureAwait(false);
        var result = await tcs.Task.ConfigureAwait(false);
        if (Completion.Failure is { } failure)
            throw failure;
        if (_serde is not null)
            return _serde.Deserialize(new ReadOnlySequence<byte>(result.Value));
        // A handler without a return value completes with an empty payload, which is not a
        // JSON document; decode it as default like the synchronous call paths do.
        if (result.Value.IsEmpty)
            return default!;
        var reader = new Utf8JsonReader(result.Value.Span);
        return JsonSerializer.Deserialize<T>(ref reader, _jsonOptions)!;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }
}

/// <summary>
///     Hands out a durable future's result as a <see cref="ValueTask{T}" /> that touches the
///     future only once it is awaited: an awakeable exposes both its future and a value, and
///     asking the future for its result starts the decode of the payload, so a value nobody
///     awaits must not deserialize anything when the runtime delivers the result.
///     <para>
///         The decode is shared with the future itself, so awaiting both the value and the future
///         still decodes once and observes the same value.
///     </para>
/// </summary>
internal sealed class DeferredFutureValue<T> : IValueTaskSource<T>
{
    private readonly Lazy<Task<T>> _awaited;

    internal DeferredFutureValue(IDurableFuture<T> future)
    {
        _awaited = new Lazy<Task<T>>(() => future.GetResult().AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        var awaited = _awaited.Value;
        if (!awaited.IsCompleted) return ValueTaskSourceStatus.Pending;
        if (awaited.IsCanceled) return ValueTaskSourceStatus.Canceled;
        return awaited.IsFaulted ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Succeeded;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        // Registered on the decode itself with the capture the awaiter asked for, so awaiting the
        // value behaves exactly as awaiting the future it stands for.
        var awaiter = _awaited.Value
            .ConfigureAwait((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            .GetAwaiter();
        if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            awaiter.OnCompleted(() => continuation(state));
        else
            awaiter.UnsafeOnCompleted(() => continuation(state));
    }

    public T GetResult(short token)
    {
        return _awaited.Value.GetAwaiter().GetResult();
    }

    /// <summary>The value task to hand out; it asks the future for its result on the first await.</summary>
    internal ValueTask<T> AsValueTask()
    {
        return new ValueTask<T>(this, 0);
    }
}
