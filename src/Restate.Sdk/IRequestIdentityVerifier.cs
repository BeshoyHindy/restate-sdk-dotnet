namespace Restate.Sdk;

/// <summary>
///     Verifies that an incoming Restate request (discovery or invocation) genuinely originates from
///     the trusted Restate runtime, using request-identity signatures.
/// </summary>
/// <remarks>
///     This is a boundary abstraction: the core SDK ships a permissive default
///     (no verification) and resolves whichever implementation is registered. The Ed25519/JWT
///     implementation lives in the <c>Restate.Sdk.Identity</c> package so that the core SDK — and
///     Native AOT consumers — take no cryptography dependency unless request identity is enabled.
/// </remarks>
public interface IRequestIdentityVerifier
{
    /// <summary>
    ///     Verifies the request identity for the given request.
    /// </summary>
    /// <param name="headerLookup">
    ///     Case-insensitive header accessor returning the header value, or <c>null</c> if absent.
    /// </param>
    /// <param name="path">The full request path (e.g. <c>/invoke/Svc/Handler</c> or <c>/discover</c>).</param>
    /// <returns>A <see cref="RequestIdentityResult" /> indicating acceptance or rejection.</returns>
    RequestIdentityResult Verify(Func<string, string?> headerLookup, string path);
}

/// <summary>
///     The outcome of an <see cref="IRequestIdentityVerifier.Verify" /> call. Either verified, or
///     rejected with a human-readable reason — never a bare boolean.
/// </summary>
public readonly record struct RequestIdentityResult
{
    private RequestIdentityResult(bool isVerified, string? rejectionReason)
    {
        IsVerified = isVerified;
        RejectionReason = rejectionReason;
    }

    /// <summary>True when the request identity was accepted (or no verification is configured).</summary>
    public bool IsVerified { get; }

    /// <summary>The reason the request was rejected, or <c>null</c> when verified.</summary>
    public string? RejectionReason { get; }

    /// <summary>A successful verification result.</summary>
    public static RequestIdentityResult Verified { get; } = new(true, null);

    /// <summary>Creates a rejection with the given reason.</summary>
    public static RequestIdentityResult Reject(string reason) => new(false, reason);
}
