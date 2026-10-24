using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal;

/// <summary>
///     Validation shared by the handler and ingress paths for the flow-control options.
/// </summary>
internal static class FlowControl
{
    /// <summary>
    ///     A limit key narrows the concurrency limit of a scope, so the server can only honour it
    ///     when a scope is set (<c>protocol.proto</c>: "a limit key is only valid if scope is set").
    ///     Sending one without a scope would be silently ignored, losing the flow control the
    ///     caller asked for — fail instead.
    /// </summary>
    public static void Validate(string? scope, string? limitKey, string paramName)
    {
        if (limitKey is not null && scope is null)
            throw new ArgumentException("A limit key requires a scope; set Scope alongside LimitKey.", paramName);
    }

    /// <summary>
    ///     The <c>scope</c> and <c>limit_key</c> fields of CallCommand and OneWayCallCommand were
    ///     added in service protocol V7 (<c>protocol.proto</c>), so a V5 or V6 server parses the
    ///     command without them and the caller silently loses the flow control it asked for —
    ///     fail instead, the same way <see cref="Validate" /> does for a limit key without a scope.
    ///     The exception is an <see cref="ArgumentException" /> rather than a
    ///     <see cref="TerminalException" />: asking for an option the negotiated stream cannot
    ///     carry is a usage error in the handler, not an outcome of the invocation.
    /// </summary>
    public static void ValidateVersion(string? scope, string? limitKey,
        ServiceProtocolVersion negotiatedVersion, string paramName)
    {
        if (negotiatedVersion >= ServiceProtocolVersion.V7 || (scope is null && limitKey is null))
            return;

        throw new ArgumentException(
            $"A scope or limit key requires service protocol V7, but the negotiated version is {negotiatedVersion}. " +
            "The server must negotiate protocol V7 for this call (restate-server 1.8 does by default).",
            paramName);
    }
}
