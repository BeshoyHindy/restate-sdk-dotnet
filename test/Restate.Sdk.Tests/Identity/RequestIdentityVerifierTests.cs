using Org.BouncyCastle.Crypto.Parameters;
using Restate.Sdk.Internal.Identity;

namespace Restate.Sdk.Tests.Identity;

public class RequestIdentityVerifierTests
{
    private const string Audience = "/invoke/Greeter/Greet";

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly string _serializedKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;
    private readonly RequestIdentityVerifier _verifier;

    public RequestIdentityVerifierTests()
    {
        (_serializedKey, _privateKey) = IdentityTestHelpers.CreateKeyPair();
        _verifier = RequestIdentityVerifier.FromKeys([_serializedKey], new FixedTimeProvider(FixedNow));
    }

    private string CreateToken(long? now = null)
    {
        return IdentityTestHelpers.CreateJwt(
            _privateKey, _serializedKey, Audience, now ?? FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void FromKeys_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RequestIdentityVerifier.FromKeys(null!));
    }

    [Fact]
    public void FromKeys_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => RequestIdentityVerifier.FromKeys([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("publickeyv2_4YnYYmZC8Ee3kbUZ85DTvNoxNbLPGXjyGkNyH9x4jLxT")] // wrong prefix
    [InlineData("4YnYYmZC8Ee3kbUZ85DTvNoxNbLPGXjyGkNyH9x4jLxT")] // no prefix
    [InlineData("publickeyv1_")] // empty body
    [InlineData("publickeyv1_0OIl")] // invalid base58 characters
    [InlineData("publickeyv1_abc")] // decodes to fewer than 32 bytes
    public void FromKeys_MalformedKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => RequestIdentityVerifier.FromKeys([key]));
    }

    [Fact]
    public void FromKeys_WrongDecodedLength_Throws()
    {
        string key = "publickeyv1_" + IdentityTestHelpers.Base58Encode(new byte[31]);

        Assert.Throws<ArgumentException>(() => RequestIdentityVerifier.FromKeys([key]));
    }

    [Fact]
    public void Verify_ValidRequest_Succeeds()
    {
        Assert.True(_verifier.Verify("v1", CreateToken(), Audience));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v2")]
    [InlineData("V1")] // scheme comparison is case-sensitive
    [InlineData(" v1")]
    public void Verify_NonV1Scheme_Fails(string? scheme)
    {
        Assert.False(_verifier.Verify(scheme, CreateToken(), Audience));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void Verify_MissingOrGarbageToken_Fails(string? token)
    {
        Assert.False(_verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_OversizedToken_Fails()
    {
        string token = CreateToken() + new string('A', 10_000);

        Assert.False(_verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_WrongPath_Fails()
    {
        Assert.False(_verifier.Verify("v1", CreateToken(), "/invoke/Greeter/Other"));
    }

    [Fact]
    public void Verify_ExpiredToken_Fails()
    {
        // Token minted two minutes ago with a 60s lifetime.
        string token = CreateToken(FixedNow.ToUnixTimeSeconds() - 120);

        Assert.False(_verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_TokenFromTheFuture_Fails()
    {
        string token = CreateToken(FixedNow.ToUnixTimeSeconds() + 60);

        Assert.False(_verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_WrongKey_Fails()
    {
        // A valid token from a different (unconfigured) keypair, presented under our kid's scheme.
        (string otherSerialized, Ed25519PrivateKeyParameters otherKey) = IdentityTestHelpers.CreateKeyPair();
        string token = IdentityTestHelpers.CreateJwt(
            otherKey, otherSerialized, Audience, FixedNow.ToUnixTimeSeconds());

        Assert.False(_verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_SecondConfiguredKey_Succeeds()
    {
        (string otherSerialized, Ed25519PrivateKeyParameters otherKey) = IdentityTestHelpers.CreateKeyPair();
        var verifier = RequestIdentityVerifier.FromKeys(
            [_serializedKey, otherSerialized], new FixedTimeProvider(FixedNow));
        string token = IdentityTestHelpers.CreateJwt(
            otherKey, otherSerialized, Audience, FixedNow.ToUnixTimeSeconds());

        Assert.True(verifier.Verify("v1", token, Audience));
    }

    [Fact]
    public void Verify_RealClockDefault_Succeeds()
    {
        // FromKeys without a TimeProvider uses the system clock.
        var verifier = RequestIdentityVerifier.FromKeys([_serializedKey]);
        string token = IdentityTestHelpers.CreateJwt(
            _privateKey, _serializedKey, Audience, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lifetimeSeconds: 300);

        Assert.True(verifier.Verify("v1", token, Audience));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
