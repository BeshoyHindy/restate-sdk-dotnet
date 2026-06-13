namespace Restate.Sdk;

/// <summary>
///     Per-operation options for syscalls that journal a serialized payload (SetState value, Call/Send
///     request parameter, ResolveAwakeable value, ResolvePromise value, RunAsync value). The .NET
///     analogue of shared-core's <c>PayloadOptions</c> (third_party/sdk-shared-core/src/lib.rs:25-47).
///     <para>
///         The single knob — <see cref="UnstableSerialization" /> — opts a SINGLE operation OUT of the
///         replay payload byte-equality check (Rust <c>unstable_serialization</c>, lib.rs:29). It is
///         only consulted when the global <see cref="Hosting.PayloadReplayChecks.Strict" /> mode is
///         enabled; in the default Disabled mode payload bytes are never compared, so the flag is inert.
///     </para>
///     <para>
///         WHY this exists in a .NET SDK whose default is Disabled: System.Text.Json does NOT guarantee
///         byte-order-stable output for unordered collections — <c>Dictionary&lt;K,V&gt;</c> and
///         <c>HashSet&lt;T&gt;</c> serialize in enumeration order, which is not contractually
///         reproducible across runs/process restarts/runtime versions (there is no global key-sort in
///         JsonSerializerOptions). A handler that opts INTO strict checking but stores/returns such a
///         collection on one specific op can mark THAT op <see cref="Unstable" /> to suppress the
///         false-positive JOURNAL_MISMATCH while keeping strict checking everywhere else. Unlike the
///         order-independent SET comparison the SDK uses for call headers
///         (InvocationJournal.HeadersEqual), payload bytes are opaque post-serialization, so there is no
///         order-independent compare available for arbitrary user values — the opt-out is the only safe
///         knob. See docs/research/shared-core/09-parity-audit.md §5 / §4 for the full rationale.
///     </para>
/// </summary>
public readonly record struct PayloadOptions
{
    /// <summary>
    ///     When <see langword="true" />, this operation's journaled payload bytes are NOT byte-compared
    ///     against the live re-serialized bytes on replay, even under global Strict mode. Set this for
    ///     operations whose serializer is non-deterministic at the byte level (e.g. a value containing a
    ///     <c>Dictionary</c> / <c>HashSet</c> serialized via System.Text.Json). Mirrors Rust
    ///     <c>PayloadOptions.unstable_serialization</c> (lib.rs:29).
    /// </summary>
    public bool UnstableSerialization { get; init; }

    /// <summary>
    ///     Stable (deterministic) serialization — payload bytes ARE byte-compared on replay under Strict
    ///     mode. This is the default. Mirrors <c>PayloadOptions::stable()</c> (lib.rs:33-38).
    /// </summary>
    public static PayloadOptions Stable => default;

    /// <summary>
    ///     Unstable (non-deterministic) serialization — payload byte equality is skipped on replay.
    ///     Mirrors <c>PayloadOptions::unstable()</c> (lib.rs:41-46).
    /// </summary>
    public static PayloadOptions Unstable => new() { UnstableSerialization = true };
}
