using System.Buffers.Text;
using System.Numerics;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Test-side signing helpers. The SDK ships verify-only Ed25519, so tests use
///     BouncyCastle (test-only dependency) to generate keys and sign tokens.
/// </summary>
internal static class IdentityTestHelpers
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>Generates an Ed25519 keypair and its Restate serialized form (<c>publickeyv1_...</c>).</summary>
    public static (string SerializedKey, Ed25519PrivateKeyParameters PrivateKey) CreateKeyPair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();
        return ("publickeyv1_" + Base58Encode(publicKey), privateKey);
    }

    /// <summary>Signs <paramref name="message" /> with Ed25519 (RFC 8032).</summary>
    public static byte[] Sign(Ed25519PrivateKeyParameters privateKey, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Builds a compact JWT from raw header/payload JSON, signed by <paramref name="privateKey" />.</summary>
    public static string CreateJwt(Ed25519PrivateKeyParameters privateKey, string headerJson, string payloadJson)
    {
        string signingInput =
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson)) + "." +
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        byte[] signature = Sign(privateKey, Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>Builds a well-formed Restate v1 identity JWT.</summary>
    public static string CreateJwt(
        Ed25519PrivateKeyParameters privateKey,
        string kid,
        string audience,
        long nowUnixSeconds,
        long lifetimeSeconds = 60,
        string algorithm = "EdDSA")
    {
        string headerJson = $$"""{"alg":"{{algorithm}}","typ":"JWT","kid":"{{kid}}"}""";
        string payloadJson =
            $$"""{"aud":"{{audience}}","exp":{{nowUnixSeconds + lifetimeSeconds}},"iat":{{nowUnixSeconds}},"nbf":{{nowUnixSeconds}}}""";
        return CreateJwt(privateKey, headerJson, payloadJson);
    }

    /// <summary>Base58btc (Bitcoin alphabet) encoder — encode side lives only in tests.</summary>
    public static string Base58Encode(ReadOnlySpan<byte> data)
    {
        int leadingZeros = 0;
        while (leadingZeros < data.Length && data[leadingZeros] == 0)
        {
            leadingZeros++;
        }

        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var builder = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out BigInteger remainder);
            builder.Insert(0, Base58Alphabet[(int)remainder]);
        }

        return new string('1', leadingZeros) + builder;
    }
}
