namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Service-protocol version negotiation, mirroring sdk-shared-core
///     <c>service_protocol/version.rs</c> and the <c>VM::new</c> content-type check
///     (<c>vm/mod.rs:214-261</c>). The runtime negotiates a per-invocation protocol version and
///     signals it on the <c>/invoke</c> request <c>Content-Type</c> as
///     <c>application/vnd.restate.invocation.v{N}</c>; this SDK accepts only versions within
///     [<see cref="MinimumSupported" />, <see cref="MaximumSupported" />] and rejects anything
///     outside with HTTP 415 (RT0015), exactly as <c>VM::new</c> does.
/// </summary>
internal static class ProtocolVersion
{
    /// <summary>The invocation content-type prefix; the trailing <c>v{N}</c> is the version number.</summary>
    internal const string ContentTypePrefix = "application/vnd.restate.invocation.v";

    /// <summary>Lowest service-protocol version this SDK speaks — shared-core <c>Version::V5</c>.</summary>
    internal const int MinimumSupported = 5;

    /// <summary>Highest service-protocol version this SDK speaks — shared-core <c>Version::V6</c>.</summary>
    internal const int MaximumSupported = 6;

    /// <summary>
    ///     The version at which terminal-error <c>Failure.metadata</c> became available
    ///     (<c>verify_error_metadata_feature_support</c> gates on <c>Version::V6</c>, vm/mod.rs:118-124).
    /// </summary>
    internal const int ErrorMetadataMinimumVersion = 6;

    /// <summary>Renders the <c>application/vnd.restate.invocation.v{N}</c> content type for a version.</summary>
    internal static string ContentTypeFor(int version) => $"{ContentTypePrefix}{version}";

    /// <summary>
    ///     Parses the bare protocol version number out of an invocation content type. Strips any
    ///     parameters (e.g. <c>; charset=...</c>) first, then reads the integer after the
    ///     <c>...invocation.v</c> prefix. Returns <see langword="null" /> when the content type is
    ///     absent or is not a well-formed invocation content type — the caller decides how to react
    ///     (echo the default for the response head vs. reject inbound).
    /// </summary>
    internal static int? TryParse(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return null;

        // The bare media type (no parameters) is what carries the version token.
        var separator = contentType.IndexOf(';');
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();

        if (!mediaType.StartsWith(ContentTypePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var versionToken = mediaType[ContentTypePrefix.Length..];
        return int.TryParse(versionToken, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    /// <summary>True iff <paramref name="version" /> is within the supported [min,max] window.</summary>
    internal static bool IsSupported(int version) =>
        version >= MinimumSupported && version <= MaximumSupported;

    /// <summary>True iff the negotiated <paramref name="version" /> can carry terminal-error metadata.</summary>
    internal static bool SupportsErrorMetadata(int version) => version >= ErrorMetadataMinimumVersion;
}
