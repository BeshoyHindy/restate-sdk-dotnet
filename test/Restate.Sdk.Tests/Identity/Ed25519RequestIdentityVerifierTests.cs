using Restate.Sdk;
using Restate.Sdk.Identity;

namespace Restate.Sdk.Tests.Identity;

public class Ed25519RequestIdentityVerifierTests
{
    private const string InvokePath = "/invoke/Greeter/Greet";

    private static Func<string, string?> Headers(string? scheme, string? jwt)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (scheme is not null) map["x-restate-signature-scheme"] = scheme;
        if (jwt is not null) map["x-restate-jwt-v1"] = jwt;
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void ValidSignedJwt_IsVerified()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, InvokePath);

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.True(result.IsVerified);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void SignedByDifferentKey_IsRejected()
    {
        var (_, attackerKey) = RestateIdentityTestTokens.GenerateKey();
        var (trustedPublicKey, _) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(trustedPublicKey);
        var jwt = RestateIdentityTestTokens.MintJwt(attackerKey, InvokePath);

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void MultipleKeys_VerifiesAgainstAny()
    {
        var (key1, _) = RestateIdentityTestTokens.GenerateKey();
        var (key2, priv2) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(key1, key2);
        var jwt = RestateIdentityTestTokens.MintJwt(priv2, InvokePath);

        Assert.True(verifier.Verify(Headers("v1", jwt), InvokePath).IsVerified);
    }

    [Fact]
    public void UnsignedScheme_IsRejected()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);

        var result = verifier.Verify(Headers("unsigned", null), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("unsigned", result.RejectionReason);
    }

    [Fact]
    public void MissingSchemeHeader_IsRejected()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);

        var result = verifier.Verify(Headers(null, null), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("x-restate-signature-scheme", result.RejectionReason);
    }

    [Fact]
    public void MissingJwtHeader_IsRejected()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);

        var result = verifier.Verify(Headers("v1", null), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("x-restate-jwt-v1", result.RejectionReason);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, InvokePath, exp: past, nbf: past - 10, iat: past - 10);

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("expired", result.RejectionReason);
    }

    [Fact]
    public void NotYetValidToken_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, InvokePath, nbf: future);

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("not yet valid", result.RejectionReason);
    }

    [Fact]
    public void WrongAudience_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, "/invoke/Other/Handler");

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("audience", result.RejectionReason);
    }

    [Fact]
    public void MissingRequiredClaims_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, InvokePath, includeAllClaims: false);

        var result = verifier.Verify(Headers("v1", jwt), InvokePath);

        Assert.False(result.IsVerified);
        Assert.Contains("required claim", result.RejectionReason);
    }

    [Fact]
    public void TamperedSignature_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        var verifier = new Ed25519RequestIdentityVerifier(publicKey);
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, InvokePath);
        // Flip the last character of the signature segment.
        var tampered = jwt[..^1] + (jwt[^1] == 'A' ? 'B' : 'A');

        Assert.False(verifier.Verify(Headers("v1", tampered), InvokePath).IsVerified);
    }

    [Theory]
    [InlineData("publickeyv1")]            // missing trailing underscore + payload
    [InlineData("notaprefix_abc")]         // wrong prefix
    public void MalformedKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => new Ed25519RequestIdentityVerifier(key));
    }

    [Fact]
    public void KeyWithWrongLength_Throws()
    {
        // Valid prefix + valid base58 that decodes to far fewer than 32 bytes.
        Assert.Throws<ArgumentException>(() => new Ed25519RequestIdentityVerifier("publickeyv1_2g"));
    }

    [Theory]
    [InlineData("/invoke/Greeter/Greet", "/invoke/Greeter/Greet")]
    [InlineData("/base/path/invoke/Greeter/Greet", "/invoke/Greeter/Greet")]
    [InlineData("/discover", "/discover")]
    [InlineData("/some/prefix/discover", "/discover")]
    [InlineData("/health", "/health")]
    public void NormalisePath_ReducesToSignedAudience(string input, string expected)
    {
        Assert.Equal(expected, Ed25519RequestIdentityVerifier.NormalisePath(input));
    }
}
