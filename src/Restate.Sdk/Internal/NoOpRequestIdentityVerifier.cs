namespace Restate.Sdk.Internal;

/// <summary>
///     Default <see cref="IRequestIdentityVerifier" /> that accepts every request. Active unless the
///     application registers a real verifier (e.g. via <c>Restate.Sdk.Identity</c>). Mirrors
///     shared-core's behavior when no public keys are configured.
/// </summary>
internal sealed class NoOpRequestIdentityVerifier : IRequestIdentityVerifier
{
    public static readonly NoOpRequestIdentityVerifier Instance = new();

    public RequestIdentityResult Verify(Func<string, string?> headerLookup, string path) =>
        RequestIdentityResult.Verified;
}
