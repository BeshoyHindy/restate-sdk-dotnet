using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Restate.Sdk.Internal.Journal;

namespace Restate.Sdk.Internal;

[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class DurableFuture<T> : IDurableFuture<T>
{
    private readonly T? _completedValue;
    private readonly bool _isPreCompleted;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly TaskCompletionSource<CompletionResult>? _tcs;

    internal DurableFuture(TaskCompletionSource<CompletionResult> tcs, JsonSerializerOptions jsonOptions,
        string? invocationId = null)
    {
        _tcs = tcs;
        _jsonOptions = jsonOptions;
        InvocationId = invocationId;
    }

    private DurableFuture(T value)
    {
        _completedValue = value;
        _isPreCompleted = true;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult>? Task => _tcs?.Task;

    public string? InvocationId { get; }

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
internal sealed class VoidDurableFuture : IDurableFuture<bool>, IDurableFuture
{
    private static readonly VoidDurableFuture CachedCompleted = CreateCompleted();
    private readonly TaskCompletionSource<CompletionResult> _tcs;

    internal VoidDurableFuture(TaskCompletionSource<CompletionResult> tcs)
    {
        _tcs = tcs;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult> Task => _tcs.Task;

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
internal sealed class LazyRunFuture<T> : IDurableFuture<T>
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
internal sealed class LazyTimerFuture : IDurableFuture
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

    public async ValueTask<object?> GetResult()
    {
        var tcs = await _initTask.ConfigureAwait(false);
        var result = await tcs.Task.ConfigureAwait(false);
        result.ThrowIfFailure();
        return null;
    }
}

/// <summary>
///     Lazy wrapper for CallFuture operations where the state machine setup is async.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class LazyCallFuture<T> : IDurableFuture<T>
{
    // Preserved at construction: a ValueTask may only be awaited once, but a future can be handed
    // to two combinators or awaited again by the handler. Preserve() materialises the underlying
    // Task when the operation is still in flight (the async state machine already allocated it,
    // so this costs nothing extra) and is a no-op for one that completed synchronously.
    private readonly ValueTask<TaskCompletionSource<CompletionResult>> _initTask;
    private readonly JsonSerializerOptions _jsonOptions;

    internal LazyCallFuture(ValueTask<TaskCompletionSource<CompletionResult>> initTask,
        JsonSerializerOptions jsonOptions)
    {
        _initTask = initTask.Preserve();
        _jsonOptions = jsonOptions;
    }

    public string? InvocationId => null;

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