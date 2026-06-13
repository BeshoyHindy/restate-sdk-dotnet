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
        if (command.EntryType != expectedType)
            throw new ProtocolException(
                $"Command type mismatch at command index {Count}: replayed {command.EntryType}, expected {expectedType}");
        if (!string.Equals(command.Name ?? "", expectedName ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count}: replayed '{command.Name}', expected '{expectedName}'");

        Count++;
        LastCommandType = command.EntryType;
        LastCommandName = command.Name;
        return command;
    }

    /// <summary>
    ///     Call/OneWayCall replay validation — type/name PLUS the target triple, so two swapped
    ///     calls with identical id shapes fail loudly instead of cross-wiring values
    ///     (check_entry_header_match compares service_name/handler_name/key too).
    /// </summary>
    public ReplayCommand DequeueReplay(JournalEntryType expectedType, string? expectedName,
        string expectedService, string expectedHandler, string? expectedKey)
    {
        var command = DequeueReplay(expectedType, expectedName);
        if (!string.Equals(command.TargetService ?? "", expectedService, StringComparison.Ordinal)
            || !string.Equals(command.TargetHandler ?? "", expectedHandler, StringComparison.Ordinal)
            || !string.Equals(command.TargetKey ?? "", expectedKey ?? "", StringComparison.Ordinal))
            throw new ProtocolException(
                $"Command mismatch at command index {Count - 1}: replayed target " +
                $"'{command.TargetService}/{command.TargetHandler}' key '{command.TargetKey}', " +
                $"expected '{expectedService}/{expectedHandler}' key '{expectedKey}'");
        return command;
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
                $"expected '{expectedTarget}' id '{FormatSignalId(expectedSignalIdx, expectedSignalName)}'");
        return command;
    }

    /// <summary>Renders the signal_id oneof for mismatch diagnostics — idx number or signal name.</summary>
    private static string FormatSignalId(uint? idx, string? name) =>
        name is not null ? name
            : idx?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<empty>";
}
