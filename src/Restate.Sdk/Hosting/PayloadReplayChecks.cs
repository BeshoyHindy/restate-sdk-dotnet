namespace Restate.Sdk.Hosting;

/// <summary>
///     Global (per-endpoint) replay payload byte-equality policy — the .NET surface of shared-core's
///     <c>NonDeterministicChecksOption</c> (third_party/sdk-shared-core/src/lib.rs:237-244).
///     <para>
///         The two members map directly onto the two Rust variants:
///         <list type="bullet">
///             <item>
///                 <see cref="Disabled" /> == <c>NonDeterministicChecksOption::PayloadChecksDisabled</c>
///                 (lib.rs:238-241): all NON-payload command parameters are still validated on replay
///                 (ids, targets, names, headers, idempotency keys, signal identities), but the journaled
///                 payload BYTES (state value, call request parameter, awakeable/promise value, output
///                 value) are NOT compared. This is an explicitly-supported shared-core configuration.
///             </item>
///             <item>
///                 <see cref="Strict" /> == <c>NonDeterministicChecksOption::Enabled</c> (lib.rs:242-243,
///                 the Rust <c>#[default]</c>): additionally byte-compares the journaled payload against
///                 the live re-serialized bytes on replay, catching a non-deterministic serializer that
///                 produces different bytes for the same logical value across re-drives.
///             </item>
///         </list>
///     </para>
///     <para>
///         DEFAULT DIVERGENCE (stated honestly): shared-core defaults to <c>Enabled</c>; this SDK defaults
///         to <see cref="Disabled" />. The reason is a concrete .NET false-positive: System.Text.Json does
///         not guarantee byte-stable key order for <c>Dictionary</c>/<c>HashSet</c>, so a CORRECT handler
///         that stores/returns one would spuriously trip JOURNAL_MISMATCH (570) under a strict default.
///         Strict is therefore offered as an explicit opt-in for handlers whose payloads are byte-stable
///         (scalars, strings, records, ordered lists, arrays). See
///         docs/research/shared-core/09-parity-audit.md §5 / §4.
///     </para>
/// </summary>
public enum PayloadReplayChecks
{
    /// <summary>
    ///     Do not byte-compare journaled payloads on replay (NonDeterministicChecksOption::PayloadChecksDisabled).
    ///     The SDK default — preserves historical behavior and avoids the System.Text.Json
    ///     unordered-collection false-positive.
    /// </summary>
    Disabled,

    /// <summary>
    ///     Byte-compare journaled payloads against live re-serialized bytes on replay
    ///     (NonDeterministicChecksOption::Enabled). Opt-in; only safe for byte-stable payloads, or with
    ///     per-op <see cref="Restate.Sdk.PayloadOptions.Unstable" /> exemptions on unordered-collection ops.
    /// </summary>
    Strict
}
