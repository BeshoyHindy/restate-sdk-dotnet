namespace Restate.Sdk.Internal.Journal;

internal readonly struct CompletionResult
{
    public ReadOnlyMemory<byte> Value { get; }
    public string? StringValue { get; }
    public ushort? FailureCode { get; }
    public string? FailureMessage { get; }
    public bool IsSuccess => FailureCode is null;
    public bool IsFailure => FailureCode is not null;

    private CompletionResult(ReadOnlyMemory<byte> value, string? stringValue, ushort? failureCode,
        string? failureMessage)
    {
        Value = value;
        StringValue = stringValue;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
    }

    public static CompletionResult Success(ReadOnlyMemory<byte> value)
    {
        return new CompletionResult(value, null, null, null);
    }

    public static CompletionResult SuccessString(string value)
    {
        return new CompletionResult(ReadOnlyMemory<byte>.Empty, value, null, null);
    }

    public static CompletionResult Failure(ushort code, string message)
    {
        return new CompletionResult(ReadOnlyMemory<byte>.Empty, null, code, message);
    }

    /// <summary>The <see cref="TerminalException" /> a failed result stands for, or null for a success.</summary>
    public TerminalException? FailureException =>
        IsFailure ? new TerminalException(FailureMessage ?? "Unknown error", FailureCode!.Value) : null;

    public void ThrowIfFailure()
    {
        if (FailureException is { } failure)
            throw failure;
    }
}