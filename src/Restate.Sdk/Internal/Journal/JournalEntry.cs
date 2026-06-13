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
}
