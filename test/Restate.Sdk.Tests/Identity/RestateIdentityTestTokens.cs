using System.Buffers.Text;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using SimpleBase;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Test helper that mints Ed25519 key pairs and Restate-style signed JWTs, mirroring how the
///     Restate runtime signs requests, so request-identity verification can be tested end-to-end.
/// </summary>
internal static class RestateIdentityTestTokens
{
    public static (string PublicKeyV1, Ed25519PrivateKeyParameters PrivateKey) GenerateKey()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = (Ed25519PublicKeyParameters)pair.Public;
        return ("publickeyv1_" + Base58Encode(publicKey.GetEncoded()), privateKey);
    }

    /// <summary>Mints a signed JWT. Defaults: valid now, expiring in one hour.</summary>
    public static string MintJwt(
        Ed25519PrivateKeyParameters privateKey,
        string audience,
        long? exp = null,
        long? nbf = null,
        long? iat = null,
        bool includeAllClaims = true)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        exp ??= now + 3600;
        nbf ??= now - 5;
        iat ??= now - 5;

        var header = Base64UrlString(Encoding.UTF8.GetBytes("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}"));
        var payload = includeAllClaims
            ? $"{{\"aud\":\"{audience}\",\"exp\":{exp},\"iat\":{iat},\"nbf\":{nbf}}}"
            : $"{{\"aud\":\"{audience}\",\"exp\":{exp}}}"; // missing iat/nbf
        var payloadB64 = Base64UrlString(Encoding.UTF8.GetBytes(payload));

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payloadB64}");
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(signingInput, 0, signingInput.Length);
        var signature = signer.GenerateSignature();

        return $"{header}.{payloadB64}.{Base64UrlString(signature)}";
    }

    private static string Base64UrlString(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static string Base58Encode(byte[] data) => Base58.Bitcoin.Encode(data);
}
