using System.Buffers;
using System.Text;
using Ed25519Verifier = Restate.Sdk.Internal.Identity.Ed25519.Ed25519;

namespace Restate.Sdk.Internal.Identity;

/// <summary>
///     Verifies Restate request identity (the <c>x-restate-signature-scheme</c> /
///     <c>x-restate-jwt-v1</c> headers) against a configured set of
///     <c>publickeyv1_</c> Ed25519 public keys.
/// </summary>
internal sealed class RequestIdentityVerifier
{
    /// <summary>Header carrying the signature scheme (<c>v1</c> or <c>unsigned</c>).</summary>
    internal const string SignatureSchemeHeader = "x-restate-signature-scheme";

    /// <summary>Header carrying the compact JWT for the <c>v1</c> scheme.</summary>
    internal const string JwtHeader = "x-restate-jwt-v1";

    private const string KeyPrefix = "publickeyv1_";
    private const string SchemeV1 = "v1";

    /// <summary>Upper bound on accepted token length; real tokens are a few hundred characters.</summary>
    private const int MaxTokenLength = 8 * 1024;

    private readonly IdentityPublicKey[] _keys;
    private readonly TimeProvider _timeProvider;

    private RequestIdentityVerifier(IdentityPublicKey[] keys, TimeProvider timeProvider)
    {
        _keys = keys;
        _timeProvider = timeProvider;
    }

    /// <summary>
    ///     Creates a verifier from serialized identity keys of the form
    ///     <c>publickeyv1_&lt;base58btc(32-byte Ed25519 public key)&gt;</c>.
    /// </summary>
    /// <param name="keys">The serialized identity keys; at least one is required.</param>
    /// <param name="timeProvider">Clock used for <c>exp</c>/<c>nbf</c> validation; defaults to the system clock.</param>
    /// <returns>The verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A key is malformed, or <paramref name="keys" /> is empty.</exception>
    public static RequestIdentityVerifier FromKeys(IReadOnlyList<string> keys, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one identity key is required.", nameof(keys));
        }

        var parsed = new IdentityPublicKey[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Invalid identity key '{key}': expected the '{KeyPrefix}' prefix.", nameof(keys));
            }

            byte[]? publicKey = Base58.Decode(key.AsSpan(KeyPrefix.Length));
            if (publicKey is null)
            {
                throw new ArgumentException(
                    $"Invalid identity key '{key}': the key body is not valid base58.", nameof(keys));
            }

            if (publicKey.Length != Ed25519Verifier.PublicKeySize)
            {
                throw new ArgumentException(
                    $"Invalid identity key '{key}': decoded to {publicKey.Length} bytes, expected {Ed25519Verifier.PublicKeySize}.",
                    nameof(keys));
            }

            // The JWT `kid` is the full serialized key, prefix included.
            parsed[i] = new IdentityPublicKey(Encoding.UTF8.GetBytes(key), publicKey);
        }

        return new RequestIdentityVerifier(parsed, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    ///     Verifies a request. Never throws.
    /// </summary>
    /// <param name="scheme">The <c>x-restate-signature-scheme</c> header value, or <see langword="null" /> if absent.</param>
    /// <param name="token">The <c>x-restate-jwt-v1</c> header value, or <see langword="null" /> if absent.</param>
    /// <param name="path">The request path the token must be scoped to (for example <c>/invoke/Greeter/Greet</c>).</param>
    /// <returns><see langword="true" /> if the request is authentic; otherwise <see langword="false" />.</returns>
    public bool Verify(string? scheme, string? token, string path)
    {
        // Keys are configured, so only the "v1" scheme is acceptable:
        // "unsigned", missing, or unknown schemes are all rejected.
        if (!string.Equals(scheme, SchemeV1, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        int byteCount = Encoding.UTF8.GetByteCount(token);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(token, rented);
            long nowUnixSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            return JwtV1.TryValidate(rented.AsSpan(0, written), _keys, path, nowUnixSeconds);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>A configured identity key: the UTF-8 <c>kid</c> (full serialized key) and the raw public key.</summary>
internal sealed class IdentityPublicKey
{
    public IdentityPublicKey(byte[] kidUtf8, byte[] publicKey)
    {
        KidUtf8 = kidUtf8;
        PublicKey = publicKey;
    }

    /// <summary>UTF-8 bytes of the full serialized key (<c>publickeyv1_...</c>), matched against the JWT <c>kid</c>.</summary>
    public byte[] KidUtf8 { get; }

    /// <summary>The raw 32-byte Ed25519 public key.</summary>
    public byte[] PublicKey { get; }
}
