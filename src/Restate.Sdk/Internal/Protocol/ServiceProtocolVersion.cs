namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Service protocol versions supported by this SDK.
///     The version is negotiated per invocation from the request content type
///     (<c>application/vnd.restate.invocation.v{N}</c>).
/// </summary>
internal enum ServiceProtocolVersion
{
    /// <summary>Service protocol version 5 (immutable journal).</summary>
    V5 = 5,

    /// <summary>Service protocol version 6 (adds StartMessage.random_seed, Failure.metadata).</summary>
    V6 = 6,

    /// <summary>Service protocol version 7 (adds Future combinators, scopes, ErrorMessage.behavior).</summary>
    V7 = 7
}

/// <summary>
///     Parsing and formatting helpers for <see cref="ServiceProtocolVersion" /> content types.
/// </summary>
internal static class ServiceProtocolVersionExtensions
{
    private const string ContentTypePrefix = "application/vnd.restate.invocation.v";

    // Single source for the per-version content-type strings: TryParse, ToContentType, and
    // SupportedContentTypes all derive from these consts, so adding a version cannot leave
    // one of the three out of sync (e.g. 415 responses advertising a stale list).
    private const string ContentTypeV5 = $"{ContentTypePrefix}5";
    private const string ContentTypeV6 = $"{ContentTypePrefix}6";
    private const string ContentTypeV7 = $"{ContentTypePrefix}7";

    /// <summary>Human-readable list of supported invocation content types, for 415 responses.</summary>
    public const string SupportedContentTypes = $"{ContentTypeV5}, {ContentTypeV6}, {ContentTypeV7}";

    /// <summary>
    ///     Parses a request content type of the form <c>application/vnd.restate.invocation.v{N}</c>.
    ///     Media type parameters (anything after <c>;</c>) and surrounding whitespace are ignored.
    /// </summary>
    /// <param name="contentType">The raw Content-Type header value, or <see langword="null" />.</param>
    /// <param name="version">The parsed protocol version when the method returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> if the content type names a supported protocol version.</returns>
    public static bool TryParse(string? contentType, out ServiceProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(contentType))
            return false;

        var value = contentType.AsSpan().Trim();

        // Ignore media type parameters, e.g. "application/vnd.restate.invocation.v7; charset=utf-8".
        var separator = value.IndexOf(';');
        if (separator >= 0)
            value = value[..separator].TrimEnd();

        if (value.SequenceEqual(ContentTypeV5))
        {
            version = ServiceProtocolVersion.V5;
            return true;
        }

        if (value.SequenceEqual(ContentTypeV6))
        {
            version = ServiceProtocolVersion.V6;
            return true;
        }

        if (value.SequenceEqual(ContentTypeV7))
        {
            version = ServiceProtocolVersion.V7;
            return true;
        }

        return false;
    }

    /// <summary>Returns the invocation content type for the given protocol version.</summary>
    public static string ToContentType(this ServiceProtocolVersion version)
    {
        return version switch
        {
            ServiceProtocolVersion.V5 => ContentTypeV5,
            ServiceProtocolVersion.V6 => ContentTypeV6,
            ServiceProtocolVersion.V7 => ContentTypeV7,
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown service protocol version")
        };
    }
}
