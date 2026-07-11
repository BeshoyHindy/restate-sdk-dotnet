using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Restate.Sdk.Internal.Identity;

namespace Restate.Sdk.Tests.Identity;

public class JwtV1Tests
{
    private const string Audience = "/invoke/Greeter/Greet";
    private const long Now = 1_700_000_000;

    private readonly string _kid;
    private readonly Ed25519PrivateKeyParameters _privateKey;
    private readonly IdentityPublicKey[] _keys;

    public JwtV1Tests()
    {
        (_kid, _privateKey) = IdentityTestHelpers.CreateKeyPair();
        _keys = [ToIdentityKey(_kid, _privateKey)];
    }

    private static IdentityPublicKey ToIdentityKey(string kid, Ed25519PrivateKeyParameters privateKey)
    {
        return new IdentityPublicKey(Encoding.UTF8.GetBytes(kid), privateKey.GeneratePublicKey().GetEncoded());
    }

    private bool Validate(string token, string audience = Audience, long now = Now)
    {
        return JwtV1.TryValidate(Encoding.UTF8.GetBytes(token), _keys, audience, now);
    }

    [Fact]
    public void ValidToken_Succeeds()
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now);

        Assert.True(Validate(token));
    }

    [Fact]
    public void ExpiredToken_Fails()
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now - 120, lifetimeSeconds: 60);

        Assert.False(Validate(token));
    }

    [Fact]
    public void NotYetValidToken_Fails()
    {
        // nbf is one minute in the future.
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now + 60);

        Assert.False(Validate(token));
    }

    [Fact]
    public void WrongAudience_Fails()
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, "/invoke/Greeter/Other", Now);

        Assert.False(Validate(token));
    }

    [Fact]
    public void DiscoveryAudience_Succeeds()
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, "/discover", Now);

        Assert.True(Validate(token, audience: "/discover"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("ES256")]
    [InlineData("eddsa")]
    public void NonEdDsaAlgorithm_Fails(string algorithm)
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now, algorithm: algorithm);

        Assert.False(Validate(token));
    }

    [Fact]
    public void UnknownKid_Fails()
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, "publickeyv1_unknown", Audience, Now);

        Assert.False(Validate(token));
    }

    [Fact]
    public void MissingKid_Fails()
    {
        string payload = $$"""{"aud":"{{Audience}}","exp":{{Now + 60}},"iat":{{Now}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, """{"alg":"EdDSA","typ":"JWT"}""", payload);

        Assert.False(Validate(token));
    }

    [Fact]
    public void MissingAlg_Fails()
    {
        string payload = $$"""{"aud":"{{Audience}}","exp":{{Now + 60}},"iat":{{Now}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, $$"""{"typ":"JWT","kid":"{{_kid}}"}""", payload);

        Assert.False(Validate(token));
    }

    [Fact]
    public void SignedByWrongKey_Fails()
    {
        // Header names our kid, but the signature comes from a different private key.
        (_, Ed25519PrivateKeyParameters otherKey) = IdentityTestHelpers.CreateKeyPair();
        string token = IdentityTestHelpers.CreateJwt(otherKey, _kid, Audience, Now);

        Assert.False(Validate(token));
    }

    [Fact]
    public void SecondConfiguredKey_Succeeds()
    {
        (string otherKid, Ed25519PrivateKeyParameters otherKey) = IdentityTestHelpers.CreateKeyPair();
        IdentityPublicKey[] keys = [.. _keys, ToIdentityKey(otherKid, otherKey)];
        string token = IdentityTestHelpers.CreateJwt(otherKey, otherKid, Audience, Now);

        Assert.True(JwtV1.TryValidate(Encoding.UTF8.GetBytes(token), keys, Audience, Now));
    }

    [Theory]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("aud")]
    public void MissingRequiredClaim_Fails(string omittedClaim)
    {
        var claims = new List<string>();
        if (omittedClaim != "aud")
        {
            claims.Add($"\"aud\":\"{Audience}\"");
        }

        if (omittedClaim != "exp")
        {
            claims.Add($"\"exp\":{Now + 60}");
        }

        if (omittedClaim != "nbf")
        {
            claims.Add($"\"nbf\":{Now}");
        }

        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, "{" + string.Join(",", claims) + "}");

        Assert.False(Validate(token));
    }

    [Theory]
    [InlineData("\"soon\"")] // non-numeric
    [InlineData("1700000060.5")] // non-integer
    [InlineData("null")]
    public void NonIntegerExp_Fails(string expJson)
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string payload = $$"""{"aud":"{{Audience}}","exp":{{expJson}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.False(Validate(token));
    }

    [Fact]
    public void AudienceAsSingleElementArray_Succeeds()
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string payload = $$"""{"aud":["{{Audience}}"],"exp":{{Now + 60}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.True(Validate(token));
    }

    [Fact]
    public void AudienceAsMultiElementArray_Fails()
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string payload = $$"""{"aud":["{{Audience}}","/other"],"exp":{{Now + 60}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.False(Validate(token));
    }

    [Fact]
    public void AudienceAsNumber_Fails()
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string payload = $$"""{"aud":42,"exp":{{Now + 60}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.False(Validate(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("onlyonesegment")]
    [InlineData("two.segments")]
    [InlineData("a.b.c.d")] // four segments
    public void WrongSegmentCount_Fails(string token)
    {
        Assert.False(Validate(token));
    }

    [Theory]
    [InlineData(0)] // header
    [InlineData(1)] // payload
    [InlineData(2)] // signature
    public void MalformedBase64UrlSegment_Fails(int segmentIndex)
    {
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now);
        string[] segments = token.Split('.');
        segments[segmentIndex] = "!not-base64url!";

        Assert.False(Validate(string.Join(".", segments)));
    }

    [Fact]
    public void StandardBase64Padding_Fails()
    {
        // Appending padding changes the signed bytes (and misaligns the segment),
        // so a padded variant of a valid token must be rejected.
        string token = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now);
        string[] segments = token.Split('.');
        segments[1] += "==";

        Assert.False(Validate(string.Join(".", segments)));
    }

    [Fact]
    public void TamperedPayload_Fails()
    {
        // Re-encode the payload with an extended expiry but keep the original signature.
        string good = IdentityTestHelpers.CreateJwt(_privateKey, _kid, Audience, Now);
        string forgedPayload = System.Buffers.Text.Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($$"""{"aud":"{{Audience}}","exp":{{Now + 999_999}},"nbf":{{Now}}}"""));
        string[] segments = good.Split('.');
        string forged = segments[0] + "." + forgedPayload + "." + segments[2];

        Assert.False(Validate(forged));
    }

    [Fact]
    public void HeaderNotAJsonObject_Fails()
    {
        string payload = $$"""{"aud":"{{Audience}}","exp":{{Now + 60}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, "[1,2]", payload);

        Assert.False(Validate(token));
    }

    [Fact]
    public void PayloadNotAJsonObject_Fails()
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, "\"claims\"");

        Assert.False(Validate(token));
    }

    [Fact]
    public void UnknownClaimsAndNestedObjects_AreIgnored()
    {
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}","extra":{"nested":[1,2,3]} }""";
        string payload =
            $$"""{"sub":"x","aud":"{{Audience}}","exp":{{Now + 60}},"iat":{{Now}},"nbf":{{Now}},"meta":{"a":[{"b":1}]} }""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.True(Validate(token));
    }

    [Fact]
    public void ExactBoundaryTimes_Succeed()
    {
        // now == nbf and now == exp are both accepted (closed interval).
        string header = $$"""{"alg":"EdDSA","typ":"JWT","kid":"{{_kid}}"}""";
        string payload = $$"""{"aud":"{{Audience}}","exp":{{Now}},"nbf":{{Now}}}""";
        string token = IdentityTestHelpers.CreateJwt(_privateKey, header, payload);

        Assert.True(Validate(token));
    }
}
