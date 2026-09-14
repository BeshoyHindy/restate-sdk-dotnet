using System.Threading.Tasks.Sources;

namespace Restate.Sdk.Tests;

/// <summary>
///     A <see cref="ValueTask{TResult}" /> backed by an <see cref="IValueTaskSource{TResult}" /> that
///     may be consumed exactly once: the source is reset as soon as its result is read, so a second
///     await of the same <see cref="ValueTask{TResult}" /> fails with an
///     <see cref="InvalidOperationException" /> for a stale token.
///     <para>
///         That is the behaviour <see cref="ValueTask{TResult}" /> documents and the pooling async
///         method builder produces (it recycles the box after the first await), so it is what code
///         holding a <see cref="ValueTask{TResult}" /> for later must be safe against.
///     </para>
/// </summary>
internal sealed class SingleUseValueTaskSource<T> : IValueTaskSource<T>
{
    private ManualResetValueTaskSourceCore<T> _core = new() { RunContinuationsAsynchronously = false };

    /// <summary>The single-use value task handed to the code under test.</summary>
    public ValueTask<T> ValueTask => new(this, _core.Version);

    T IValueTaskSource<T>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            // Mirrors the pooled builder returning its box: the token is now stale.
            _core.Reset();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token)
    {
        return _core.GetStatus(token);
    }

    void IValueTaskSource<T>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }

    public void SetResult(T result)
    {
        _core.SetResult(result);
    }

    public void SetException(Exception error)
    {
        _core.SetException(error);
    }
}
