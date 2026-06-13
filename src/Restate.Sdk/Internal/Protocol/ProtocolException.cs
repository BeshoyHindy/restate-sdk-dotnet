namespace Restate.Sdk.Internal.Protocol;

internal sealed class ProtocolException : Exception
{
    /// <summary>
    ///     Journal/command mismatch — the code paths taken differ between the recorded journal and
    ///     this execution (non-determinism). Shared-core <c>errors.rs:69</c> JOURNAL_MISMATCH.
    /// </summary>
    internal const ushort JournalMismatchCode = 570;

    /// <summary>
    ///     A generic protocol violation (unexpected message, unavailable entry, malformed stream).
    ///     Shared-core <c>errors.rs:70</c> PROTOCOL_VIOLATION. Used as the default so any protocol
    ///     fault surfaces as 571 rather than collapsing to a generic 500.
    /// </summary>
    internal const ushort ProtocolViolationCode = 571;

    public ProtocolException(string message, ushort code = ProtocolViolationCode) : base(message)
    {
        Code = code;
    }

    public ProtocolException(string message, Exception inner, ushort code = ProtocolViolationCode)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>
    ///     The error code surfaced to the runtime via the terminal error path: 570 for a
    ///     journal/command mismatch, 571 for any other protocol violation. Mirrors the per-error
    ///     code mapping in shared-core <c>vm/errors.rs</c> (impl_error_code!).
    /// </summary>
    public ushort Code { get; }
}
