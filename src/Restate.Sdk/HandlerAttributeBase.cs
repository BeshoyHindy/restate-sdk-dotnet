using System.Diagnostics.CodeAnalysis;

namespace Restate.Sdk;

/// <summary>
///     Base class for handler attributes, providing shared configuration properties.
/// </summary>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix")]
public abstract class HandlerAttributeBase : Attribute
{
    /// <summary>
    ///     Override the handler name used in discovery and invocation routing.
    ///     Defaults to the method name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Maximum time the handler can be inactive (no messages exchanged) before the runtime
    ///     suspends the invocation. Format: TimeSpan string (e.g. "00:05:00" for 5 minutes).
    /// </summary>
    public string? InactivityTimeout { get; set; }

    /// <summary>
    ///     Maximum time to wait for the handler to complete after the inactivity timeout expires.
    ///     Format: TimeSpan string (e.g. "00:01:00" for 1 minute).
    /// </summary>
    public string? AbortTimeout { get; set; }

    /// <summary>
    ///     Duration to retain idempotent response results when an <c>Idempotency-Key</c> header is used.
    ///     Format: TimeSpan string (e.g. "1.00:00:00" for 24 hours).
    /// </summary>
    public string? IdempotencyRetention { get; set; }

    /// <summary>
    ///     Duration to retain the invocation journal after the handler completes.
    ///     Format: TimeSpan string (e.g. "1.00:00:00" for 24 hours).
    /// </summary>
    public string? JournalRetention { get; set; }

    /// <summary>
    ///     When <c>true</c>, this handler cannot be invoked via the Restate ingress HTTP API.
    ///     It can only be called from other Restate handlers.
    /// </summary>
    public bool IngressPrivate { get; set; }
}
