namespace Restate.Sdk;

/// <summary>
///     The settled outcome of a single durable future returned by
///     <see cref="Context.AllSettled{T}" />. Mirrors the shape of a JavaScript
///     <c>Promise.allSettled</c> result: exactly one of <see cref="Value" /> or
///     <see cref="Error" /> is meaningful, selected by <see cref="IsSuccess" />.
/// </summary>
/// <typeparam name="T">The future's result type.</typeparam>
public readonly record struct DurableSettled<T>
{
    private DurableSettled(bool isSuccess, T? value, Exception? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>True when the future completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>The successful result, or <c>default</c> when <see cref="IsSuccess" /> is false.</summary>
    public T? Value { get; }

    /// <summary>The failure, or <c>null</c> when <see cref="IsSuccess" /> is true.</summary>
    public Exception? Error { get; }

    /// <summary>Creates a successful settled result.</summary>
    public static DurableSettled<T> Success(T value) => new(true, value, null);

    /// <summary>Creates a failed settled result.</summary>
    public static DurableSettled<T> Failure(Exception error) => new(false, default, error);
}
