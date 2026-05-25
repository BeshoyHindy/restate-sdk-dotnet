using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SimpleBase;

namespace Restate.Sdk.Identity;

/// <summary>
///     Verifies Restate request-identity signatures (scheme <c>v1</c>) using Ed25519/JWT, mirroring
///     the algorithm in <c>restate-sdk-shared-core</c>'s <c>request_identity</c> module. Pure-managed:
///     uses BouncyCastle for the Ed25519 check and <see cref="System.Buffers.Text.Base64Url" /> /
///     <see cref="System.Text.Json.JsonDocument" /> for JWT decoding — no native dependency.
/// </summary>
public sealed class Ed25519RequestIdentityVerifier : IRequestIdentityVerifier
{
    private const string SignatureSchemeHeader = "x-restate-signature-scheme";
    private const string SignatureSchemeV1 = "v1";
    private const string SignatureSchemeUnsigned = "unsigned";
    private const string SignatureJwtV1Header = "x-restate-jwt-v1";
    private const string IdentityV1Prefix = "publickeyv1_";

    private readonly byte[][] _publicKeys;

    /// <summary>
    ///     Creates a verifier trusting the given <c>publickeyv1_&lt;base58&gt;</c> public keys. A request
    ///     is accepted if its JWT validates against <em>any</em> configured key.
    /// </summary>
    /// <exception cref="ArgumentException">A key is malformed (missing prefix, bad base58, or not 32 bytes).</exception>
    public Ed25519RequestIdentityVerifier(params string[] publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        _publicKeys = new byte[publicKeys.Length][];
        for (var i = 0; i < publicKeys.Length; i++)
            _publicKeys[i] = ParseKey(publicKeys[i]);
    }

    /// <inheritdoc />
    public RequestIdentityResult Verify(Func<string, string?> headerLookup, string path)
    {
        ArgumentNullException.ThrowIfNull(headerLookup);

        // No keys configured behaves as pass-through (matches shared-core); in practice this verifier
        // is only registered when keys are supplied.
        if (_publicKeys.Length == 0)
            return RequestIdentityResult.Verified;

        var scheme = headerLookup(SignatureSchemeHeader);
        if (scheme is null)
            return RequestIdentityResult.Reject($"missing header: {SignatureSchemeHeader}");

        switch (scheme)
        {
            case SignatureSchemeV1:
                var jwt = headerLookup(SignatureJwtV1Header);
                if (jwt is null)
                    return RequestIdentityResult.Reject($"missing header: {SignatureJwtV1Header}");
                return VerifyJwt(jwt, NormalisePath(path));

            case SignatureSchemeUnsigned:
                return RequestIdentityResult.Reject(
                    "got unsigned request, expecting only signed requests matching the configured keys");

            default:
                return RequestIdentityResult.Reject(
                    $"bad {SignatureSchemeHeader} header, unexpected value {scheme}");
        }
    }

    private RequestIdentityResult VerifyJwt(string jwt, string audience)
    {
        var firstDot = jwt.IndexOf('.');
        var lastDot = jwt.LastIndexOf('.');
        if (firstDot <= 0 || lastDot <= firstDot)
            return RequestIdentityResult.Reject("invalid JWT: expected three segments");

        var headerSegment = jwt.AsSpan(0, firstDot);
        var payloadSegment = jwt.AsSpan(firstDot + 1, lastDot - firstDot - 1);
        var signatureSegment = jwt.AsSpan(lastDot + 1);

        byte[] headerJson, payloadJson, signature;
        try
        {
            headerJson = Base64Url.DecodeFromChars(headerSegment);
            payloadJson = Base64Url.DecodeFromChars(payloadSegment);
            signature = Base64Url.DecodeFromChars(signatureSegment);
        }
        catch (FormatException)
        {
            return RequestIdentityResult.Reject("invalid JWT: malformed base64url");
        }

        if (!IsEdDsaHeader(headerJson))
            return RequestIdentityResult.Reject("invalid JWT: expected alg EdDSA");

        var claimsResult = ValidateClaims(payloadJson, audience);
        if (!claimsResult.IsVerified)
            return claimsResult;

        // The Ed25519 signature is computed over the ASCII signing input "<header>.<payload>".
        var signingInput = Encoding.ASCII.GetBytes(jwt[..lastDot]);
        return VerifySignature(signingInput, signature)
            ? RequestIdentityResult.Verified
            : RequestIdentityResult.Reject("invalid JWT: signature does not match any configured key");
    }

