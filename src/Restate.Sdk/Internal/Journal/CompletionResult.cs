namespace Restate.Sdk.Internal.Journal;

internal readonly struct CompletionResult
{
    public ReadOnlyMemory<byte> Value { get; }
    public string? StringValue { get; }
    public ushort? FailureCode { get; }
    public string? FailureMessage { get; }

    /// <summary>
    ///     V6 terminal-error <c>Failure.metadata</c> parsed off an incoming failure notification, or
    ///     null when there is none. Surfaced onto the re-raised <see cref="TerminalException" />.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FailureMetadata { get; }

    public bool IsSuccess => FailureCode is null;
    public bool IsFailure => FailureCode is not null;

    private CompletionResult(ReadOnlyMemory<byte> value, string? stringValue, ushort? failureCode,
        string? failureMessage, IReadOnlyDictionary<string, string>? failureMetadata)
    {
        Value = value;
        StringValue = stringValue;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
        FailureMetadata = failureMetadata;
    }

    public static CompletionResult Success(ReadOnlyMemory<byte> value)
    {
        return new CompletionResult(value, null, null, null, null);
    }

    public static CompletionResult SuccessString(string value)
    {
        return new CompletionResult(ReadOnlyMemory<byte>.Empty, value, null, null, null);
    }

    public static CompletionResult Failure(ushort code, string message,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new CompletionResult(ReadOnlyMemory<byte>.Empty, null, code, message, metadata);
    }

    public void ThrowIfFailure()
    {
        if (IsFailure)
            throw new TerminalException(FailureMessage ?? "Unknown error", FailureCode!.Value, FailureMetadata);
    }
}
