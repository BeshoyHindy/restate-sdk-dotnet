using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.Journal;

internal enum JournalEntryType
{
    Input,
    Output,
    GetState,
    SetState,
    ClearState,
    ClearAllState,
    GetStateKeys,
    Sleep,
    Call,
    OneWayCall,
    Awakeable,
    CompleteAwakeable,
    Run,
    GetPromise,
    PeekPromise,
    CompletePromise,
    AttachInvocation,
    GetInvocationOutput,
    SendSignal
}

/// <summary>
///     Which arm of the Attach/GetOutput <c>target</c> oneof a replayed command carried — the structural
///     discriminant validated on replay (Rust's AttachInvocationCommand/GetInvocationOutputCommand derive
///     header_eq via <c>eq</c>, so the whole target oneof participates; messages.rs:227/230). <c>None</c>
///     is only reached on a corrupt/foreign journal with the oneof unset.
/// </summary>
internal enum AttachReplayTargetKind
{
    None,
    InvocationId,
    WorkflowTarget,
    IdempotentRequestTarget
}

/// <summary>
///     The flat structural identity of an Attach/GetOutput <c>target</c> oneof used for replay equality —
///     the <see cref="Kind" /> plus whichever string fields that arm carries (the rest null). A positional
///     <c>record struct</c> so the journaled and live identities compare by VALUE in one equality (no
///     per-field branch fan-out); unset fields are null on both sides, so normalization is symmetric.
///     <para>
///         Coverage: every member (the primary ctor, the eight property getters, and
///         Equals/GetHashCode/ToString/PrintMembers/Deconstruct/EqualityContract) is COMPILER-SYNTHESIZED —
///         this type has no hand-written body. The replay equality is exercised end-to-end by
///         ParityBatchETests (matching + every divergence), but the synthesized members are pure data
///         plumbing with nothing hand-written to instrument, so the type carries
///         <c>[ExcludeFromCodeCoverage]</c> and is listed in eng/coverage-gate.ts
///         SANCTIONED_EXCLUSIONS — the same treatment as the synthesized <c>SendResponse</c> record.
///     </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal readonly record struct AttachReplayIdentity(
    AttachReplayTargetKind Kind,
    string? InvocationId,
    string? WorkflowName,
    string? WorkflowKey,
    string? ServiceName,
    string? HandlerName,
    string? IdempotencyKey,
    string? ServiceKey);

