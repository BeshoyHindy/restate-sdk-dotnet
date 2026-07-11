using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Ed25519Verifier = Restate.Sdk.Internal.Identity.Ed25519.Ed25519;

namespace Restate.Sdk.Internal.Identity;

/// <summary>
///     Compact-JWT parsing and validation for the Restate <c>v1</c> request identity scheme.
///     Reflection-free (<see cref="Utf8JsonReader" /> + <see cref="Base64Url" />), so AOT- and trim-safe.
/// </summary>
internal static class JwtV1
{
    /// <summary>
    ///     Validates a compact JWT (<c>base64url(header).base64url(payload).base64url(signature)</c>):
    ///     header must carry <c>alg=EdDSA</c> and a <c>kid</c> matching a configured key, the Ed25519
    ///     signature must verify, and the payload must carry <c>exp</c>/<c>nbf</c> bracketing
    ///     <paramref name="nowUnixSeconds" /> plus an <c>aud</c> equal to <paramref name="expectedAudience" />.
    ///     Never throws.
    /// </summary>
    /// <param name="token">The UTF-8 bytes of the compact JWT.</param>
    /// <param name="keys">The configured identity keys.</param>
    /// <param name="expectedAudience">The exact request path the token must be scoped to.</param>
    /// <param name="nowUnixSeconds">The current time as Unix seconds.</param>
    /// <returns><see langword="true" /> if the token is valid; otherwise <see langword="false" />.</returns>
    public static bool TryValidate(
        ReadOnlySpan<byte> token,
        IdentityPublicKey[] keys,
        string expectedAudience,
        long nowUnixSeconds)
    {
        // Split into exactly three non-empty dot-separated segments.
        int firstDot = token.IndexOf((byte)'.');
        if (firstDot <= 0)
        {
            return false;
        }

        int secondDot = token[(firstDot + 1)..].IndexOf((byte)'.');
        if (secondDot <= 0)
        {
            return false;
        }

        secondDot += firstDot + 1;

        ReadOnlySpan<byte> headerSegment = token[..firstDot];
        ReadOnlySpan<byte> payloadSegment = token[(firstDot + 1)..secondDot];
        ReadOnlySpan<byte> signatureSegment = token[(secondDot + 1)..];
        if (signatureSegment.IsEmpty || signatureSegment.Contains((byte)'.'))
        {
            return false;
        }

        // Signature must decode to exactly 64 bytes.
        Span<byte> signature = stackalloc byte[Ed25519Verifier.SignatureSize];
        if (!TryDecodeSegmentExact(signatureSegment, signature))
        {
            return false;
        }

        // Header: alg must be EdDSA and kid must resolve to a configured key.
        byte[]? publicKey = DecodeAndParseHeader(headerSegment, keys);
        if (publicKey is null)
        {
            return false;
        }

        // Ed25519 signature over the ASCII bytes of "base64url(header).base64url(payload)" —
        // exactly the token prefix up to the second dot.
        if (!Ed25519Verifier.Verify(signature, token[..secondDot], publicKey))
        {
            return false;
        }

        // Claims: exp/nbf required and bracketing now, aud must equal the request path.
        return DecodeAndValidateClaims(payloadSegment, expectedAudience, nowUnixSeconds);
    }

    /// <summary>Decodes a base64url segment that must fill <paramref name="destination" /> exactly.</summary>
    private static bool TryDecodeSegmentExact(ReadOnlySpan<byte> segment, Span<byte> destination)
    {
        OperationStatus status = Base64Url.DecodeFromUtf8(segment, destination, out int consumed, out int written);
        return status == OperationStatus.Done && consumed == segment.Length && written == destination.Length;
    }

    private static byte[]? DecodeAndParseHeader(ReadOnlySpan<byte> headerSegment, IdentityPublicKey[] keys)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Base64Url.GetMaxDecodedLength(headerSegment.Length));
        try
        {
            OperationStatus status = Base64Url.DecodeFromUtf8(headerSegment, rented, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != headerSegment.Length)
            {
                return null;
            }

            return ParseHeader(rented.AsSpan(0, written), keys);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Parses the JOSE header and returns the public key matching its <c>kid</c>,
    ///     or <see langword="null" /> if the header is malformed, <c>alg</c> is not
    ///     <c>EdDSA</c>, or the <c>kid</c> is unknown.
    /// </summary>
    private static byte[]? ParseHeader(ReadOnlySpan<byte> headerJson, IdentityPublicKey[] keys)
    {
        try
        {
            Utf8JsonReader reader = new(headerJson);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            bool algValid = false;
            byte[]? matchedKey = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("alg"u8))
                {
                    // Every occurrence must be exactly "EdDSA" — "none", "HS256", etc. are rejected.
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("EdDSA"u8))
                    {
                        return null;
                    }

                    algValid = true;
                }
                else if (reader.ValueTextEquals("kid"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return null;
                    }

                    matchedKey = null;
                    foreach (IdentityPublicKey key in keys)
                    {
                        if (reader.ValueTextEquals(key.KidUtf8))
                        {
                            matchedKey = key.PublicKey;
                            break;
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return null; // Malformed object or trailing content.
            }

            return algValid ? matchedKey : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool DecodeAndValidateClaims(ReadOnlySpan<byte> payloadSegment, string expectedAudience, long nowUnixSeconds)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Base64Url.GetMaxDecodedLength(payloadSegment.Length));
        try
        {
            OperationStatus status = Base64Url.DecodeFromUtf8(payloadSegment, rented, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != payloadSegment.Length)
            {
                return false;
            }

            return ValidateClaims(rented.AsSpan(0, written), expectedAudience, nowUnixSeconds);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Validates the claims set: <c>exp</c> and <c>nbf</c> must be present, numeric, and satisfy
    ///     <c>nbf &lt;= now &lt;= exp</c>; <c>aud</c> (string or single-element array) must equal
    ///     <paramref name="expectedAudience" />.
    /// </summary>
    private static bool ValidateClaims(ReadOnlySpan<byte> payloadJson, string expectedAudience, long nowUnixSeconds)
    {
        try
        {
            Utf8JsonReader reader = new(payloadJson);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            bool hasExp = false;
            bool hasNbf = false;
            bool audValid = false;
            long exp = 0;
            long nbf = 0;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("exp"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out exp))
                    {
                        return false;
                    }

                    hasExp = true;
                }
                else if (reader.ValueTextEquals("nbf"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out nbf))
                    {
                        return false;
                    }

                    hasNbf = true;
                }
                else if (reader.ValueTextEquals("aud"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        audValid = reader.ValueTextEquals(expectedAudience);
                    }
                    else if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        // The server signs a single audience; only single-element arrays are accepted.
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                        {
                            return false;
                        }

                        audValid = reader.ValueTextEquals(expectedAudience);
                        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return false; // Malformed object or trailing content.
            }

            return hasExp && hasNbf && audValid && nowUnixSeconds >= nbf && nowUnixSeconds <= exp;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
