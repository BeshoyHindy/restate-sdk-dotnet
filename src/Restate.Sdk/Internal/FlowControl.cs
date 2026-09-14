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
}
