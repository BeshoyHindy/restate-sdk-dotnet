using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
///     The runtime's delivery for one durable future, before any deserialization: a wait the
///     combinators park on, plus the failure the delivery carried, if any.
///     <para>
///         <see cref="Settled" /> never faults. It awaits the underlying wait and keeps whatever
///         it threw, so a branch a combinator abandons still has its fault observed and never
///         surfaces as <see cref="TaskScheduler.UnobservedTaskException" /> at collection.
///     </para>
///     <para>
///         One instance per future, created on first use and shared by every combinator the
///         future is handed to.
///     </para>
/// </summary>
internal sealed class RawCompletion
{
    private RawCompletion()
    {
    }

    /// <summary>A delivery that is already settled with a value: a future resolved locally.</summary>
    internal static RawCompletion Resolved { get; } = new();

    /// <summary>The wait to park on; completes once the delivery happened, and never faults.</summary>
    internal Task Settled { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the delivery already happened.</summary>
    internal bool IsSettled => Settled.IsCompleted;

    /// <summary>
    ///     The failure the delivery carried, or null when it carried a value. Only meaningful
    ///     once <see cref="IsSettled" /> is true. Reading it deserializes nothing.
    /// </summary>
    internal Exception? Failure { get; private set; }

    /// <summary>Tracks a wait the runtime completes with a result or a failure.</summary>
    internal static RawCompletion OfDelivery(Task<CompletionResult> delivery)
    {
        var completion = new RawCompletion();
        completion.Settled = completion.CaptureDelivery(delivery);
        return completion;
    }

    /// <summary>Tracks a delivery whose wait only exists once an asynchronous setup completes.</summary>
    internal static RawCompletion OfDelivery(ValueTask<TaskCompletionSource<CompletionResult>> setup)
    {
        var completion = new RawCompletion();
        completion.Settled = completion.CaptureSetup(setup);
        return completion;
    }

    /// <summary>
    ///     Tracks any other wait that settles the future: the setup of a side effect, which
    ///     carries its own result, or the decoded result of a future the SDK did not create.
    /// </summary>
    internal static RawCompletion OfWait(Task wait)
    {
        var completion = new RawCompletion();
        completion.Settled = completion.CaptureWait(wait);
        return completion;
    }

    private async Task CaptureDelivery(Task<CompletionResult> delivery)
    {
        try
        {
            (await delivery.ConfigureAwait(false)).ThrowIfFailure();
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
    }

    private async Task CaptureSetup(ValueTask<TaskCompletionSource<CompletionResult>> setup)
    {
        try
        {
            var tcs = await setup.ConfigureAwait(false);
            (await tcs.Task.ConfigureAwait(false)).ThrowIfFailure();
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
    }

    private async Task CaptureWait(Task wait)
    {
        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure = ex;
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
    private readonly InvocationIdSlot _invocationId;
    private readonly bool _isPreCompleted;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly TaskCompletionSource<CompletionResult>? _tcs;
    private RawCompletion? _completion;

    internal DurableFuture(TaskCompletionSource<CompletionResult> tcs, JsonSerializerOptions jsonOptions,
        InvocationIdSlot invocationId = default)
    {
        _tcs = tcs;
        _jsonOptions = jsonOptions;
        _invocationId = invocationId;
    }

    private DurableFuture(T value)
    {
        _completedValue = value;
        _isPreCompleted = true;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult>? Task => _tcs?.Task;

    public long ArrivalOrder => (_tcs as CompletionSource)?.ArrivalOrder ?? 0;

    public RawCompletion Completion => _completion ??=
        _isPreCompleted ? RawCompletion.Resolved : RawCompletion.OfDelivery(_tcs!.Task);

    public string? InvocationId => _invocationId.Read();

    public async ValueTask<T> GetResult()
    {
        if (_isPreCompleted) return _completedValue!;
        var result = await _tcs!.Task.ConfigureAwait(false);
        result.ThrowIfFailure();
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
    private readonly TaskCompletionSource<CompletionResult> _tcs;
    private RawCompletion? _completion;

    internal VoidDurableFuture(TaskCompletionSource<CompletionResult> tcs)
    {
        _tcs = tcs;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult> Task => _tcs.Task;

    public long ArrivalOrder => (_tcs as CompletionSource)?.ArrivalOrder ?? 0;

    public RawCompletion Completion => _completion ??= RawCompletion.OfDelivery(_tcs.Task);

    public string? InvocationId => null;

    public async ValueTask<bool> GetResult()
    {
        var result = await _tcs.Task.ConfigureAwait(false);
        result.ThrowIfFailure();
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
    private readonly ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> _initTask;
    private RawCompletion? _completion;

    internal LazyRunFuture(ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> initTask)
    {
        _initTask = initTask.Preserve();
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result.Tcs as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

    // The side effect carries its own result, so the setup completing is the whole delivery.
    public RawCompletion Completion => _completion ??= RawCompletion.OfWait(_initTask.AsTask());

    public string? InvocationId => null;

    public async ValueTask<T> GetResult()
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
    private readonly ValueTask<TaskCompletionSource<CompletionResult>> _initTask;
    private RawCompletion? _completion;

    internal LazyTimerFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask)
    {
        _initTask = initTask.Preserve();
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

    public RawCompletion Completion => _completion ??= RawCompletion.OfDelivery(_initTask);

    public async ValueTask<object?> GetResult()
    {
        var tcs = await _initTask.ConfigureAwait(false);
        var result = await tcs.Task.ConfigureAwait(false);
        result.ThrowIfFailure();
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
    private readonly ValueTask<TaskCompletionSource<CompletionResult>> _initTask;
    private readonly InvocationIdSlot _invocationId;
    private readonly JsonSerializerOptions _jsonOptions;
    private RawCompletion? _completion;

    internal LazyNotificationFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask,
        JsonSerializerOptions jsonOptions, InvocationIdSlot invocationId = default)
    {
        _initTask = initTask.Preserve();
        _jsonOptions = jsonOptions;
        _invocationId = invocationId;
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

    public async ValueTask<T> GetResult()
    {
        var tcs = await _initTask.ConfigureAwait(false);
        var result = await tcs.Task.ConfigureAwait(false);
        result.ThrowIfFailure();
        var reader = new Utf8JsonReader(result.Value.Span);
        return JsonSerializer.Deserialize<T>(ref reader, _jsonOptions)!;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }
}