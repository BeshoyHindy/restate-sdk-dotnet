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

    /// <summary>Human-readable list of supported invocation content types, for 415 responses.</summary>
    public const string SupportedContentTypes =
        "application/vnd.restate.invocation.v5, " +
        "application/vnd.restate.invocation.v6, " +
        "application/vnd.restate.invocation.v7";

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

        if (!value.StartsWith(ContentTypePrefix, StringComparison.Ordinal))
            return false;

        var suffix = value[ContentTypePrefix.Length..];
        if (suffix.Length != 1)
            return false;

        switch (suffix[0])
        {
            case '5':
                version = ServiceProtocolVersion.V5;
                return true;
            case '6':
                version = ServiceProtocolVersion.V6;
                return true;
            case '7':
                version = ServiceProtocolVersion.V7;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Returns the invocation content type for the given protocol version.</summary>
    public static string ToContentType(this ServiceProtocolVersion version)
    {
        return version switch
        {
            ServiceProtocolVersion.V5 => "application/vnd.restate.invocation.v5",
            ServiceProtocolVersion.V6 => "application/vnd.restate.invocation.v6",
            ServiceProtocolVersion.V7 => "application/vnd.restate.invocation.v7",
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown service protocol version")
        };
    }
}
