namespace Restate.Sdk;

/// <summary>
///     The settlement outcome of a single durable future awaited by <see cref="Context.AllSettled{T}" />.
///     Conveys either a successful value or the failure that faulted the future, without throwing.
/// </summary>
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
    public bool IsSuccess { get; }

    /// <summary>The successful result value, or <c>default</c> when <see cref="IsSuccess" /> is false.</summary>
    public T? Value { get; }

    /// <summary>The failure that faulted the future, or <c>null</c> when <see cref="IsSuccess" /> is true.</summary>
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
