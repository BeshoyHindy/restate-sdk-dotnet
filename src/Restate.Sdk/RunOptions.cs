namespace Restate.Sdk;

/// <summary>
///     Options for <c>ctx.RunAsync</c> operations. Today this carries only <see cref="Payload" /> for
///     SURFACE parity with shared-core's <c>propose_run</c> PayloadOptions plumbing (lib.rs propose_run
///     path) and to future-proof the API.
///     <para>
///         NOTE — there is no active Run replay payload byte-check to gate: <c>RunCommand</c> uses
///         <c>impl_message_traits!(RunCommand: command eq)</c> (messages.rs:224) with NO
///         <c>ignore_payload_equality</c> arm. A run's produced value travels on
///         <c>ProposeRunCompletion</c> (a notification, <c>: core</c> eq, messages.rs:75), not on the
///         <c>RunCommand</c> header, so shared-core performs NO RunCommand payload byte-compare on replay.
///         This field exists for shape parity, not because a Run payload is byte-validated. Stated
///         honestly per docs/research/shared-core/09-parity-audit.md (comparedPayloads exclusion).
///     </para>
/// </summary>
public readonly record struct RunOptions
{
    /// <summary>
    ///     Per-op payload serialization options. Inert today (no Run replay byte-check exists); present
    ///     for surface parity with the other payload-bearing option types.
    /// </summary>
    public PayloadOptions Payload { get; init; }

    /// <inheritdoc cref="Payload" />
    public static RunOptions WithPayloadOptions(PayloadOptions payload) =>
        new() { Payload = payload };
}
