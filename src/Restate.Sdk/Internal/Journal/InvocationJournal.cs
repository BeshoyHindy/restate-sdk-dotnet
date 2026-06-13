using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.Journal;

/// <summary>
///     Command accounting + buffered replay queue. Mirrors sdk-shared-core's Journal counters
///     (vm/context.rs) and State::Replaying{ commands } (vm/transitions/journal.rs):
///     replay is driven exclusively by the queue buffered during StartAsync, ends when the
///     queue empties, and never reads the wire.
///
///     THREAD-SAFETY: this class is intentionally NOT self-synchronizing. Every member —
///     EnqueueReplay (preflight only), DequeueReplay, RecordCommand, the counters — is invoked
///     exclusively inside the state machine's _commandLock, the same domain as the completion id
///     counters, so id-allocation order, journal order, and dequeue order coincide by construction.
/// </summary>
internal sealed class InvocationJournal
{
    private readonly Queue<ReplayCommand> _replayCommands = new();

    /// <summary>StartMessage.known_entries — commands + notifications. Preflight loop bound + logging ONLY.</summary>
    public int KnownEntries { get; private set; }

    /// <summary>Total commands recorded (replayed-consumed + live-written). Entry 0 is Input.</summary>
    public int Count { get; private set; }

    public int CommandIndex => Count - 1;

    /// <summary>Last recorded command metadata — Rust current_entry_ty/current_entry_name, for error messages.</summary>
    public JournalEntryType LastCommandType { get; private set; } = JournalEntryType.Input;
    public string? LastCommandName { get; private set; }

    /// <summary>Replay is in progress while buffered commands remain — Rust !commands.is_empty().</summary>
    public bool IsReplaying => _replayCommands.Count > 0;

    public void Initialize(int knownEntries) => KnownEntries = knownEntries;

    public void EnqueueReplay(in ReplayCommand command) => _replayCommands.Enqueue(command);

    /// <summary>Records a live command write (the Processing-path analogue of DequeueReplay).</summary>
    public void RecordCommand(JournalEntryType type, string? name = null)
    {
        Count++;
        LastCommandType = type;
        LastCommandName = name;
    }

    /// <summary>
    ///     Pops the next buffered replay command, validating type and name STRICTLY — the
    ///     analogues of UnavailableEntryError, CommandTypeMismatchError and
    ///     check_entry_header_match/CommandMismatchError in vm/transitions/journal.rs:887-902.
    ///     Rust compares the whole expected command, so name comparison is unconditional:
    ///     null normalizes to "" and journaled-nonempty vs expected-empty (or vice versa) is a
    ///     MISMATCH. (This fork's journals are self-produced — pre-release, no foreign-journal
    ///     leniency.)
    /// </summary>
    public ReplayCommand DequeueReplay(JournalEntryType expectedType, string? expectedName = null)
    {
        if (_replayCommands.Count == 0)
            throw new ProtocolException(
                $"Unavailable entry during replay at command index {Count}: expected {expectedType}");

        var command = _replayCommands.Dequeue();
        // CommandTypeMismatchError / CommandMismatchError → JOURNAL_MISMATCH (570) in shared-core
        // (vm/errors.rs:390,396): a recorded-vs-current code-path divergence, not a generic violation.
        if (command.EntryType != expectedType)
            throw new ProtocolException(
                $"Command type mismatch at command index {Count}: replayed {command.EntryType}, expected {expectedType}",
                ProtocolException.JournalMismatchCode);
        if (!string.Equals(command.Name ?? "", expectedName ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count}: replayed '{command.Name}', expected '{expectedName}'",
                ProtocolException.JournalMismatchCode);

        Count++;
        LastCommandType = command.EntryType;
        LastCommandName = command.Name;
        return command;
    }

