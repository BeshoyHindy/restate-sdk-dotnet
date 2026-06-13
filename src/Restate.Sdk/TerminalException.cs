using System.Collections.Generic;

namespace Restate.Sdk;

/// <summary>
///     An error that will not be retried by the Restate runtime.
///     Use this for business logic failures or validation errors where retrying would not help.
/// </summary>
public sealed class TerminalException : Exception
{
    /// <summary>Creates a terminal exception with the specified message and error code.</summary>
    public TerminalException(string message, ushort code = 500)
        : this(message, code, null)
    {
    }

    /// <summary>
    ///     Creates a terminal exception with the specified message, error code, and structured
    ///     <paramref name="metadata" /> (key/value pairs). Metadata maps onto the protocol
    ///     <c>Failure.metadata</c> field and is only carried on the wire when the negotiated
    ///     service-protocol version supports it (V6+); on older versions it is silently dropped.
    /// </summary>
    public TerminalException(string message, ushort code, IReadOnlyDictionary<string, string>? metadata)
        : base(message)
    {
        Code = code;
        Metadata = metadata ?? EmptyMetadata;
    }

    /// <summary>Creates a terminal exception with the specified message, inner exception, and error code.</summary>
    public TerminalException(string message, Exception innerException, ushort code = 500)
        : base(message, innerException)
    {
        Code = code;
        Metadata = EmptyMetadata;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(0);

    /// <summary>The HTTP-like error code for this terminal error.</summary>
    public ushort Code { get; }

    /// <summary>
    ///     Structured error metadata (key/value pairs) round-tripped through the protocol
    ///     <c>Failure.metadata</c> field on V6+. Empty when no metadata was supplied or the negotiated
    ///     version does not support it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
