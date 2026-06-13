using Restate.Sdk.Internal.Journal;

namespace Restate.Sdk.Tests.Journal;

public sealed class CompletionResultTests
{
    [Fact]
    public void Success_IsSuccess_NotFailure_ThrowIfFailureNoOps()
    {
        var result = CompletionResult.Success(new byte[] { 1, 2, 3 });

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        result.ThrowIfFailure();   // success → no throw
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Value.ToArray());
    }

    [Fact]
    public void SuccessString_CarriesStringValue_AndIsSuccess()
    {
        var result = CompletionResult.SuccessString("hello");

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.StringValue);
        result.ThrowIfFailure();
    }

    [Fact]
    public void Failure_WithMessage_ThrowsTerminalWithMessageAndCode()
    {
        var result = CompletionResult.Failure(409, "Conflict");

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        var ex = Assert.Throws<TerminalException>(result.ThrowIfFailure);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public void Failure_WithNullMessage_FallsBackToUnknownError()
    {
        // CompletionResult.cs:39 — the `FailureMessage ?? "Unknown error"` null-coalescing fallback.
        // A failure whose message is null (a malformed/empty Failure frame on the wire) must still
        // surface a TerminalException with a non-null message; the fallback is the defense.
        var result = CompletionResult.Failure(500, null!);

        var ex = Assert.Throws<TerminalException>(result.ThrowIfFailure);
        Assert.Equal("Unknown error", ex.Message);
        Assert.Equal(500, ex.Code);
    }
}
