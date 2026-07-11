using System.Diagnostics.CodeAnalysis;

namespace Restate.Sdk;

/// <summary>
///     The settlement outcome of a single durable future awaited by <see cref="Context.AllSettled{T}" />.
///     Conveys either a successful value or the failure that faulted the future, without throwing.
/// </summary>
/// <remarks>
///     Only the <see cref="Success" /> and <see cref="Failure" /> factories produce meaningful
///     instances. A <c>default(SettledResult&lt;T&gt;)</c> (zero-initialized) value represents no
///     settlement at all: <see cref="IsSuccess" /> is <see langword="false" /> yet
///     <see cref="Error" /> is <see langword="null" />. The SDK never produces such a value.
/// </remarks>
/// <typeparam name="T">The result type of the underlying durable future.</typeparam>
public readonly record struct SettledResult<T>
{
    private SettledResult(bool isSuccess, T? value, Exception? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>True when the future completed successfully.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <summary>The successful result value, or <c>default</c> when <see cref="IsSuccess" /> is false.</summary>
    public T? Value { get; }

    /// <summary>
    ///     The failure that faulted the future, or <c>null</c> when <see cref="IsSuccess" /> is true.
    ///     Non-null for every factory-produced failed settlement (see the remarks on
    ///     <see cref="SettledResult{T}" /> for the <c>default</c>-value caveat).
    /// </summary>
    public Exception? Error { get; }

    /// <summary>Creates a successful settlement carrying the given value.</summary>
    public static SettledResult<T> Success(T value)
    {
        return new SettledResult<T>(true, value, null);
    }

    /// <summary>Creates a failed settlement carrying the given error.</summary>
    public static SettledResult<T> Failure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SettledResult<T>(false, default, error);
    }
}
