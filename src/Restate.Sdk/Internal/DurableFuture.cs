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
    // The resolve thunk: the state-machine park API (or, in tests, a TCS task). First GetResult
    // invokes and caches it — no bare CompletionManager TCS ever crosses the API boundary.
    private readonly Func<ValueTask<CompletionResult>>? _resolve;
    private Task<CompletionResult>? _resolved;

    internal DurableFuture(Func<ValueTask<CompletionResult>> resolve, JsonSerializerOptions jsonOptions,
        string? invocationId = null)
    {
        _resolve = resolve;
        _jsonOptions = jsonOptions;
        InvocationId = invocationId;
    }

    /// <summary>TCS-backed constructor retained for combinator tests; adapts the TCS to a thunk.</summary>
    internal DurableFuture(TaskCompletionSource<CompletionResult> tcs, JsonSerializerOptions jsonOptions,
        string? invocationId = null)
        : this(() => new ValueTask<CompletionResult>(tcs.Task), jsonOptions, invocationId)
    {
    }

    private DurableFuture(T value)
    {
        _completedValue = value;
        _isPreCompleted = true;
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult>? Task => _isPreCompleted ? null : (_resolved ??= _resolve!().AsTask());

    public string? InvocationId { get; }

    public async ValueTask<T> GetResult()
    {
        if (_isPreCompleted) return _completedValue!;
        var result = await (_resolved ??= _resolve!().AsTask()).ConfigureAwait(false);
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
    private readonly Func<ValueTask<CompletionResult>> _resolve;
    private Task<CompletionResult>? _resolved;

    internal VoidDurableFuture(Func<ValueTask<CompletionResult>> resolve)
    {
        _resolve = resolve;
    }

    /// <summary>TCS-backed constructor retained for combinator tests; adapts the TCS to a thunk.</summary>
    internal VoidDurableFuture(TaskCompletionSource<CompletionResult> tcs)
        : this(() => new ValueTask<CompletionResult>(tcs.Task))
    {
    }

    /// <summary>Internal access to the underlying task for combinator implementations.</summary>
    internal Task<CompletionResult> Task => _resolved ??= _resolve().AsTask();

    public string? InvocationId => null;

    public async ValueTask<bool> GetResult()
    {
        var result = await (_resolved ??= _resolve().AsTask()).ConfigureAwait(false);
        result.ThrowIfFailure();
        return true;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        await GetResult().ConfigureAwait(false);
        return null;
    }
}

/// <summary>
///     Run future: parks through the state-machine resolve thunk and returns the locally computed
///     value source via the notification (the ack barrier).
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class LazyRunFuture<T> : IDurableFuture<T>
{
    private readonly Func<ValueTask<CompletionResult>> _resolve;
    private readonly JsonSerializerOptions _jsonOptions;

    internal LazyRunFuture(Func<ValueTask<CompletionResult>> resolve, JsonSerializerOptions jsonOptions)
    {
        _resolve = resolve;
        _jsonOptions = jsonOptions;
    }

    public string? InvocationId => null;

    public async ValueTask<T> GetResult()
    {
        var result = await _resolve().ConfigureAwait(false);
        result.ThrowIfFailure();
        if (result.Value.IsEmpty) return default!;
        var reader = new Utf8JsonReader(result.Value.Span);
        return JsonSerializer.Deserialize<T>(ref reader, _jsonOptions)!;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }
}

/// <summary>
///     Timer (non-blocking sleep) future. Parks through the state-machine resolve thunk.
/// </summary>
internal sealed class LazyTimerFuture : IDurableFuture
{
    private readonly Func<ValueTask<CompletionResult>> _resolve;

    internal LazyTimerFuture(Func<ValueTask<CompletionResult>> resolve)
    {
        _resolve = resolve;
    }

    public async ValueTask<object?> GetResult()
    {
        var result = await _resolve().ConfigureAwait(false);
        result.ThrowIfFailure();
        return null;
    }
}

/// <summary>
///     CallFuture: parks through the state-machine resolve thunk and deserializes the response.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "JSON deserialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
internal sealed class LazyCallFuture<T> : IDurableFuture<T>
{
    private readonly Func<ValueTask<CompletionResult>> _resolve;
    private readonly JsonSerializerOptions _jsonOptions;

    internal LazyCallFuture(Func<ValueTask<CompletionResult>> resolve, JsonSerializerOptions jsonOptions)
    {
        _resolve = resolve;
        _jsonOptions = jsonOptions;
    }

    public string? InvocationId => null;

    public async ValueTask<T> GetResult()
    {
        var result = await _resolve().ConfigureAwait(false);
        result.ThrowIfFailure();
        var reader = new Utf8JsonReader(result.Value.Span);
        return JsonSerializer.Deserialize<T>(ref reader, _jsonOptions)!;
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }
}
