using System.Collections;
using Microsoft.Extensions.Logging;

namespace Restate.Sdk.Internal;

/// <summary>
///     Wraps an <see cref="ILogger" /> and suppresses output while the invocation is replaying
///     journal entries — the same discipline as <see cref="DurableConsole" />. This prevents
///     duplicate log lines when a handler is re-executed to rebuild its state after a restart.
///     Checks the live replay state on each call so output resumes once replay completes.
/// </summary>
internal sealed class ReplayAwareLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly Func<bool> _isReplaying;

    public ReplayAwareLogger(ILogger inner, Func<bool> isReplaying)
    {
        _inner = inner;
        _isReplaying = isReplaying;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return !_isReplaying() && _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>
///     Structured logging scope carrying the invocation ID, created once per invocation and
///     spanning the handler execution. Implements <see cref="IReadOnlyList{T}" /> of
///     key/value pairs so structured logging providers surface <c>InvocationId</c> as a field.
/// </summary>
internal sealed class InvocationLogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly string _invocationId;
    private string? _cachedToString;

    public InvocationLogScope(string invocationId)
    {
        _invocationId = invocationId;
    }

    public KeyValuePair<string, object?> this[int index] =>
        index == 0
            ? new KeyValuePair<string, object?>("InvocationId", _invocationId)
            : throw new ArgumentOutOfRangeException(nameof(index));

    public int Count => 1;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return this[0];
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override string ToString()
    {
        return _cachedToString ??= $"InvocationId:{_invocationId}";
    }
}