    /// <summary>
    ///     Call/OneWayCall replay validation — type/name PLUS the target triple, the custom headers, and
    ///     the idempotency_key, so two swapped calls with identical id shapes fail loudly instead of
    ///     cross-wiring values (Rust header_eq compares service_name/handler_name/key/headers/idempotency_key;
    ///     messages.rs:186-213). Headers come from an UNORDERED IReadOnlyDictionary, so they are compared
    ///     as an order-INDEPENDENT key→value SET (HeadersEqual) rather than by byte/sequence order — a
    ///     byte-order compare would spuriously fail a CORRECT replay whose Dictionary enumeration order
    ///     happened to differ. idempotency_key is a plain string compare. Payload bytes are NOT compared
    ///     (the deferred G13 item).
    /// </summary>
    public ReplayCommand DequeueReplay(JournalEntryType expectedType, string? expectedName,
        string expectedService, string expectedHandler, string? expectedKey,
        IReadOnlyDictionary<string, string>? expectedHeaders = null, string? expectedIdempotencyKey = null)
    {
        var command = DequeueReplay(expectedType, expectedName);
        if (!string.Equals(command.TargetService ?? "", expectedService, StringComparison.Ordinal)
            || !string.Equals(command.TargetHandler ?? "", expectedHandler, StringComparison.Ordinal)
            || !string.Equals(command.TargetKey ?? "", expectedKey ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed target " +
                $"'{command.TargetService}/{command.TargetHandler}' key '{command.TargetKey}', " +
                $"expected '{expectedService}/{expectedHandler}' key '{expectedKey}'",
                ProtocolException.JournalMismatchCode);
        if (!HeadersEqual(command.CallHeaders, expectedHeaders))
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed call headers differ from the " +
                "re-supplied ones (order-independent key/value set mismatch)",
                ProtocolException.JournalMismatchCode);
        if (!string.Equals(command.CallIdempotencyKey ?? "", expectedIdempotencyKey ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed idempotency key " +
                $"'{command.CallIdempotencyKey}', expected '{expectedIdempotencyKey}'",
                ProtocolException.JournalMismatchCode);
        return command;
    }

    /// <summary>
    ///     Attach/GetOutput replay validation — type PLUS the structural <c>target</c> identity (the oneof
    ///     kind and its string fields), so a non-deterministic handler that on replay attaches to a
    ///     DIFFERENT invocation/workflow/idempotency target than was journaled fails loudly. Mirrors the
    ///     Call target-triple overload and Rust's derived header_eq (the whole target oneof participates;
    ///     messages.rs:227/230). Value PAYLOAD bytes are not relevant here (Attach/GetOutput carry no request).
    /// </summary>
    public ReplayCommand DequeueReplay(JournalEntryType expectedType, AttachReplayIdentity expected)
    {
        var command = DequeueReplay(expectedType);
        // Single record-struct value equality over the whole target identity (kind + fields), so there is
        // no per-field branch fan-out. Normalize the journaled command into the same AttachReplayIdentity
        // shape the live target produces (ToReplayIdentity); unset string fields default to null on BOTH
        // sides, so a corrupt/foreign None-kind journal (kind None, all fields null) can never equal a live
        // target (always a concrete kind). The mismatch message renders each side's kind for diagnostics.
        var replayed = new AttachReplayIdentity(
            command.AttachTargetKind, command.AttachInvocationId, command.AttachWorkflowName,
            command.AttachWorkflowKey, command.AttachServiceName, command.AttachHandlerName,
            command.AttachIdempotencyKey, command.AttachServiceKey);
        if (replayed != expected)
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed attach target kind " +
                $"'{replayed.Kind}' identity differs from the re-supplied '{expected.Kind}' target",
                ProtocolException.JournalMismatchCode);
        return command;
    }

    /// <summary>
    ///     Order-independent header set-equality: the journaled and live header maps are equal iff they
    ///     have the same key count and every key maps to the same value (ordinal). Both-null/both-empty
    ///     are equal; null and empty are treated as equivalent (a header-less command). This is the
    ///     false-positive-free analogue of Rust's <c>Vec&lt;Header&gt;</c> equality for a .NET source that
    ///     produces headers from an UNORDERED dictionary, so journal byte order is not reproducible.
    /// </summary>
    private static bool HeadersEqual(
        IReadOnlyDictionary<string, string>? replayed, IReadOnlyDictionary<string, string>? expected)
    {
        var replayedCount = replayed?.Count ?? 0;
        var expectedCount = expected?.Count ?? 0;
        if (replayedCount != expectedCount) return false;
        if (replayedCount == 0) return true;
        foreach (var (key, value) in replayed!)
            if (!expected!.TryGetValue(key, out var expectedValue)
                || !string.Equals(value, expectedValue, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    ///     SendSignal replay validation — type PLUS target_invocation_id and the signal_id identity
    ///     (the CANCEL/named idx, or the signal name), so a non-deterministic handler that on replay
    ///     cancels a DIFFERENT target or sends a DIFFERENT named signal than was journaled fails loudly
    ///     instead of being silently accepted. Mirrors the Call target-triple overload and Rust's
    ///     SendSignalCommand command_header_eq (target_invocation_id + signal_id; messages.rs:622-654).
    ///     SendSignal carries an always-empty entry_name, so name is validated by the base overload as ""
    ///     (<paramref name="expectedSignalName" /> null = the IDX variant). Payload bytes are NOT
    ///     compared — parity with the documented §5 payload-equality deferral.
    /// </summary>
    public ReplayCommand DequeueReplay(JournalEntryType expectedType,
        string expectedTarget, uint? expectedSignalIdx, string? expectedSignalName)
    {
        var command = DequeueReplay(expectedType);
        if (!string.Equals(command.SignalTargetInvocationId ?? "", expectedTarget, StringComparison.Ordinal)
            || command.SignalIdx != expectedSignalIdx
            || !string.Equals(command.SignalName ?? "", expectedSignalName ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed signal target " +
                $"'{command.SignalTargetInvocationId}' id '{FormatSignalId(command.SignalIdx, command.SignalName)}', " +
                $"expected '{expectedTarget}' id '{FormatSignalId(expectedSignalIdx, expectedSignalName)}'",
                ProtocolException.JournalMismatchCode);
        return command;
    }

    /// <summary>Renders the signal_id oneof for mismatch diagnostics — idx number or signal name.</summary>
    private static string FormatSignalId(uint? idx, string? name) =>
        name is not null ? name
            : idx?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<empty>";
}
