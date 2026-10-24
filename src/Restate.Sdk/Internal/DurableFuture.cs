using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal;

/// <summary>
///     A durable future that knows where the runtime's delivery of its result sits in the
///     invocation's completion order (see <see cref="ArrivalClock" />). The combinators rank
///     already-settled futures by it, so a replayed invocation takes the branch the original
///     attempt took instead of the one argument order happens to favour.
/// </summary>
internal interface IArrivalOrderedFuture
{
    /// <summary>The arrival position of the delivered result, or 0 when none was delivered.</summary>
    long ArrivalOrder { get; }
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

    internal VoidDurableFuture(TaskCompletionSource<CompletionResult> tcs)
    {
        _tcs = tcs;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult> Task => _tcs.Task;

    public long ArrivalOrder => (_tcs as CompletionSource)?.ArrivalOrder ?? 0;

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

    internal LazyRunFuture(ValueTask<(TaskCompletionSource<CompletionResult> Tcs, T Result)> initTask)
    {
        _initTask = initTask.Preserve();
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result.Tcs as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

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

    internal LazyTimerFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask)
    {
        _initTask = initTask.Preserve();
    }

    // The wait only exists once the operation was set up; until then nothing can have arrived.
    public long ArrivalOrder => _initTask.IsCompletedSuccessfully
        ? (_initTask.Result as CompletionSource)?.ArrivalOrder ?? 0
        : 0;

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