    private static bool IsEdDsaHeader(byte[] headerJson)
    {
        using var doc = JsonDocument.Parse(headerJson);
        return doc.RootElement.TryGetProperty("alg", out var alg)
               && alg.ValueKind == JsonValueKind.String
               && alg.GetString() == "EdDSA";
    }

    private static RequestIdentityResult ValidateClaims(byte[] payloadJson, string audience)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        // All four claims are required (shared-core: required_spec_claims = aud, exp, iat, nbf).
        if (!root.TryGetProperty("aud", out var aud)
            || !root.TryGetProperty("exp", out var exp)
            || !root.TryGetProperty("iat", out _)
            || !root.TryGetProperty("nbf", out var nbf))
            return RequestIdentityResult.Reject("invalid JWT: missing required claim (aud/exp/iat/nbf)");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // leeway = 0
        if (!exp.TryGetInt64(out var expSeconds) || now >= expSeconds)
            return RequestIdentityResult.Reject("invalid JWT: token expired");

        if (!nbf.TryGetInt64(out var nbfSeconds) || now < nbfSeconds)
            return RequestIdentityResult.Reject("invalid JWT: token not yet valid");

        return AudienceMatches(aud, audience)
            ? RequestIdentityResult.Verified
            : RequestIdentityResult.Reject($"invalid JWT: audience mismatch (expected {audience})");
    }

    private static bool AudienceMatches(JsonElement aud, string audience)
    {
        switch (aud.ValueKind)
        {
            case JsonValueKind.String:
                return aud.GetString() == audience;
            case JsonValueKind.Array:
                foreach (var entry in aud.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() == audience)
                        return true;
                return false;
            default:
                return false;
        }
    }

    private bool VerifySignature(byte[] signingInput, byte[] signature)
    {
        // Ed25519 signatures are exactly 64 bytes; reject early to avoid BouncyCastle exceptions.
        if (signature.Length != 64)
            return false;

        foreach (var key in _publicKeys)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            verifier.BlockUpdate(signingInput, 0, signingInput.Length);
            if (verifier.VerifySignature(signature))
                return true;
        }

        return false;
    }

    private static byte[] ParseKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.StartsWith(IdentityV1Prefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"identity v1 jwt public keys are expected to start with {IdentityV1Prefix}", nameof(key));

        byte[] decoded;
        try
        {
            // Bitcoin alphabet matches the `bs58` crate default used by the Restate runtime.
            decoded = Base58.Bitcoin.Decode(key.AsSpan(IdentityV1Prefix.Length));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"cannot decode the public key with base58: {ex.Message}", nameof(key), ex);
        }

        if (decoded.Length != 32)
            throw new ArgumentException($"decoded key should have length of 32, was {decoded.Length}", nameof(key));

        return decoded;
    }

    /// <summary>
    ///     Reduces a request path to the audience the Restate runtime signs: the trailing
    ///     <c>/invoke/&lt;service&gt;/&lt;handler&gt;</c> or <c>/discover</c> segment, ignoring any base-path prefix.
    /// </summary>
    internal static string NormalisePath(string path)
    {
        var slashes = new List<int>(8);
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '/')
                slashes.Add(i);

        if (slashes.Count >= 3)
        {
            var thirdFromLast = slashes[^3];
            var secondFromLast = slashes[^2];
            if (path.AsSpan(thirdFromLast, secondFromLast - thirdFromLast).SequenceEqual("/invoke"))
                return path[thirdFromLast..];
        }

        if (slashes.Count >= 1 && path.AsSpan(slashes[^1]).SequenceEqual("/discover"))
            return path[slashes[^1]..];

        return path;
    }
}