/// <summary>
///     One buffered replayed command, parsed during StartAsync preflight.
///     The C# analogue of one element of Rust's State::Replaying{ commands: VecDeque&lt;RawMessage&gt; }
///     (vm/transitions/journal.rs), pre-decoded so replay never re-parses or re-reads the wire.
/// </summary>
internal readonly struct ReplayCommand
{
    public MessageType MessageType { get; init; }
    public JournalEntryType EntryType { get; init; }

    /// <summary>
    ///     Run name / state key / promise key / the proto <c>name</c> field of Call, OneWayCall,
    ///     Sleep (field 12) and SendSignal (<c>entry_name</c>, field 12) — used for non-determinism
    ///     validation. Rust's check_entry_header_match compares the WHOLE expected command;
    ///     name is part of that comparison, so empty-vs-nonempty is a MISMATCH.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     result_completion_id parsed from the command. Every completable V4 command written by
    ///     a conformant SDK carries an id &gt;= 1 (counters start at 1 precisely so 0 means
    ///     field-unset, context.rs:106-107); 0 on a completable command is a corrupt/foreign
    ///     journal and ValidateReplayCompletionId rejects it.
    /// </summary>
    public uint ResultCompletionId { get; init; }

    /// <summary>invocation_id_notification_idx (Call/OneWayCall). Always set by a conformant SDK.</summary>
    public uint InvocationIdNotificationIdx { get; init; }

    /// <summary>
    ///     Call/OneWayCall target triple (service_name/handler_name/key) — replay-validated against
    ///     the live call's target so swapped calls with identical id shapes fail loudly instead of
    ///     cross-wiring values (check_entry_header_match parity).
    /// </summary>
    public string? TargetService { get; init; }
    public string? TargetHandler { get; init; }
    public string? TargetKey { get; init; }

    /// <summary>
    ///     Call/OneWayCall custom request headers (CallCommand field 4 / OneWayCall field 5) parsed as
    ///     an order-INDEPENDENT key→value map. Rust's header_eq compares <c>self.headers == other.headers</c>
    ///     (messages.rs:191/208), but the live headers originate from an UNORDERED IReadOnlyDictionary,
    ///     so the journal byte order is NOT reproducible from the live source: a byte/sequence compare
    ///     would spuriously fail a CORRECT replay. We therefore compare as a SET (key→value map equality),
    ///     which is the deterministic, false-positive-free analogue. Null = the command carried no headers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CallHeaders { get; init; }

    /// <summary>
    ///     Call/OneWayCall idempotency_key (CallCommand field 6 / OneWayCall field 7) — a plain optional
    ///     string, replay-validated by exact equality (Rust header_eq: <c>self.idempotency_key ==
    ///     other.idempotency_key</c>, messages.rs:193/210). Null = field unset (no idempotency).
    /// </summary>
    public string? CallIdempotencyKey { get; init; }

    /// <summary>
    ///     Attach/GetOutput target identity (the whole <c>target</c> oneof — Rust derives header_eq via
    ///     <c>impl_message_traits!(... eq)</c> so it compares the FULL struct incl. target, messages.rs:227/230).
    ///     The oneof kind plus its string fields are mirrored here so a non-deterministic handler that on
    ///     replay attaches to a DIFFERENT invocation/workflow/idempotency target than was journaled fails
    ///     loudly. <see cref="AttachTargetKind" /> selects which fields are meaningful.
    /// </summary>
    public AttachReplayTargetKind AttachTargetKind { get; init; }
    public string? AttachInvocationId { get; init; }
    public string? AttachWorkflowName { get; init; }
    public string? AttachWorkflowKey { get; init; }
    public string? AttachServiceName { get; init; }
    public string? AttachHandlerName { get; init; }
    public string? AttachIdempotencyKey { get; init; }
    public string? AttachServiceKey { get; init; }

    /// <summary>
    ///     SendSignalCommand target + signal identity (target_invocation_id field 1; signal_id oneof:
    ///     idx field 2 / name field 3) — replay-validated against the live cancel/named-signal so a
    ///     non-deterministic handler that on replay cancels a DIFFERENT target, or sends a DIFFERENT
    ///     named signal, fails loudly instead of being silently accepted (the Rust SendSignalCommand
    ///     command_header_eq compares target_invocation_id + signal_id; see messages.rs:622-654).
    ///     <see cref="SignalName" /> is set for the NAME variant; <see cref="SignalIdx" /> for the IDX
    ///     variant (CANCEL = idx 1). Exactly one is non-null on a parsed SendSignal command.
    /// </summary>
    public string? SignalTargetInvocationId { get; init; }
    public uint? SignalIdx { get; init; }
    public string? SignalName { get; init; }

    /// <summary>GetEagerStateCommand / GetEagerStateKeysCommand carry the result inline.</summary>
    public bool HasEagerResult { get; init; }
    public bool EagerIsVoid { get; init; }

    /// <summary>Eager value bytes; for GetEagerStateKeys this is the keys re-encoded as JSON string[].</summary>
    public ReadOnlyMemory<byte> EagerValue { get; init; }

    /// <summary>
    ///     G13 — the journaled command's serialized PAYLOAD bytes, for the strict replay byte-equality
    ///     check. Populated by ParseReplayCommand ONLY for the payload-bearing commands and ONLY when the
    ///     command actually carried a value (the <c>Value</c> arm of each result/completion oneof):
    ///     SetState (value), Call/OneWayCall (request parameter), CompletePromise (CompletionValue),
    ///     CompleteAwakeable (Value), Output (result Value). The Failure/Void arms set
    ///     <see cref="HasPayloadValue" /> = false so the existing full structural eq still governs them —
    ///     parity with Rust's <c>match (Some(Value), Some(Value)) =&gt; true</c> which short-circuits ONLY
    ///     the Value/Value case (messages.rs:90-95/112-114/164-168/240-244). Consulted only under global
    ///     strict mode; in the default Disabled mode these bytes are never compared.
    /// </summary>
    public bool HasPayloadValue { get; init; }

    /// <inheritdoc cref="HasPayloadValue" />
    public ReadOnlyMemory<byte> PayloadValue { get; init; }
}
