# 05 — Managed Fix Blueprint: Full CoreVM Parity (B1–B10)

Status: AUTHORITATIVE implementation spec. The executor implements this verbatim; no design
decisions remain open. Cross-checked against the fork source at the line numbers cited below
(current as of branch `feat/sdk-shared-core`, post-commit 9b3161a) and against the vendored
ground truth in `third_party/sdk-shared-core` (Rust CoreVM v0.10.x) and
`third_party/sdk-shared-core/service-protocol/dev/restate/service/protocol.proto`.

Inputs: `docs/research/shared-core/04-managed-bug-verification.json` (all 10 findings +
architecture object), `01-shared-core-feature-surface.md`, `02-sdk-parity-matrix.md`,
`03-managed-gap-analysis.md`.

---

## 1. Target model (mirror CoreVM)

The fork currently derives wire completion ids from `_journal.Count`, stores raw replayed
COMMAND protobuf bytes as user results, compares a command-only journal count against
`known_entries` (which per protocol.proto:60-61 counts commands AND notifications), and reads
the pipe from two concurrent tasks. The target model replaces all of that with the CoreVM
structure:

### 1.1 Dedicated id counters (Rust `vm/context.rs:101-114`)

On `InvocationStateMachine` (InvocationStateMachine.cs):

```csharp
// Mirrors sdk-shared-core vm/context.rs Journal::default():
//   completion_index: 1  ("Clever trick for protobuf here" — 0 means field-unset)
//   signal_index: 17     ("1 to 16 are reserved!" — BuiltInSignal.CANCEL = 1, 2-15 reserved)
private const uint FirstCompletionId = 1;
private const uint FirstUserSignalId = 17;
internal const uint CancelSignalId = 1;

private uint _nextCompletionId = FirstCompletionId;
private uint _nextSignalId = FirstUserSignalId;

private uint NextCompletionId() => _nextCompletionId++;   // Rust next_completion_notification_id()
private uint NextSignalId() => _nextSignalId++;           // Rust next_signal_notification_id()
```

INVARIANT (the heart of B1): every completable operation allocates its completion id(s) from
`NextCompletionId()` in BOTH the Processing AND the Replaying branch, in the same order, so the
counters advance identically across attempts and ids are deterministic. The increments are
plain (non-Interlocked) BY DESIGN: both counters are only ever touched inside `_commandLock`
(1.6), the same section that journals/dequeues the command — that is what makes id order equal
journal order under fan-out. `Call` allocates TWO
ids in the order `invocationIdNotificationIdx` then `resultCompletionId` (Rust `sys_call`,
vm/mod.rs:742-744). Eager-state gets allocate one id even though no wire notification ever
arrives (Rust `SysStateGet`, vm/transitions/journal.rs:301 — allocation happens before the
eager/lazy branch and in the Replaying branch too). The dummy-journal-slot hack (`"BUG 1 FIX"`
comments at Operations.cs:280-283, 357-360, 780-783, 875-878) is deleted.

Delete field `private int _nextSignalIndex;` (InvocationStateMachine.cs:31) — replaced by
`_nextSignalId` above. This is the whole of B4.

### 1.2 ReplayCommand queue + replay cursor (Rust `State::Replaying { commands: VecDeque }`)

New struct (in `Internal/Journal/JournalEntry.cs`, replacing the `JournalEntry` struct, which is
deleted along with its dead `WithCompletion` — zero call sites read entry results after this
redesign):

```csharp
namespace Restate.Sdk.Internal.Journal;

internal enum JournalEntryType { /* UNCHANGED — keep the existing 19 members */ }

/// <summary>
///     One buffered replayed command, parsed during StartAsync preflight.
///     The C# analogue of one element of Rust's State::Replaying{ commands: VecDeque<RawMessage> },
///     pre-decoded so replay never re-parses or re-reads the wire.
/// </summary>
internal readonly struct ReplayCommand
{
    public MessageType MessageType { get; init; }
    public JournalEntryType EntryType { get; init; }
    /// <summary>
    ///     Run name / state key / promise key / the proto `name` field of Call, OneWayCall,
    ///     Sleep (field 12) and SendSignal (`entry_name`, field 12) — used for non-determinism
    ///     validation. Rust's check_entry_header_match compares the WHOLE expected command;
    ///     name is part of that comparison, so empty-vs-nonempty is a MISMATCH (see 2.1).
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    ///     result_completion_id parsed from the command. Every completable V4 command written by
    ///     a conformant SDK carries an id >= 1 (counters start at 1 precisely so 0 means
    ///     field-unset, context.rs:106-107); 0 on a completable command is a corrupt/foreign
    ///     journal and ValidateReplayCompletionId rejects it (2.6(d)).
    /// </summary>
    public uint ResultCompletionId { get; init; }
    /// <summary>invocation_id_notification_idx (Call/OneWayCall). Always set by a conformant SDK.</summary>
    public uint InvocationIdNotificationIdx { get; init; }
    /// <summary>Call/OneWayCall target triple (service_name/handler_name/key) — replay-validated
    /// against the live call's target so swapped calls with identical id shapes fail loudly
    /// instead of cross-wiring values (check_entry_header_match parity).</summary>
    public string? TargetService { get; init; }
    public string? TargetHandler { get; init; }
    public string? TargetKey { get; init; }
    /// <summary>GetEagerStateCommand / GetEagerStateKeysCommand carry the result inline.</summary>
    public bool HasEagerResult { get; init; }
    public bool EagerIsVoid { get; init; }
    /// <summary>Eager value bytes; for GetEagerStateKeys this is the keys re-encoded as JSON string[].</summary>
    public ReadOnlyMemory<byte> EagerValue { get; init; }
}
```

DELIBERATE DIVERGENCE (documented, see §5): Rust's `check_entry_header_match` also compares
payload bytes (parameter/state value) unless `non_deterministic_checks_ignore_payload_equality`
is set. Full payload-byte equality is DEFERRED in this program of work; type + name + target
triple + completion-id equality is the validated subset.

Memory ownership: `ReplayCommand` fields come from `Google.Protobuf` parsed messages
(`ByteString.Memory`), which own their backing arrays — the `RawMessage.DetachPayload()` +
`TrackPooledBuffer` dance in StartAsync/ReplayNextEntryAsync is no longer needed for replay and
is removed (see 2.2).

`InvocationJournal` becomes the queue owner + command counter (full new shape in 2.1):

- `Queue<ReplayCommand> _replayCommands` — filled only by `StartAsync` preflight.
- `bool IsReplaying => _replayCommands.Count > 0` — the C# analogue of
  `!commands.is_empty()` (Rust journal.rs:464-476). This REPLACES
  `Count < KnownEntries` (InvocationJournal.cs:20), discharging B2.
- `ReplayCommand DequeueReplay(JournalEntryType expectedType, string? expectedName)` — pops one
  command per sys-call (Rust `commands.pop_front().ok_or(UnavailableEntryError)`,
  journal.rs:406-408) and validates type/name (Rust `check_entry_header_match` →
  `CommandMismatchError`, journal.rs:887-902). Empty queue → `ProtocolException`
  ("unavailable entry"), never a wire read. This + the single pump discharge B3 and the
  positional-mismatch half of B5.
- `int Count` — total commands recorded so far (replayed-consumed + live-written);
  `CommandIndex => Count - 1` mirrors Rust `journal.command_index()` for error metadata.
- `KnownEntries` is kept ONLY to delimit the preflight read loop and for logging.

`ReplayNextEntryAsync` (Protocol.cs:200-234) and `AdvanceReplayIndex` (Protocol.cs:236-244) are
deleted; both are replaced by the synchronous `DequeueReplayCommand` on the state machine
(2.6). After `StartAsync` returns, `ProcessIncomingMessagesAsync` is provably the only reader
of `ProtocolReader`.

### 1.3 CompletionManager keyed by WIRE ids, lock-based (B9)

`CompletionManager` keys are wire completion ids (for `_completions`) / wire signal ids (for
`_signalCompletions`) — never journal indices. The class switches from lock-free
`ConcurrentDictionary` compositions (four TOCTOU windows, the production-lossy one being
TryComplete/TryFail lines 97-104/110-117) to a plain `Dictionary<int, CompletionSlot>` under a
private lock, mirroring the Rust VM's exclusive `&mut self` access. New members
`FailAll(Exception)`/`CancelAll()` (which LATCH the manager terminally), `HasResultFor(int)`,
and `TryClaimForExecution(int)` support suspension (B8) and Run replay (decision 4). The
suspension waiting set comes from the SM's awaiting set (1.5), NOT from the manager — there is
no `PendingIds()`. Full spec in 2.4.

### 1.4 Eager state: `{ is_partial, Dictionary<string, ReadOnlyMemory<byte>?> }` (B7)

Mirrors Rust `EagerState { is_partial: bool, values: HashMap<String, Option<Bytes>> }`
(vm/context.rs:373-435):

```csharp
// On InvocationStateMachine — replaces `Dictionary<string, ReadOnlyMemory<byte>>? _initialState` (line 38).
// Value null = known-cleared marker (Rust None). Absent key + partial = Unknown; absent + complete = Empty.
private readonly Dictionary<string, ReadOnlyMemory<byte>?> _eagerState = new();
private bool _eagerStateIsPartial = true;   // EagerState::default() => is_partial: true
```

Tri-state lookup in `GetStateAsync` (Unknown → GetLazyStateCommand; Empty/Value → journal AND
send `GetEagerStateCommandMessage` with the observed result inline, per journal.rs:313-343).
`SetState`/`ClearState`/`ClearAllState` mutate the cache UNCONDITIONALLY — including during
replay (Rust mod.rs:603/627/650). `ClearAllState` flips `_eagerStateIsPartial = false`.
`ParseStartMessage` stops discarding the partial map (ProtobufCodec.cs:46-52) and surfaces
`bool PartialState`.

### 1.5 Suspension transition (B8) — AWAIT-DRIVEN, mirroring `do_progress`

New sentinel `internal sealed class SuspendedException : Exception` (Rust `Err(Suspended)`,
code 599).

CRITICAL MODEL CORRECTION (adversarial review): Rust evaluates the suspension condition at
EVERY await — `do_progress`'s Processing arm checks `context.input_is_closed` each time the
handler polls (async_results.rs:140-160) and suspends with exactly the `awaiting_on`
notification-id set (`HitSuspensionPoint(notification_ids)`, terminal.rs:18-53). A design that
only evaluates suspension when the pump reads EOF deadlocks whenever EOF lands BEFORE the
handler parks — the normal ordering in request-response/Lambda-style delivery, where the
runtime half-closes input right after the known-entries batch. The suspension decision
therefore lives at the PARK SITE, not (only) at the EOF site:

1. ONE park API. Every waiter in the SDK parks through a single state-machine method,
   `AwaitNotificationAsync(uint id, NotificationKind kind)` (full spec in 2.5) — the
   Template A/C/D awaits, the Run-ack await, signal/awakeable awaits, `DurableFuture<T>.GetResult`
   (which today awaits a bare TCS, DurableFuture.cs:40 — it receives a resolve thunk instead,
   see 2.10), and the lazy `InvocationHandle.GetInvocationIdAsync` resolution (2.7.4). No code
   path may `await tcs.Task` directly on a CompletionManager TCS.
2. AWAITING SET. The park API registers the awaited id in `_awaiting` (a
   `HashSet<(uint Id, NotificationKind Kind)>` on the SM, guarded by `_commandLock`, see 1.6)
   before parking and removes it on unpark. The SuspensionMessage's
   `waiting_completions`/`waiting_signals` are built from the still-UNRESOLVED members of this
   set — never from "all registered TCSs". This is `awaiting_on` parity: a `ctx.Send` whose
   invocation-id TCS nobody awaits, or an un-awaited CallFuture, can NEVER cause or appear in a
   suspension (Rust: sys_write_output is not an await point; a handler that sends then returns
   completes normally with Output+End).
3. TRIGGER SITES. `TrySuspendAsync` (2.5) is invoked from exactly three places, and evaluates
   the full condition `{_inputClosed && _executingRuns == 0 && unresolved awaited ids exist}`
   ATOMICALLY under `_commandLock` (this also kills the volatile store-load/Dekker race between
   `MarkInputClosed` and the `_executingRuns` read flagged in review — there are no lock-free
   reads of suspension state):
   - the park site, after registering in `_awaiting` (covers EOF-before-park);
   - the pump, after `ReadMessageAsync` returns null (covers EOF-after-park);
   - the Run epilogue, after `_executingRuns` is decremented (covers the
     `any_executing`/WaitingPendingRun deferral, async_results.rs:147-157).
4. EFFECT. Under `_commandLock`: write `SuspensionMessage { waiting_completions,
   waiting_signals }` (protocol.proto:88-97 — "Implementations MUST send this message"),
   `State = Closed`, `_suspended = true`. After releasing the lock: flush (under `_flushGate`),
   then `FailAll(new SuspendedException())` on both completion managers so every parked waiter
   unwinds. NO `End` frame follows a suspension (Rust HitSuspensionPoint sends the suspension
   message then EOF only). Because the Suspension write, `State = Closed`, and every other
   terminal write (Output/Error/End, 2.7.5) all happen inside `_commandLock` with an in-lock
   state re-check, "Error/End after Suspension" is impossible by construction.
5. LATCH. `FailAll`/`CancelAll` latch the CompletionManager terminally (2.4): any
   `GetOrRegister` issued AFTER suspension/abort returns an already-faulted TCS, so a straggler
   continuation that parks post-suspension unwinds immediately instead of waiting on a slot
   nobody can resolve.
6. REPLAY-MUTATION GUARD. Parking while `_journal.IsReplaying` is still true (buffered replay
   commands remain) with no buffered result is provably non-deterministic user code — journaled
   notifications all arrive inside the known-entries batch, so a missing completion with LATER
   journaled commands proves an added await point / code mutation (Rust's proof-by-contradiction,
   async_results.rs:50-112, `UncompletedDoProgressDuringReplay`). The park API throws
   `ProtocolException` in that case instead of parking (which would otherwise degenerate into a
   silent suspend→resume→suspend loop). Only frontier awaits (replay queue drained → State
   already Processing) may park/suspend.

`InvocationHandler.HandleAsync` catches `SuspendedException` and returns WITHOUT writing an
error frame (invariant spec in 2.8). `SuspensionMessage.waiting_named_signals` (field 3) is
intentionally ALWAYS empty until named-signal consumption lands (§5 non-goal).

### 1.6 Command lock + flush gate (B5) — synchronous ordered section

CRITICAL MODEL CORRECTION (adversarial review): an async `SemaphoreSlim` write gate does NOT
guarantee journal order == call order. `SemaphoreSlim.WaitAsync` is not FIFO, sync `Wait()`
barges past queued async waiters, and an op whose prefix detaches to a pool-thread continuation
journals at gate-GRANT time, not call time — so a fan-out attempt could journal
[SetStateCommand, RunCommand] where call order was [Run, SetState], poisoning every replay of a
correct program. The fix is structural: the serialize+write+record prefix is SYNCHRONOUS and
never yields, so the calling thread cannot be overtaken between deciding to journal and
journaling.

Two primitives on the state machine:

```csharp
// ONE mutual-exclusion domain for ALL VM state — the .NET analogue of Rust's &mut self.
// Guards: _nextCompletionId/_nextSignalId, _journal (replay queue + counters + RecordCommand),
// _serializeBuffer, WriteCommand (sync PipeWriter buffer copies), State/_suspended transitions,
// _inputClosed/_executingRuns/_awaiting (suspension condition, 1.5), _eagerState/_eagerStateIsPartial.
// NEVER held across any await. All sections are short sync buffer work.
private readonly object _commandLock = new();

// Serializes FlushAsync calls only (PipeWriter does not allow concurrent flushes). Frame ORDER
// is fixed at WriteCommand time inside _commandLock; a flush pushes everything buffered so far,
// so flush-grant order is irrelevant to wire order.
private readonly SemaphoreSlim _flushGate = new(1, 1);
```

INVARIANTS (these are the heart of B5 and of replay determinism):

- Id allocation, replay dequeue, `_serializeBuffer` use, `WriteCommand`, and `RecordCommand`
  for one op happen inside ONE `lock (_commandLock)` block — so allocation order == journal
  order == replay-dequeue order BY CONSTRUCTION, on every thread. (`_nextCompletionId++` /
  `Queue.Dequeue()` are never executed outside the lock.)
- The locked prefix is synchronous: a future-creating method (`RunFutureAsync`,
  `CallFutureAsync`, `SleepFutureAsync`) has its command journaled before the method first
  yields, so the handler issuing ops sequentially gets sequential journal entries even when the
  returned futures are stored un-awaited (DefaultContext.cs:76-86).
- `FlushAsync` happens AFTER releasing `_commandLock`, under `_flushGate`. Helper:
  `private async ValueTask FlushGatedAsync(CancellationToken ct)` =
  `await _flushGate.WaitAsync(ct); try { await FlushAsync(ct); } finally { _flushGate.Release(); }`.
- User code (`await action()`) and parking (`await tcs.Task` via the park API) run with NO lock
  held.
- Inside every locked section, re-check `State` first (`ThrowIfClosedLocked`, 2.5) — closed by
  suspension → `SuspendedException`; closed normally → `InvalidOperationException`/silent
  return per op.
- AUDIT RULE: every `Serialize`/`SerializeObject`/`SerializeWithSerde` call site in the SDK
  must be inside `_commandLock` with the produced bytes fully consumed (copied into the
  PipeWriter or `CopyToPooled`) before the lock is released — including the handler-result
  serialization in `InvocationHandler.HandleAsync`, which moves INTO `CompleteAsync` (2.7.5,
  2.8) to close the torn-OutputCommand race with straggler Run proposals sharing
  `_serializeBuffer`.

### 1.7 Run command order + result-from-notification (B5 + B10b)

All Run variants journal `RunCommandMessage{ name, result_completion_id }` in the SYNCHRONOUS
locked prefix (1.6), BEFORE executing the user action (Rust `SysRun` journals at creation,
journal.rs:659-713) — journal order = creation order, never completion order. The result is
attached afterwards via `ProposeRunCompletionMessage` keyed by the CAPTURED completion id
(never a fresh `_journal.Count` read — `WriteRunCommand`/`WriteRunProposal`
(Operations.cs:195-207) take explicit `uint completionId` parameters).

FAILURE DIRECTION (adversarial-review correction to B10b): a terminal Run failure must surface
to user code ONLY via the `RunCompletionNotification`, exactly like success — NEVER by
rethrowing the closure's `TerminalException` directly. Rethrowing directly would let saga
compensations run BEFORE the runtime durably stored the failure proposal; Rust surfaces the
failure only from the notification (after durable storage), and under closed input it suspends
instead. So `ExecuteAndProposeRunAsync` (2.7.2) proposes the failure and RETURNS; every Run path
then awaits the notification through the park API and `completion.ThrowIfFailure()` raises the
`TerminalException` after durability (bidi) or unwinds with `SuspendedException` (closed input)
— Rust semantics in both directions, and identical fresh-vs-replay behavior. On success the
locally computed value is returned directly (no re-deserialization of the notification
payload); the notification await is still the ack barrier.

Run during replay (resolved decision 4, NARROWED by review): dequeue + verify the RunCommand;
then exactly three cases, mirroring `DoProgress`'s Replaying arm:

1. Completion buffered for its id (`_completions.HasResultFor`) → consume it WITHOUT executing
   the closure (Rust `non_deterministic_find_id`).
2. No completion AND the dequeue ENDED replay (`!_journal.IsReplaying` — the Run is the LAST
   journaled command, i.e., the replay frontier; the SM is now Processing) → execute the
   closure inline, propose with the SAME deterministic id, await the notification. This — and
   ONLY this — is the resume case decision 4 covers. (Thread/timing differs from Rust's queued
   `DoProgress::ExecuteRun`, but same id, same wire frames, same value source.) The
   execute-vs-await decision is made atomically via `CompletionManager.TryClaimForExecution`
   (2.4) so a late notification racing the check can never cause a duplicate execution or a
   duplicate proposal.
3. No completion AND replay continues (`_journal.IsReplaying` still true — journaled commands
   exist AFTER this uncompleted Run) → `ProtocolException` ("uncompleted Run during replay —
   journal mutation"), `UncompletedDoProgressDuringReplay` parity (async_results.rs:50-112). In
   Rust this situation is impossible-by-construction (runs execute only in Processing); the
   .NET SDK must fail fast here, NOT re-execute a user side effect mid-replay.

### 1.8 InvocationHandle: lazy invocation id (B6)

`public readonly record struct InvocationHandle(string InvocationId)` (InvocationHandle.cs:6)
becomes a lazy handle mirroring Rust `SendHandle.invocation_id().await` / Python
`handle.invocation_id()`:

```csharp
namespace Restate.Sdk;

/// <summary>
///     A handle to an invocation started by a send operation. The invocation id is resolved
///     lazily from the runtime's CallInvocationIdCompletionNotification — mirroring the Rust
///     shared-core SendHandle and the Python SDK send handle. Sends are fire-and-forget; the
///     id round trip happens only if the caller asks for it.
/// </summary>
public sealed class InvocationHandle
{
    private readonly Lazy<Task<string>> _invocationId;

    /// <summary>Eager constructor for call sites that already know the id (ingress clients, tests, mocks).</summary>
    public InvocationHandle(string invocationId) =>
        _invocationId = new Lazy<Task<string>>(() => Task.FromResult(invocationId));

    /// <summary>Lazy constructor — the resolve thunk runs on FIRST GetInvocationIdAsync call,
    /// not at send time, so an unawaited handle never parks, never registers in the awaiting
    /// set (1.5), and never produces an UnobservedTaskException.</summary>
    internal InvocationHandle(Func<Task<string>> resolveInvocationId) =>
        _invocationId = new Lazy<Task<string>>(resolveInvocationId);

    public ValueTask<string> GetInvocationIdAsync() => new(_invocationId.Value);
}
```

WHY a thunk and not a `Task<string>` (adversarial-review correction): an eagerly-started
resolution task is itself a parked awaiter from creation — it would register in the awaiting
set, spuriously suspend a handler that does `ctx.Send` then returns, and fault unobserved on
suspension (UnobservedTaskException noise). With the thunk, awaiting the id is an EXPLICIT
suspension point routed through the park API (2.7.4), exactly like Rust
`SendHandle.invocation_id().await`.

Specified semantics of `GetInvocationIdAsync()`:
- Awaiting it IS a suspension point: parked with input closed and no
  `CallInvocationIdCompletionNotification` → the invocation suspends with the send's id in
  `waiting_completions`; the in-flight await unwinds with `SuspendedException` (intended — it
  propagates out of the handler and must not be wrapped).
- A Failure notification surfaces as `TerminalException`.
- A handle awaited AFTER the invocation ended (post-`Dispose`/`CancelAll`) gets
  `TaskCanceledException` — documented public behavior.
- Repeated awaits return the same value (the `Lazy<Task>` caches the resolution).

The sync `InvocationId` positional property is REMOVED (pre-release fork; no compat
obligation). `Restate.Sdk.Testing/MockContext.cs:228/237/356` already uses the eager
constructor shape and keeps compiling; `test/Restate.Sdk.Tests/OptionsTests.cs:48-52`
(`InvocationHandle_StoresId` reads `handle.InvocationId`) does NOT — it is rewritten in the
Phase 2a sweep (2.9). `SendAsync` returns immediately after flush; the handle wraps a resolve
thunk over the pending invocation-id slot.

---

## 2. Per-file edit spec

Build-entangled foundation files (Phase 1, single executor, in this order):
2.1 InvocationJournal.cs → 2.2 JournalEntry.cs → 2.3 ProtobufCodec.cs (+ ProtocolTypes.cs)
→ 2.4 CompletionManager.cs → 2.5 InvocationStateMachine.cs (+ SuspendedException.cs)
→ 2.6 InvocationStateMachine.Protocol.cs → 2.7 InvocationStateMachine.Operations.cs
→ 2.8 InvocationHandler.cs. They do not compile independently; commit as one unit after green.

### 2.1 `src/Restate.Sdk/Internal/Journal/InvocationJournal.cs` — discharges B2 (with 2.6), supports B1/B3/B5

Replace the whole class. The pooled `JournalEntry[]` array, indexer, `TrackPooledBuffer`,
`Dispose`, and `Grow` are deleted (replay data now lives in parsed protobuf messages that own
their memory; the only remaining pooled rentals are `CopyToPooled` on the state machine).

```csharp
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.Journal;

/// <summary>
///     Command accounting + buffered replay queue. Mirrors sdk-shared-core's Journal counters
///     (vm/context.rs) and State::Replaying{ commands } (vm/transitions/journal.rs):
///     replay is driven exclusively by the queue buffered during StartAsync, ends when the
///     queue empties, and never reads the wire.
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
}
```

THREAD-SAFETY: this class is intentionally NOT self-synchronizing. Every member —
`EnqueueReplay` (preflight only), `DequeueReplay`, `RecordCommand`, the counters — is invoked
exclusively inside the state machine's `_commandLock` (1.6), the same domain as the completion
id counters, so id-allocation order, journal order, and dequeue order coincide by construction.

Note `IDisposable` is gone — remove `_journal.Dispose()` from `InvocationStateMachine.Dispose`
(InvocationStateMachine.cs:84). Existing `test/Restate.Sdk.Tests/Journal/JournalTests.cs` is
rewritten against this API in Phase 1 (compile gate).

### 2.2 `src/Restate.Sdk/Internal/Journal/JournalEntry.cs` — supports B1/B2/B3; discharges the B1 dead-code finding

- KEEP `JournalEntryType` exactly as-is (19 members).
- DELETE the `JournalEntry` struct entirely (its `Result`-carrying replay role is gone;
  `WithCompletion` at lines 51-54 was already dead).
- ADD the `ReplayCommand` struct exactly as specified in section 1.2.

### 2.3 `src/Restate.Sdk/Internal/Protocol/ProtobufCodec.cs` + `ProtocolTypes.cs` — supports B1/B6/B7/B8

`ProtocolTypes.cs` — change `StartMessageFields` (lines 6-12):

```csharp
internal readonly record struct StartMessageFields(
    byte[] RawId,
    string InvocationId,
    string? Key,
    uint KnownEntries,
    ulong RandomSeed,
    Dictionary<string, ReadOnlyMemory<byte>?> EagerState,   // ALWAYS materialized (may be empty)
    bool PartialState);                                      // StartMessage field 5 — no longer discarded
```

`ProtobufCodec.cs` edits:

(a) `ParseStartMessage` (lines 42-61): always materialize the state map and surface the flag —

```csharp
var eagerState = new Dictionary<string, ReadOnlyMemory<byte>?>(msg.StateMap.Count);
foreach (var entry in msg.StateMap)
    eagerState[entry.Key.ToStringUtf8()] = entry.Value.Memory;
return new StartMessageFields(msg.Id.ToByteArray(), msg.DebugId,
    msg.Key.Length > 0 ? msg.Key : null, msg.KnownEntries, msg.RandomSeed,
    eagerState, msg.PartialState);
```

(b) NEW `ParseReplayCommand` — the preflight decoder. Uses the generated parsers (AOT-safe,
protobuf codegen, no reflection):

```csharp
public static ReplayCommand ParseReplayCommand(MessageType type, ReadOnlySpan<byte> payload)
{
    switch (type)
    {
        case MessageType.OutputCommand:
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.Output };
        case MessageType.GetLazyStateCommand:
        {
            var m = Gen.GetLazyStateCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetState,
                Name = m.Key.ToStringUtf8(), ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.GetEagerStateCommand:
        {
            var m = Gen.GetEagerStateCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetState,
                Name = m.Key.ToStringUtf8(), HasEagerResult = true,
                EagerIsVoid = m.ResultCase == Gen.GetEagerStateCommandMessage.ResultOneofCase.Void,
                EagerValue = m.ResultCase == Gen.GetEagerStateCommandMessage.ResultOneofCase.Value
                    ? m.Value.Content.Memory : ReadOnlyMemory<byte>.Empty };
        }
        case MessageType.GetLazyStateKeysCommand:
        {
            var m = Gen.GetLazyStateKeysCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetStateKeys,
                ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.GetEagerStateKeysCommand:
        {
            var m = Gen.GetEagerStateKeysCommandMessage.Parser.ParseFrom(payload);
            // Re-encode keys as JSON string[] — same convention as the StateKeys notification (lines 106-113).
            // Verified generated shape: Value is Gen.StateKeys (field 14) and may be null when unset.
            var keyCount = m.Value?.Keys.Count ?? 0;
            var keys = new string[keyCount];
            for (var i = 0; i < keyCount; i++) keys[i] = m.Value!.Keys[i].ToStringUtf8();
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetStateKeys,
                HasEagerResult = true,
                EagerValue = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(keys) };
        }
        case MessageType.SetStateCommand:
        {
            var m = Gen.SetStateCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.SetState,
                Name = m.Key.ToStringUtf8() };
        }
        case MessageType.ClearStateCommand:
        {
            var m = Gen.ClearStateCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.ClearState,
                Name = m.Key.ToStringUtf8() };
        }
        case MessageType.ClearAllStateCommand:
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.ClearAllState };
        case MessageType.SleepCommand:
        {
            var m = Gen.SleepCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.Sleep,
                Name = m.Name, ResultCompletionId = m.ResultCompletionId };   // name = proto field 12
        }
        case MessageType.CallCommand:
        {
            var m = Gen.CallCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.Call,
                Name = m.Name,                                                // proto field 12
                TargetService = m.ServiceName, TargetHandler = m.HandlerName, TargetKey = m.Key,
                ResultCompletionId = m.ResultCompletionId,
                InvocationIdNotificationIdx = m.InvocationIdNotificationIdx };
        }
        case MessageType.OneWayCallCommand:
        {
            var m = Gen.OneWayCallCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.OneWayCall,
                Name = m.Name,
                TargetService = m.ServiceName, TargetHandler = m.HandlerName, TargetKey = m.Key,
                InvocationIdNotificationIdx = m.InvocationIdNotificationIdx };
        }
        case MessageType.SendSignalCommand:
        {
            var m = Gen.SendSignalCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.SendSignal,
                Name = m.EntryName };                                         // entry_name, proto field 12
        }
        case MessageType.RunCommand:
        {
            var m = Gen.RunCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.Run,
                Name = m.Name, ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.GetPromiseCommand:
        {
            var m = Gen.GetPromiseCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetPromise,
                Name = m.Key, ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.PeekPromiseCommand:
        {
            var m = Gen.PeekPromiseCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.PeekPromise,
                Name = m.Key, ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.CompletePromiseCommand:
        {
            var m = Gen.CompletePromiseCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.CompletePromise,
                Name = m.Key, ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.AttachInvocationCommand:
        {
            var m = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.AttachInvocation,
                ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.GetInvocationOutputCommand:
        {
            var m = Gen.GetInvocationOutputCommandMessage.Parser.ParseFrom(payload);
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.GetInvocationOutput,
                ResultCompletionId = m.ResultCompletionId };
        }
        case MessageType.CompleteAwakeableCommand:
            return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.CompleteAwakeable };
        default:
            throw new ProtocolException($"Unknown replayed command type: {type}");
    }
}
```

(All generated property names above are VERIFIED against `obj/Debug/net10.0/dev/restate/service/Protocol.cs`:
`GetEagerStateKeysCommandMessage.Value` is `Gen.StateKeys` at field 14;
`SuspensionMessage.WaitingCompletions`/`WaitingSignals` are `RepeatedField<uint>` at fields 1/2;
the name/target accessors map to proto fields verified in protocol.proto —
`SleepCommandMessage.Name` (12), `CallCommandMessage.{ServiceName,HandlerName,Key,Name}`
(1/2/5/12), `OneWayCallCommandMessage.{ServiceName,HandlerName,Key,Name}`,
`SendSignalCommandMessage.EntryName` (12, `entry_name` — `name` is taken by the signal_id
oneof); the remaining accessors are already used elsewhere in the codec.)

(c) NEW `CreateGetEagerStateCommand` (B7 — journaled eager hits, journal.rs:325-340):

```csharp
/// <summary>value == null → Void result (known-absent/cleared); otherwise Value result.</summary>
public static Gen.GetEagerStateCommandMessage CreateGetEagerStateCommand(string key, ReadOnlyMemory<byte>? value)
{
    var msg = new Gen.GetEagerStateCommandMessage { Key = ByteString.CopyFromUtf8(key) };
    if (value is null) msg.Void = new Gen.Void();
    else msg.Value = new Gen.Value { Content = ByteString.CopyFrom(value.Value.Span) };
    return msg;
}

public static Gen.GetEagerStateKeysCommandMessage CreateGetEagerStateKeysCommand(IEnumerable<string> keys)
{
    var sk = new Gen.StateKeys();
    foreach (var k in keys) sk.Keys.Add(ByteString.CopyFromUtf8(k));
    return new Gen.GetEagerStateKeysCommandMessage { Value = sk };
}
```

(d) NEW `CreateSuspensionMessage` (B8):

```csharp
/// <summary>
///     waiting_named_signals (proto field 3) is INTENTIONALLY never populated: this fork does
///     not consume named signals (§5 non-goal); Rust fills it from NotificationId::SignalName
///     (terminal.rs:43-46). Whoever implements named-signal waits must extend this factory or
///     the runtime will never resume a named-signal park.
/// </summary>
public static Gen.SuspensionMessage CreateSuspensionMessage(
    IReadOnlyCollection<uint> waitingCompletions, IReadOnlyCollection<uint> waitingSignals)
{
    var msg = new Gen.SuspensionMessage();
    foreach (var id in waitingCompletions) msg.WaitingCompletions.Add(id);
    foreach (var id in waitingSignals) msg.WaitingSignals.Add(id);
    return msg;
}
```

(e) Replace the magic `Idx = 1` in `CreateCancelInvocationCommand` (line 382) with
`InvocationStateMachine.CancelSignalId` (or a local `const uint CancelSignalId = 1` in the
codec referenced by both — pick the codec constant and have the SM reuse it).

### 2.4 `src/Restate.Sdk/Internal/Journal/CompletionManager.cs` — discharges B9; supports B8 + Run replay

Keep `CompletionSlot` and `CompletionResult` unchanged. Replace the manager:

```csharp
/// <summary>
///     Per-invocation completion table keyed by WIRE completion id (or wire signal id for the
///     signal instance) — never by journal index. Exactly two tasks contend (handler + pump),
///     so a plain Dictionary under one lock mirrors the Rust VM's exclusive &mut self access
///     and removes the four ConcurrentDictionary TOCTOU windows (the lossy one: a TryComplete
///     whose failed TryRemove raced GetOrRegister, silently dropping the only notification).
///     TCSs use RunContinuationsAsynchronously, so resolving inside the lock never runs user
///     continuations inline.
/// </summary>
internal sealed class CompletionManager
{
    private readonly Dictionary<int, CompletionSlot> _slots = new();
    private readonly HashSet<int> _claimed = new();   // Run ids claimed for local execution
    private readonly object _gate = new();
    private Exception? _terminal;                     // latch: set by FailAll/CancelAll

    public TaskCompletionSource<CompletionResult> GetOrRegister(int completionId)
    {
        lock (_gate)
        {
            // LATCH: after FailAll/CancelAll any new registration is born faulted, so a
            // straggler continuation that parks post-suspension/post-abort unwinds immediately
            // instead of waiting on a slot nobody can ever resolve.
            if (_terminal is not null)
            {
                var faulted = NewTcs();
                faulted.SetException(_terminal);
                _ = faulted.Task.Exception;            // pre-observe (see FailAll)
                return faulted;
            }
            if (_slots.TryGetValue(completionId, out var slot))
            {
                if (slot.Kind == CompletionSlot.SlotKind.Tcs) return slot.Tcs;
                var resolved = NewTcs();
                if (slot.Kind == CompletionSlot.SlotKind.Result) resolved.SetResult(slot.Result);
                else resolved.SetException(slot.Exception);
                _slots[completionId] = new CompletionSlot(resolved);
                return resolved;
            }
            var tcs = NewTcs();
            _slots[completionId] = new CompletionSlot(tcs);
            return tcs;
        }
    }

    public bool TryComplete(int completionId, CompletionResult result)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;   // latched — drop late deliveries
            if (_slots.TryGetValue(completionId, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                    && slot.Tcs.TrySetResult(result);  // duplicate redelivery → false, no overwrite
            _slots[completionId] = new CompletionSlot(result);   // early completion, parked for later
            return true;
        }
    }

    public bool TryFail(int completionId, ushort code, string message)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;
            if (_slots.TryGetValue(completionId, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                    && slot.Tcs.TrySetException(new TerminalException(message, code));
            _slots[completionId] = new CompletionSlot(new TerminalException(message, code));
            return true;
        }
    }

    /// <summary>A buffered or already-delivered result exists for this id (Run replay dedup —
    /// the analogue of async_results.non_deterministic_find_id).</summary>
    public bool HasResultFor(int completionId)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(completionId, out var slot)
                && (slot.Kind != CompletionSlot.SlotKind.Tcs || slot.Tcs.Task.IsCompleted);
        }
    }

    /// <summary>
    ///     Atomic execute-vs-await decision for Run replay (1.7 case 2): returns false if a
    ///     result exists, was delivered, or the id was already claimed; otherwise marks the id
    ///     claimed-for-local-execution and returns true. Closes the TOCTOU where a late
    ///     RunCompletionNotification delivered by the pump between a bare HasResultFor check
    ///     and closure start would cause a duplicate side-effect execution + duplicate
    ///     ProposeRunCompletion. TryComplete still resolves a claimed slot's TCS normally.
    /// </summary>
    public bool TryClaimForExecution(int completionId)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;
            if (_slots.TryGetValue(completionId, out var slot)
                && (slot.Kind != CompletionSlot.SlotKind.Tcs || slot.Tcs.Task.IsCompleted))
                return false;
            return _claimed.Add(completionId);
        }
    }

    /// <summary>Faults every pending waiter (suspension / abort unwind), clears the table, and
    /// LATCHES the manager: all later GetOrRegister calls return pre-faulted TCSs.</summary>
    public void FailAll(Exception exception)
    {
        lock (_gate)
        {
            _terminal ??= exception;
            foreach (var pair in _slots)
                if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs
                    && pair.Value.Tcs.TrySetException(exception))
                    _ = pair.Value.Tcs.Task.Exception;   // mark observed: slots nobody awaits
                                                         // (e.g., un-awaited call results) must
                                                         // not raise UnobservedTaskException;
                                                         // awaiting still rethrows normally.
            _slots.Clear();
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            _terminal ??= new TaskCanceledException("Invocation completed");
            foreach (var pair in _slots)
                if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs)
                    pair.Value.Tcs.TrySetCanceled();
            _slots.Clear();
        }
    }

    private static TaskCompletionSource<CompletionResult> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

There is NO `PendingIds()` — the SuspensionMessage waiting sets are computed from the SM's
awaiting set inside `_commandLock` (1.5/2.5), so they contain exactly the ids parked awaiters
are awaiting (Rust `awaiting_on`/HitSuspensionPoint parity) with no snapshot-vs-registration
window. The production-unused `Register(int)` (lines 50-72) is DELETED; its
duplicate-registration tests in `CompletionManagerTests.cs` are removed/ported to
`GetOrRegister` semantics.

### 2.5 `src/Restate.Sdk/Internal/StateMachine/InvocationStateMachine.cs` — discharges B4; supports B1/B5/B7/B8

- Add the counters/constants from 1.1; delete `_nextSignalIndex` (line 31). [B4]
- Replace `_initialState` (line 38) with `_eagerState` + `_eagerStateIsPartial` (1.4). [B7]
- Add `private readonly object _commandLock = new();` and
  `private readonly SemaphoreSlim _flushGate = new(1, 1);` (1.6). [B5]
- Add suspension state — ALL guarded by `_commandLock`, no volatile/Interlocked needed (1.5
  point 3 — the lock supplies the fences the volatile store-load pair could not):
  `private bool _inputClosed;`, `private bool _suspended;`, `private int _executingRuns;`,
  `private readonly HashSet<(uint Id, NotificationKind Kind)> _awaiting = new();`, plus
  `internal enum NotificationKind { Completion, Signal }`. [B8]
- `Initialize` (lines 109-130): new signature

  ```csharp
  public void Initialize(string invocationId, byte[] rawInvocationId, string key, ulong randomSeed,
      int knownEntries, Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null,
      bool partialState = true)
  ```

  Body changes: `if (knownEntries < 1) throw new ProtocolException("known_entries is zero; expected at least the input entry");`
  (Rust `KNOWN_ENTRIES_IS_ZERO`, vm/transitions/input.rs:66 — resolved decision 4). Copy
  `eagerState` pairs into `_eagerState`; `_eagerStateIsPartial = partialState;`. Keep
  `State = knownEntries > 1 ? Replaying : Processing` as the provisional value (StartAsync
  finalizes it after buffering; note the comparison is now `> 1` because entry 0 is the Input
  consumed by StartAsync itself). Keep the convenience overload, updated to forward the new
  parameters.
- `Dispose` (lines 80-95): drop `_journal.Dispose()`; add `_flushGate.Dispose()`.
- `EnsureActive` (lines 132-136): suspension-aware and LOCKED (it reads `State`+`_suspended`,
  which are only coherent under `_commandLock` — a fan-out closure finishing concurrently with
  suspension must see SuspendedException, not a stale InvalidOperationException) —

  ```csharp
  private void EnsureActive()
  {
      lock (_commandLock)
      {
          if (State == InvocationState.Closed && _suspended) throw new SuspendedException();
          if (State is InvocationState.WaitingStart or InvocationState.Closed)
              ThrowInvalidState(State, "perform operations");
      }
  }
  ```
- NEW in-lock state check used at the top of every `lock (_commandLock)` section (keep
  `WriteCommand` as-is — after this change it is only ever called while holding
  `_commandLock`; `FlushAsync` is only ever called via `FlushGatedAsync`, 1.6):

  ```csharp
  // Caller MUST hold _commandLock.
  private void ThrowIfClosedLocked()
  {
      if (State == InvocationState.Closed)
          throw _suspended ? new SuspendedException()
                           : new InvalidOperationException("Invocation already closed");
  }
  ```
- NEW park API — the ONLY way any SDK code awaits a CompletionManager TCS (1.5 point 1):

  ```csharp
  /// <summary>
  ///     Single park point for all notification waits (Rust do_progress analogue): registers
  ///     the awaited id, enforces the replay-mutation guard, evaluates the suspension
  ///     condition at the await site, and deregisters on unpark.
  /// </summary>
  internal async ValueTask<CompletionResult> AwaitNotificationAsync(uint id, NotificationKind kind)
  {
      var manager = kind == NotificationKind.Completion ? _completions : _signalCompletions;
      var tcs = manager.GetOrRegister((int)id);
      if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);

      lock (_commandLock)
      {
          // UncompletedDoProgressDuringReplay parity (async_results.rs:50-112): a missing
          // completion while journaled commands remain proves an added await point.
          if (_journal.IsReplaying && !tcs.Task.IsCompleted)
              throw new ProtocolException(
                  $"Uncompleted await during replay (journal mutation / added await point): " +
                  $"awaiting {kind} id {id}; last command " +
                  $"{_journal.LastCommandType} '{_journal.LastCommandName}' " +
                  $"at index {_journal.CommandIndex}");
          _awaiting.Add((id, kind));
      }
      try
      {
          // EOF-before-park coverage: the pump may have closed input before we got here.
          await TrySuspendAsync().ConfigureAwait(false);
          return await tcs.Task.ConfigureAwait(false);
      }
      finally
      {
          lock (_commandLock) _awaiting.Remove((id, kind));
      }
  }
  ```
- NEW file `src/Restate.Sdk/Internal/SuspendedException.cs`:

  ```csharp
  namespace Restate.Sdk.Internal;

  /// <summary>
  ///     Control-flow sentinel mirroring sdk-shared-core's Suspended error (code 599):
  ///     unwinds a handler parked on uncompleted notifications after the runtime closed the
  ///     input stream. Never reported as an invocation failure.
  /// </summary>
  internal sealed class SuspendedException : Exception
  {
      public SuspendedException() : base("Invocation suspended") { }
  }
  ```
- NEW suspension entry point (called from the THREE trigger sites of 1.5: the park API after
  registering, the pump on EOF, and the Run epilogue after the `_executingRuns` decrement):

  ```csharp
  /// <summary>
  ///     HitSuspensionPoint (terminal.rs:18-53): suspend iff input is closed, no Run closure
  ///     is mid-flight (any_executing/WaitingPendingRun guard), and at least one parked
  ///     awaiter's id is still unresolved. The whole condition AND the Suspension write happen
  ///     under _commandLock, so suspension is atomic with respect to every other state
  ///     transition (no Dekker store-load race, no Error/End-after-Suspension interleaving,
  ///     no waiter registered-but-omitted window).
  /// </summary>
  internal async ValueTask TrySuspendAsync()
  {
      List<uint> waitingCompletions;
      List<uint> waitingSignals;
      lock (_commandLock)
      {
          if (State == InvocationState.Closed) return;   // already completed/aborted/suspended
          if (!_inputClosed || _executingRuns > 0) return;
          waitingCompletions = new List<uint>();
          waitingSignals = new List<uint>();
          foreach (var (id, kind) in _awaiting)
          {
              var manager = kind == NotificationKind.Completion ? _completions : _signalCompletions;
              if (manager.HasResultFor((int)id)) continue;   // resolved — that waiter will unpark
              if (kind == NotificationKind.Completion) waitingCompletions.Add(id);
              else waitingSignals.Add(id);
          }
          if (waitingCompletions.Count == 0 && waitingSignals.Count == 0) return;   // nobody truly parked
          waitingCompletions.Sort();
          waitingSignals.Sort();
          WriteCommand(MessageType.Suspension,
              ProtobufCodec.CreateSuspensionMessage(waitingCompletions, waitingSignals));
          State = InvocationState.Closed;
          _suspended = true;
      }
      await FlushGatedAsync(CancellationToken.None).ConfigureAwait(false);   // NO End frame after suspension
      _completions.FailAll(new SuspendedException());
      _signalCompletions.FailAll(new SuspendedException());
  }

  internal void MarkInputClosed() { lock (_commandLock) _inputClosed = true; }
  ```

  (`CompletionManager.HasResultFor` takes the manager's own lock inside `_commandLock`; the
  manager never takes `_commandLock`, so the lock order `_commandLock → manager._gate` is
  acyclic. Note a handler that did `ctx.Send` and then returned has NOTHING in `_awaiting` —
  EOF then produces no suspension and `CompleteAsync` proceeds to Output+End, the Rust
  sys_write_output behavior.)

### 2.6 `src/Restate.Sdk/Internal/StateMachine/InvocationStateMachine.Protocol.cs` — discharges B2/B3; supports B1/B7/B8

(a) `StartAsync` (lines 16-102):
- Pass the new `ParseStartMessage` fields to `Initialize(..., fields.EagerState, fields.PartialState)`.
- After parsing the InputCommand: `_journal.RecordCommand(JournalEntryType.Input);` (replaces
  the `Append(JournalEntry.Completed(...))` at line 62).
- Replay-buffer loop (replaces lines 67-87):

  ```csharp
  for (var i = 1; i < (int)fields.KnownEntries; i++)
  {
      var msg = await _reader.ReadMessageAsync(ct).ConfigureAwait(false)
                ?? throw new ProtocolException("Stream ended while reading known entries");
      if (msg.Header.Type.IsCommand())
          _journal.EnqueueReplay(ProtobufCodec.ParseReplayCommand(msg.Header.Type, msg.Payload));
      else if (msg.Header.Type.IsNotification())
          HandleIncomingMessage(msg);   // buffers into CompletionManager by WIRE id — early-completion slots
      else
          throw new ProtocolException($"Unexpected {msg.Header.Type} inside the known-entries replay batch");
      msg.Dispose();
  }
  State = _journal.IsReplaying ? InvocationState.Replaying : InvocationState.Processing;
  if (State == InvocationState.Processing) Log.ReplayCompleted(Logger, InvocationId);
  ```

  Both `IsCommand()` notifications counting and the `IsReplaying` redefinition implement Rust
  input.rs:79-148 (PostReceiveEntry counts every message toward entries_to_replay; commands →
  VecDeque, notifications → async_results). This kills the `Count < KnownEntries` hang/skip (B2)
  and removes the second wire reader (B3).

(b) `ProcessIncomingMessagesAsync` (lines 104-121) — suspension + EXHAUSTIVE abort unwind.
Adversarial-review correction: the pump now has throw sites of its own (the
command-after-preflight guard in (c), parse errors, reader IO failures). If ANY of them killed
the pump without `FailAll`, the parked handler would wait forever on TCSs nobody can resolve
and `HandleAsync` would never reach its finally — so the unwind clause catches `Exception`, not
just OCE:

```csharp
public async Task ProcessIncomingMessagesAsync(CancellationToken ct)
{
    try
    {
        while (State != InvocationState.Closed)
        {
            var message = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
            if (message is null)
            {
                Log.StreamEnded(Logger, InvocationId);
                MarkInputClosed();
                await TrySuspendAsync().ConfigureAwait(false);   // EOF-after-park trigger site
                break;
            }
            var msg = message.Value;
            HandleIncomingMessage(msg);
            msg.Dispose();
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // RST_STREAM / teardown: unpark any waiter so HandleAsync can unwind (defensive leak-stop).
        _completions.FailAll(new OperationCanceledException(ct));
        _signalCompletions.FailAll(new OperationCanceledException(ct));
        throw;
    }
    catch (Exception ex)
    {
        // Pump death (ProtocolException from (c), parse error, reader IO failure) must fault
        // every parked waiter — Rust parity: any do_transition error moves the VM to a terminal
        // error state observed by every subsequent poll. The handler unwinds with this
        // exception and HandleAsync's existing catch arms emit the ErrorMessage.
        _completions.FailAll(ex);
        _signalCompletions.FailAll(ex);
        throw;
    }
}
```

(c) `HandleIncomingMessage` (lines 123-192):
- A command-typed message after preflight is a protocol violation — add at the top:

  ```csharp
  if (type.IsCommand())
      throw new ProtocolException($"Unexpected command {type} outside the replay batch");
  ```

  (Previously commands were silently dropped.)
- Signal routing: skip the reserved built-in range with a debug log instead of feeding
  `_signalCompletions` — `if (signalIndex < FirstUserSignalId) { Log.BuiltInSignalIgnored(...); return; }`
  (cancellation handling proper remains out of scope; named signals (`signal.Name`) are also
  logged-and-ignored — pre-existing gap, unchanged).
- Completion routing is otherwise UNCHANGED — `notification.CompletionId` is already the wire
  id, which is now also the CompletionManager key, so the existing
  `TryComplete/TryFail((int)notification.CompletionId, ...)` becomes correct by construction.

(d) DELETE `ReplayNextEntryAsync` (lines 200-234) and `AdvanceReplayIndex` (lines 236-244).
Replacement (sync — no wire access, the B3 fix):

```csharp
// Caller MUST hold _commandLock (1.6 — id alloc, dequeue, and the State flip are one atomic
// section; both overloads forward to the matching InvocationJournal.DequeueReplay).
private ReplayCommand DequeueReplayCommand(JournalEntryType expectedType, string? expectedName = null)
{
    var command = _journal.DequeueReplay(expectedType, expectedName);
    if (!_journal.IsReplaying)
    {
        State = InvocationState.Processing;
        Log.ReplayCompleted(Logger, InvocationId);
    }
    return command;
}

/// <summary>
///     Non-determinism check: a replayed command's wire id must equal the locally re-allocated
///     one (counters advance identically across attempts). STRICT (adversarial-review
///     correction): every completable V4 command carries an id >= 1 — counters start at 1
///     precisely so 0 means field-unset (context.rs:106-107) and Rust's header_eq compares the
///     field unconditionally. This is only called for completable commands, so replayed == 0
///     means a corrupted/foreign journal, never a benign skip. (OneWayCall's
///     invocation_id_notification_idx is likewise always set — sys_send, mod.rs:816-836.)
/// </summary>
private void ValidateReplayCompletionId(uint replayed, uint allocated)
{
    if (replayed == 0)
        throw new ProtocolException(
            $"Corrupt journal at command index {_journal.CommandIndex}: " +
            "completable command missing its completion id");
    if (replayed != allocated)
        throw new ProtocolException(
            $"Non-deterministic replay at command index {_journal.CommandIndex}: " +
            $"journaled completion id {replayed}, locally allocated {allocated}");
}
```

(e) `MapMessageTypeToEntry` (lines 246-270): no longer needed by Protocol.cs (mapping moved
into `ParseReplayCommand`) — DELETE.

### 2.7 `src/Restate.Sdk/Internal/StateMachine/InvocationStateMachine.Operations.cs` — discharges B1/B5/B6(partial)/B7/B10b; biggest edit

Structure of every op after this rewrite (the 1.6 invariants made concrete):

1. A SYNCHRONOUS locked prefix — `lock (_commandLock) { ThrowIfClosedLocked(); allocate id(s);
   branch on State; replay → DequeueReplayCommand + ValidateReplayCompletionId, live →
   Serialize (inside the lock) + WriteCommand + RecordCommand }`. Id allocation, the replay
   dequeue, and the journal write are ONE atomic section — never split, never outside the lock.
2. An optional `await FlushGatedAsync(ct)` AFTER the lock (per-op flush policy unchanged).
3. Parking ONLY through `AwaitNotificationAsync` (2.5) — never a bare `tcs.Task`.

`AwaitCompletionAsync`/`WriteCommandGatedAsync` from earlier drafts do not exist; the park API
plus the locked prefix replace them. Keep `Log.AwaitingCompletion` inside
`AwaitNotificationAsync`.

#### 2.7.1 Replay-branch rewrite — all 26 sites

EVERY op follows one of four templates. NONE of them touch `replay.Result` or
`Deserialize<T>(replay.Result)` ever again.

Template A — completable, value from notification:

```csharp
uint completionId;
bool replaying;
lock (_commandLock)
{
    ThrowIfClosedLocked();
    completionId = NextCompletionId();                            // allocated in BOTH branches
    replaying = State == InvocationState.Replaying;
    if (replaying)
    {
        var cmd = DequeueReplayCommand(JournalEntryType.X, name); // dequeue + verify type/name
        ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
    }
    else
    {
        WriteCommand(MessageType.X, ProtobufCodec.CreateX(..., completionId));
        _journal.RecordCommand(JournalEntryType.X, name);
    }
}
if (!replaying) await FlushGatedAsync(ct).ConfigureAwait(false);
// Park API enforces the UncompletedDoProgressDuringReplay guard (no buffered completion while
// replay continues → ProtocolException, never a hang/suspend-loop) and the at-await suspension
// check; value comes from the buffered/late notification.
var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
completion.ThrowIfFailure();                                      // Failure notification → TerminalException
return /* deserialize completion.Value per-op */;
```

Template B — non-completable command (no id):

```csharp
lock (_commandLock)
{
    ThrowIfClosedLocked();
    if (State == InvocationState.Replaying) { DequeueReplayCommand(JournalEntryType.X, name); return; }
    WriteCommand(...);                                            // Serialize inside the lock if needed
    _journal.RecordCommand(JournalEntryType.X, name);
}
// per-op flush policy
```

Template C — Call (two ids, replaces the dummy-slot hack; target triple validated):

```csharp
uint invocationIdCompletionId, resultCompletionId;
bool replaying;
lock (_commandLock)
{
    ThrowIfClosedLocked();
    invocationIdCompletionId = NextCompletionId();   // FIRST — sys_call order, vm/mod.rs:742-744
    resultCompletionId = NextCompletionId();         // SECOND
    replaying = State == InvocationState.Replaying;
    if (replaying)
    {
        var cmd = DequeueReplayCommand(JournalEntryType.Call, name: null,
            expectedService: service, expectedHandler: handler, expectedKey: key);
        ValidateReplayCompletionId(cmd.InvocationIdNotificationIdx, invocationIdCompletionId);
        ValidateReplayCompletionId(cmd.ResultCompletionId, resultCompletionId);
    }
    else
    {
        var requestBytes = SerializeObject(request);              // shared buffer — inside the lock
        WriteCallCommandMessage(service, handler, key, requestBytes,
            invocationIdCompletionId, resultCompletionId);
        _journal.RecordCommand(JournalEntryType.Call);
    }
}
if (!replaying) await FlushGatedAsync(ct).ConfigureAwait(false);
var completion = await AwaitNotificationAsync(resultCompletionId, NotificationKind.Completion).ConfigureAwait(false);
completion.ThrowIfFailure();
return Deserialize<TResponse>(completion.Value);
```

Template D — Run (resolved decision 4 as NARROWED in 1.7 — frontier-only inline execution,
atomic claim, fail-fast on journal mutation):

```csharp
uint completionId;
bool replaying, stillReplaying = false;
lock (_commandLock)
{
    ThrowIfClosedLocked();
    completionId = NextCompletionId();
    replaying = State == InvocationState.Replaying;
    if (replaying)
    {
        var cmd = DequeueReplayCommand(JournalEntryType.Run, name);
        ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
        stillReplaying = _journal.IsReplaying;          // false ⇔ this Run was the replay frontier
    }
    else
    {
        WriteRunCommand(name, completionId);
        _journal.RecordCommand(JournalEntryType.Run, name);
    }
}
if (replaying)
{
    if (stillReplaying && !_completions.HasResultFor((int)completionId))
        throw new ProtocolException(
            $"Uncompleted Run '{name}' during replay with later journaled commands — " +
            "journal mutation (UncompletedDoProgressDuringReplay parity); refusing to " +
            "re-execute a side effect mid-replay");                       // 1.7 case 3
    if (!stillReplaying && _completions.TryClaimForExecution((int)completionId))
        await ExecuteAndProposeRunAsync(name, action, completionId, retryPolicy, ct)
            .ConfigureAwait(false);                                       // 1.7 case 2 (frontier resume)
    // else: 1.7 case 1 — buffered/raced completion; consume it below without executing.
}
else
{
    await FlushGatedAsync(ct).ConfigureAwait(false);
    await ExecuteAndProposeRunAsync(name, action, completionId, retryPolicy, ct).ConfigureAwait(false);
}
var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
completion.ThrowIfFailure();                                              // B10b: failure re-raises here,
                                                                          // AFTER durable storage (1.7)
return Deserialize<T>(completion.Value);   // or the locally computed value when this attempt executed (2.7.2)
```

Per-site mapping (current line → template):

| Site (current lines) | Op | Template | Notes |
|---|---|---|---|
| RunSync replay 16-17/37-41 | Run | D | `RunSyncReplayAsync` deleted; RunSync delegates to the shared Run core |
| RunAsync\<T\> 56-60 | Run | D | |
| RunAsync void 85-89 | Run | D | result ignored, failure still throws |
| RunFutureAsync 216-223 | Run | D | non-blocking shape — see 2.7.2; future parks via thunk `() => AwaitNotificationAsync(completionId, Completion)` |
| CallAsync 272-276 | Call | C | |
| SendAsync 305-310 | OneWayCall | C-variant | one id (`InvocationIdNotificationIdx`); handle wraps a lazy resolve THUNK over the park API (2.7.4) — NO UTF-8 decode of command bytes |
| CallFutureAsync 337-353 | Call | C | returns park thunk for `resultCompletionId` (2.10); the `_journal.Count - 1` fallback (line 348) is deleted |
| SleepFutureAsync 381-387 | Sleep | A | returns park thunk instead of awaiting; `replayIndex` (line 383) deleted |
| AttachInvocationAsync 409-413 | AttachInvocation | A | |
| GetInvocationOutputAsync 436-441 | GetInvocationOutput | A | empty completion → `default` |
| GetStateAsync 475-479 | GetState | A+eager | see 2.7.3 |
| SetState 504-508 | SetState | B | cache mutation happens BEFORE the branch (2.7.3) |
| ClearState 524-528 | ClearState | B | ditto |
| ClearAllState 541-545 | ClearAllState | B | ditto |
| GetStateKeysAsync 557-561 | GetStateKeys | A+eager | eager → `Deserialize<string[]>(cmd.EagerValue)` |
| SleepAsync 585-589 | Sleep | A | void value |
| ResolveAwakeable 647-651 | CompleteAwakeable | B | |
| RejectAwakeable 663-667 | CompleteAwakeable | B | |
| GetPromiseAsync 681-685 | GetPromise | A | |
| PeekPromiseAsync 707-711 | PeekPromise | A | empty → default |
| ResolvePromise 732-736 | CompletePromise | B+id | allocates `NextCompletionId()` before the branch (proto field 11) but does not await it (sync API); see 2.7.5 |
| RejectPromise 751-755 | CompletePromise | B+id | ditto |
| CallAsync\<TReq,TResp\> 772-776 | Call | C | |
| SendAsync\<TReq\> 805-810 | OneWayCall | C-variant | |
| CancelInvocationAsync 835-839 | SendSignal | B | |
| CallAsync idem-key 867-871 | Call | C | |

#### 2.7.2 Run core (Processing path) — B5 + B10b

Consolidate `RunSync`/`RunAsync<T>`/`RunAsync(void)`/`RunFutureAsync`/`ExecuteWithRetryAsync`
around one core. `WriteRunCommand`/`WriteRunProposal` (lines 195-207) change signature to take
the id explicitly — the racy `(uint)_journal.Count` reads are deleted:

```csharp
private void WriteRunCommand(string name, uint completionId) =>
    WriteCommand(MessageType.RunCommand, ProtobufCodec.CreateRunCommand(name, completionId));

private void WriteRunProposal(uint completionId, ReadOnlySpan<byte> serialized) =>
    WriteCommand(MessageType.ProposeRunCompletion, ProtobufCodec.CreateRunProposal(completionId, serialized));
```

Every blocking Run variant IS Template D verbatim (the template subsumes both branches; RunSync
inlines it without the retry loop; the void overload skips serialization). The locally computed
value is preferred over re-deserializing the notification payload:

```csharp
public async ValueTask<T> RunAsync<T>(string name, Func<Task<T>> action, CancellationToken ct,
    RetryPolicy? retryPolicy = null)
{
    EnsureActive();
    /* Template D's locked prefix + flush (2.7.1) runs here verbatim, yielding completionId,
       replaying, stillReplaying — including the case-3 mutation throw. This attempt executes
       the closure iff it is the live path or the claimed replay frontier: */
    var executesLocally = !replaying
        || (!stillReplaying && _completions.TryClaimForExecution((int)completionId));
    var (hasValue, value) = executesLocally
        ? await ExecuteAndProposeRunAsync(name, action, completionId, retryPolicy, ct).ConfigureAwait(false)
        : (false, default(T)!);

    // The notification await is the ACK BARRIER in both directions (1.7): success returns only
    // after the runtime acked durable storage; terminal failure surfaces HERE (ThrowIfFailure),
    // never by rethrowing the closure's exception — so saga compensations cannot run before
    // the failure is durable, and closed-input failure converts to suspension (Rust parity).
    var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
    completion.ThrowIfFailure();
    Log.SideEffectExecuted(Logger, name, InvocationId);
    return hasValue ? value : Deserialize<T>(completion.Value);
}

/// <summary>Executes the closure and proposes its outcome. NEVER rethrows TerminalException —
/// the failure travels through the proposal → notification → ThrowIfFailure path (1.7).
/// Single body shared by the blocking variants (Template D) and RunFutureAsync.</summary>
private async Task<(bool HasValue, T Value)> ExecuteAndProposeRunAsync<T>(string name,
    Func<Task<T>> action, uint completionId, RetryPolicy? retryPolicy, CancellationToken ct)
{
    lock (_commandLock) _executingRuns++;   // any_executing guard for suspension (1.5)
    try
    {
        T value;
        try
        {
            value = retryPolicy is not null
                ? await ExecuteWithRetryAsync(name, action, retryPolicy, ct).ConfigureAwait(false)
                : await action().ConfigureAwait(false);
        }
        catch (TerminalException ex)
        {
            // Terminal failure (thrown directly or via retry exhaustion): propose FAILURE and
            // fall through — the caller's notification await re-raises after durability.
            lock (_commandLock)
            {
                ThrowIfClosedLocked();
                WriteCommand(MessageType.ProposeRunCompletion,
                    ProtobufCodec.CreateRunProposalFailure(completionId, ex.Code, ex.Message));
            }
            await FlushGatedAsync(ct).ConfigureAwait(false);
            return (false, default!);
        }
        // Success: serialize INSIDE the lock (shared _serializeBuffer), propose by captured id.
        lock (_commandLock)
        {
            ThrowIfClosedLocked();
            var serialized = Serialize(value);
            WriteRunProposal(completionId, serialized.Span);
        }
        await FlushGatedAsync(ct).ConfigureAwait(false);
        return (true, value);
    }
    finally
    {
        lock (_commandLock) _executingRuns--;
        // Run-epilogue trigger site (1.5 point 3): with the run no longer executing, a
        // closed-input invocation whose handler is parked on this run's notification (or any
        // other id) can now suspend. TrySuspendAsync re-checks the whole condition internally.
        await TrySuspendAsync().ConfigureAwait(false);
    }
}
```

Detached-execution failure containment: when `ExecuteAndProposeRunAsync` runs as a detached
task (RunFutureAsync below) and dies of an INFRASTRUCTURE exception (write failure — not a
TerminalException, which is proposed), the task wrapper must
`_completions.TryFail((int)completionId, 500, ex.Message)` (or fault the slot's TCS) so the
future's eventual awaiter observes the failure and no task exception goes unobserved.

`ExecuteWithRetryAsync` (lines 107-193): the exhaustion arms (128-141, 174-184) STOP writing
`WriteRunCommand` + proposal + journal append themselves (the command is already journaled by
the prefix; the local `JournalEntry.Completed(..., Empty, ...)` append — the B10b empty-success
defect — is gone with the JournalEntry struct). They now simply
`throw new TerminalException($"Run '{name}' failed after {attempt + 1} attempt(s): {ex.Message}", 500);`
and the catch in `ExecuteAndProposeRunAsync` proposes the failure with the captured id.

`RunFutureAsync` (lines 211-240) becomes NON-blocking with a fully synchronous journaling
prefix (1.6 — the command is journaled before the method returns, so fan-out creation order ==
journal order even though the closure runs detached):

- Run the Template D locked prefix synchronously (alloc id; replay → dequeue/validate/frontier
  logic incl. the case-3 ProtocolException; processing → WriteRunCommand + RecordCommand).
- Processing (and replay frontier with a successful claim): start
  `var execution = Task.Run(() => ExecuteAndProposeRunAsync(name, action, completionId, retryPolicy, ct))`
  DETACHED, with the infrastructure-failure containment wrapper above; do NOT await it.
- Return `(Resolve: () => AwaitNotificationAsync(completionId, NotificationKind.Completion),
  CompletionId: completionId)` — `LazyRunFuture<T>` parks through the thunk (2.10) and
  deserializes `completion.Value` on first `GetResult`. There is no `(tcs, value)` tuple and
  no `HasValue` fast path: the future ALWAYS resolves from the notification (the ack barrier),
  which keeps `DefaultContext.RunAsync`'s un-awaited-storage shape (DefaultContext.cs:76-86)
  correct without ever handing a bare TCS across the API. Flush of the prefix happens via
  `FlushGatedAsync` fired before returning (await it — flushing is cheap and keeps the
  RunCommand on the wire before user code proceeds, matching the blocking path).

#### 2.7.3 Eager state (B7)

`GetStateAsync<T>` (lines 464-495) — full rewrite:

```csharp
/// <summary>
///     ONE decode rule for state values, applied IDENTICALLY on every path — fresh eager hit,
///     replayed GetEagerStateCommand, lazy notification — so fresh and replayed runs can never
///     diverge on the same payload. Void/cleared (known-absent) → default. Empty Value payload
///     → default as well: this NORMALIZATION (an empty payload is treated as absent on every
///     path, matching the fork's existing lazy-path behavior) is a documented divergence from
///     Rust, which hands empty bytes to the deserializer (journal.rs:426-432); see §5.
/// </summary>
private T? DeserializeStateValue<T>(bool isVoid, ReadOnlyMemory<byte> value) =>
    isVoid || value.IsEmpty ? default : Deserialize<T>(value);

public async ValueTask<T?> GetStateAsync<T>(string key, CancellationToken ct)
{
    EnsureActive();
    uint completionId;
    ReplayCommand? replayed = null;
    var wroteLazy = false;
    (bool IsVoid, ReadOnlyMemory<byte> Value)? eagerHit = null;

    lock (_commandLock)
    {
        ThrowIfClosedLocked();
        completionId = NextCompletionId();   // SysStateGet allocates unconditionally (journal.rs:301)
        if (State == InvocationState.Replaying)
        {
            var cmd = DequeueReplayCommand(JournalEntryType.GetState, key);  // GetEagerState OR GetLazyState
            if (!cmd.HasEagerResult) ValidateReplayCompletionId(cmd.ResultCompletionId, completionId);
            replayed = cmd;
        }
        else if (_eagerState.TryGetValue(key, out var eager))   // Value or cleared-marker (null)
        {
            WriteCommand(MessageType.GetEagerStateCommand,
                ProtobufCodec.CreateGetEagerStateCommand(key, eager));
            _journal.RecordCommand(JournalEntryType.GetState, key);
            eagerHit = (eager is null, eager ?? ReadOnlyMemory<byte>.Empty);
        }
        else if (!_eagerStateIsPartial)                          // complete map: absent == known-empty
        {
            WriteCommand(MessageType.GetEagerStateCommand,
                ProtobufCodec.CreateGetEagerStateCommand(key, null));
            _journal.RecordCommand(JournalEntryType.GetState, key);
            eagerHit = (true, ReadOnlyMemory<byte>.Empty);
        }
        else                                                     // Unknown under partial → real roundtrip
        {
            WriteCommand(MessageType.GetLazyStateCommand,
                ProtobufCodec.CreateGetStateCommand(key, completionId));
            _journal.RecordCommand(JournalEntryType.GetState, key);
            wroteLazy = true;
        }
    }

    if (replayed is { HasEagerResult: true } eagerCmd)
        return DeserializeStateValue<T>(eagerCmd.EagerIsVoid, eagerCmd.EagerValue);  // Void ≠ empty Value:
                                                                                     // branch is on the oneof,
                                                                                     // never on payload length
    if (eagerHit is { } hit)
        return DeserializeStateValue<T>(hit.IsVoid, hit.Value);

    if (wroteLazy) await FlushGatedAsync(ct).ConfigureAwait(false);
    var completion = await AwaitNotificationAsync(completionId, NotificationKind.Completion).ConfigureAwait(false);
    completion.ThrowIfFailure();
    // CompletionResult encodes Void as an empty Value today; the unified helper maps both
    // Void and empty payload to default, so this path is byte-for-byte consistent with the
    // eager and replayed-eager paths above.
    return DeserializeStateValue<T>(isVoid: false, completion.Value);
}
```

`SetState<T>` (lines 500-518) — cache mutation FIRST and unconditional (Rust sys_state_set
mutates before do_transition, mod.rs:596-612):

```csharp
public void SetState<T>(string key, T value)
{
    EnsureActive();
    lock (_commandLock)   // sync ops take the SAME Monitor as async prefixes — no barging,
    {                     // no overtaking a queued async writer (the SemaphoreSlim hazard)
        ThrowIfClosedLocked();
        var serialized = Serialize(value);                 // shared buffer — inside the lock
        _eagerState[key] = CopyToPooled(serialized);       // unconditional, replay included
        if (State == InvocationState.Replaying)
        {
            DequeueReplayCommand(JournalEntryType.SetState, key);   // inside the SAME section
        }
        else
        {
            WriteCommand(MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand(key, serialized.Span));
            _journal.RecordCommand(JournalEntryType.SetState, key);
        }
    }
}
```

`ClearState` (lines 520-535): same single-`lock (_commandLock)` shape as SetState —
`_eagerState[key] = null;` (marker — NOT `Remove`, per EagerState::clear) unconditionally;
then replay-dequeue or write+record, all inside the one locked section.
`ClearAllState` (lines 537-551): ditto with `_eagerState.Clear(); _eagerStateIsPartial = false;`
(EagerState::clear_all).

`GetStateKeysAsync` (lines 553-577): same shape as GetStateAsync — allocate id; replay →
dequeue `GetStateKeys`, eager → `Deserialize<string[]>(cmd.EagerValue)`, lazy → await id;
processing → if `!_eagerStateIsPartial`, collect
`_eagerState.Where(p => p.Value is not null).Select(p => p.Key).Order().ToArray()`, write
`CreateGetEagerStateKeysCommand(keys)` + record, return keys; else lazy command as today (with
the wire `completionId`).

#### 2.7.4 Calls and Sends (B1 two-id + B6 step 1)

`CallAsync` (267-298), `CallAsync<TReq,TResp>` (767-797), `CallAsync` idem-key (862-894),
`CallFutureAsync` (332-372) — all are Template C verbatim (dummy-slot blocks at 280-283 /
357-360 / 780-783 / 875-878 DELETED; the locked prefix serializes the request inside
`_commandLock`, the result await goes through the park API). Do NOT pre-register the
invocation-id slot or start a resolution task: an early `CallInvocationIdCompletionNotification`
parks as an early-completion slot in the manager by itself, and a future CallHandle consumer
resolves it on demand through the park API — eager registration would recreate the
spurious-suspension hazard fixed in 2.7.4's Send shape below. `CallFutureAsync` returns the
resolve thunk for `resultCompletionId` (2.10) instead of awaiting.

`SendAsync` both overloads (300-328, 799-827) — Phase 1 keeps the existing blocking await but
keys it by the WIRE id (`invocationIdCompletionId = NextCompletionId()`; locked prefix writes
`WriteSendCommandMessage(..., invocationIdCompletionId)` + record, then flush; await via the
park API; build eager `InvocationHandle`). Replay validates the target triple via the
`DequeueReplay` overload (OneWayCall carries service/handler/key like Call). Phase 2a (B6) then
deletes the await:

```csharp
// Phase 2a final shape — fire-and-forget (Rust sys_send returns SendHandle immediately).
// NOTE (adversarial-review correction): do NOT call GetOrRegister or start a resolution task
// here — an eagerly-started Task is a parked awaiter from birth, which would put the send's id
// into the awaiting set and spuriously suspend a handler that sends-then-returns. The THUNK
// runs only when the user awaits GetInvocationIdAsync(), making that await an explicit
// suspension point through the park API (1.5/1.8).
/* locked prefix: write + record; then await FlushGatedAsync(ct) */
return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));

private async Task<string> ResolveInvocationIdAsync(uint invocationIdCompletionId)
{
    var completion = await AwaitNotificationAsync(invocationIdCompletionId, NotificationKind.Completion)
        .ConfigureAwait(false);
    completion.ThrowIfFailure();
    return completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span);
}
```

Replay branches (305-310, 805-810) in Phase 2a: dequeue + validate (type, name, target triple,
`InvocationIdNotificationIdx`), then
`return new InvocationHandle(() => ResolveInvocationIdAsync(invocationIdCompletionId));`
— the buffered/late `CallInvocationIdCompletionNotification` resolves it on demand. The UTF-8
decode of raw command bytes is gone.

#### 2.7.5 Remaining ops

- `SleepAsync`/`SleepFutureAsync` (581-604, 376-401): Template A; the locked prefix writes
  `CreateSleepCommand(wakeUpTime, completionId)` (name populated → field 12), flush, then
  park-API await / return the resolve thunk.
- `AttachInvocationAsync` (405-430) / `GetInvocationOutputAsync` (432-460) /
  `GetPromiseAsync` (677-701) / `PeekPromiseAsync` (703-726): Template A with their existing
  Create* codec calls taking the wire `completionId`.
- `Awakeable()` (613-622): `var signalId = NextSignalId();` (first = 17, B4);
  `_signalCompletions.GetOrRegister((int)signalId)`; `BuildAwakeableId((int)signalId)`
  unchanged otherwise.
- `ResolveAwakeable`/`RejectAwakeable` (643-673): Template B; processing → locked write +
  `RecordCommand(CompleteAwakeable)` (no flush — same buffered-write policy as today).
- `ResolvePromise`/`RejectPromise` (728-763): allocate `NextCompletionId()` in both branches
  (counter determinism; proto CompletePromiseCommandMessage.result_completion_id); replay →
  Template B + `ValidateReplayCompletionId(cmd.ResultCompletionId, completionId)`; processing →
  locked write with the wire id + record. The ack notification, when it arrives, lands as an
  early-completion slot — benign (public API is sync void; awaiting the ack is a noted
  residual gap).
- `CancelInvocationAsync` (831-849): Template B; processing → locked write + record + flush.
- TERMINAL OPS — `CompleteAsync` (901-912) / `FailTerminalAsync` (918-929) / `FailAsync`
  (936-951) are each ONE locked section (adversarial-review correction: the current pre-gate
  `State` checks are stale by the time anything is written, letting an ErrorMessage+End land
  AFTER a concurrent Suspension frame — a protocol violation). Required shape, all three:

  ```csharp
  lock (_commandLock)
  {
      if (State == InvocationState.Closed)
      {
          if (_suspended) throw new SuspendedException();   // CompleteAsync; Fail* return silently
          return;                                            // raced normal close
      }
      /* CompleteAsync only — replay parity with Rust SysWriteOutput pop (mod.rs:1292-1312): */
      if (State == InvocationState.Replaying)
      {
          DequeueReplayCommand(JournalEntryType.Output);
          // Rust SysEnd succeeds only in Processing: commands left AFTER the journaled Output
          // mean the journal recorded MORE than the code produced — a non-determinism signal,
          // never silently swallowed (terminal.rs:56-73).
          if (_journal.IsReplaying)
              throw new ProtocolException(
                  "Journal contains commands after Output — non-deterministic replay");
          // skip the Output write; fall through to End below
      }
      else
      {
          var output = serializeResult();   // see signature change below — INSIDE the lock
          WriteCommand(MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(output.Span));
          // (FailTerminalAsync: CreateOutputFailure; FailAsync: CreateErrorMessage instead)
      }
      _writer.WriteHeaderOnly(MessageType.End);
      State = InvocationState.Closed;
  }
  await FlushGatedAsync(ct).ConfigureAwait(false);
  ```

  Because Suspension (2.5) and End are both written under `_commandLock` behind a `State`
  re-check, "frames after Suspension" is structurally impossible.

  SIGNATURE CHANGE (torn-OutputCommand fix): `CompleteAsync` no longer accepts pre-serialized
  `ReadOnlyMemory<byte>` — `InvocationHandler.HandleAsync` currently calls
  `sm.SerializeObject(result)` UNGATED and passes memory aliasing the shared `_serializeBuffer`,
  which a straggler Run proposal can overwrite before the copy (2.8). New signature:
  `CompleteAsync(object? result, CancellationToken ct)` performing
  `result is null ? ReadOnlyMemory<byte>.Empty : SerializeObject(result)` INSIDE the lock (or
  equivalently `CompleteAsync(Func<ReadOnlyMemory<byte>> serializeResult, ct)` — pick one and
  apply at the single call site).

### 2.8 `src/Restate.Sdk/Internal/InvocationHandler.cs` — discharges B8 (handler side)

(a) RESULT PATH: replace the ungated serialize + pass-bytes pair (lines 64-67,
`var output = result is not null ? sm.SerializeObject(result) : ...; await sm.CompleteAsync(output, ct)`)
with the new `CompleteAsync` shape from 2.7.5 — `await sm.CompleteAsync(result, ct)` — so the
result is serialized inside `_commandLock` and can no longer be torn by a straggler Run
proposal sharing `_serializeBuffer`.

(b) SUSPENSION CATCH — the load-bearing invariant is ORDERING, not a line number: the new arm
MUST appear before every arm that writes to the wire, and in particular before
`catch (Exception)` (whose `FailAsync` would otherwise emit an Error frame after the Suspension
frame — a protocol violation). The first arm today is `catch (TerminalException)` (~line 71);
place `catch (SuspendedException)` directly before it as arm #1:

```csharp
catch (SuspendedException)
{
    // The state machine already wrote the SuspensionMessage and closed the output —
    // protocol.proto:88-97. No Output/Error frame may follow.
    Log.InvocationSuspended(logger, sm.InvocationId);   // new LoggerMessage in Internal/Log.cs
}
```

Additionally, every other catch arm's `sm.Fail*Async(...)` can now itself throw
`SuspendedException` (a suspension raced the failure; the SM is Closed+suspended and the
locked re-check in 2.7.5 throws). The existing bare `catch { }` wrappers around those calls
swallow it — that behavior is CORRECT and load-bearing; keep the wrappers and add a comment
saying so. Invariant test: §4.7's wire-order assertion (no Error/Output/End frame ever follows
a Suspension frame in any scenario).

(c) PUMP-FAULT TOLERANCE in the `finally` (lines 104-128): the pump can now die with
`ProtocolException`/IO errors (2.6(b)) which `await incomingTask` would rethrow PAST the
existing OCE-only catch and escape into Kestrel after the response is already terminated.
Broaden it:

```csharp
if (incomingTask is not null)
    try { await incomingTask.ConfigureAwait(false); }
    catch (OperationCanceledException ex) { Log.IncomingReaderStopped(logger, ex, sm.InvocationId); }
    catch (Exception ex)
    {
        // Pump fault already surfaced to the handler through the faulted TCSs (FailAll in
        // 2.6(b)) and was reported via the catch arms above — log only, never rethrow from finally.
        Log.IncomingReaderFaulted(logger, ex, sm.InvocationId);   // new Debug-level LoggerMessage
    }
```

Add the `InvocationSuspended` (Information), `BuiltInSignalIgnored` (Debug) and
`IncomingReaderFaulted` (Debug) entries to `Internal/Log.cs`.

### 2.9 `src/Restate.Sdk/InvocationHandle.cs` — discharges B6 (API side) — Phase 2a

Replace the file with the sealed class from section 1.8. Compile-fix sweep:
`Restate.Sdk.Testing/MockContext.cs` (eager ctor — compiles as-is),
`Restate.Sdk.Generators/Emitters/ClientEmitter.cs` (only references the type name — fine), any
samples using `.InvocationId` switch to `await handle.GetInvocationIdAsync()`, AND
`test/Restate.Sdk.Tests/OptionsTests.cs` — `InvocationHandle_StoresId` (lines 48-52) reads the
removed `handle.InvocationId` property; replace it with the lazy-handle unit tests of §4.5
cases 5-7 (eager ctor resolves synchronously; thunk ctor resolves when invoked; repeated
`GetInvocationIdAsync` calls return the same value).

### 2.10 `src/Restate.Sdk/Internal/DurableFuture.cs` — futures park via thunk (Phase 1) + B10d deletion (Phase 2b)

PHASE 1 (required by 1.5 point 1 — `GetResult` currently awaits a bare TCS at
DurableFuture.cs:40, bypassing every suspension/replay-guard hook): `DurableFuture<T>`,
`VoidDurableFuture`, `LazyRunFuture<T>`, `LazyCallFuture<T>`, `LazyTimerFuture` are constructed
with a resolve thunk `Func<ValueTask<CompletionResult>>` supplied by the SM
(`() => sm.AwaitNotificationAsync(id, kind)`) instead of a `TaskCompletionSource`. First
`GetResult()` invokes and caches it; `ThrowIfFailure` + deserialize as today. The
combinator-facing `internal Task<CompletionResult>? Task` accessor is reimplemented over the
cached task. The pre-completed constructor path (`Completed(value)`) is unchanged.
`RunFutureAsync`'s consumers adapt per 2.7.2 (resolve-thunk shape — there is no
`(tcs, value)`/`HasValue` tuple).

PHASE 2b (B10d): delete `CachedCompleted` (line 66), `Completed()` (92-95), `CreateCompleted()`
(97-103) from `VoidDurableFuture`. Zero PRODUCTION call sites (verified by grep; the only
construction is `new VoidDurableFuture(task.Result)` at DefaultContext.cs:92) — but
`test/Restate.Sdk.Tests/DurableFutureTests.cs:84` (`VoidFuture_Completed_IsImmediate`) calls
`VoidDurableFuture.Completed()`: this lane MUST also delete that test (or port it to the
surviving constructor path) or the test project does not compile.

### 2.11 `src/Restate.Sdk/Internal/Context/DefaultContext.cs` + sibling contexts

- `Now()` (29-32): UNCHANGED — `__restate_now` fixed name is correct (B10a non-bug; see §5).
- `RunAsync` (76-86): adapt to the resolve-thunk shape (2.7.2/2.10) — the returned future
  always resolves from the notification; there is no completed-value fast path (the
  notification IS the ack barrier; a pre-ack fast path would leak un-acked values).
- `Timer` (88-94), `CallFuture*` (96-115), `Call*`, `Attach`, `GetOutput`, `Sleep`,
  `Awakeable` (215-236), promises: signatures unchanged; recompile only.
- `Send` overloads (145-155, 193-198): unchanged signatures (`ValueTask<InvocationHandle>`);
  the handle they forward is now lazy after Phase 2a.
- `DefaultObjectContext`/`DefaultWorkflowContext`/shared variants: recompile only (state and
  promise methods route through the rewritten SM ops).

---

## 3. Opus execution sequencing

Phases are strictly ordered; a phase starts only after the previous phase's gate is green.

### Phase 1 — FOUNDATION (ONE executor, sequential, single commit-series)

Files (edit in this order; they cannot compile independently — do NOT split across agents):
1. `Internal/Journal/JournalEntry.cs` (2.2)
2. `Internal/Journal/InvocationJournal.cs` (2.1)
3. `Internal/Protocol/ProtocolTypes.cs` + `Internal/Protocol/ProtobufCodec.cs` (2.3)
4. `Internal/Journal/CompletionManager.cs` (2.4)
5. `Internal/SuspendedException.cs` + `Internal/StateMachine/InvocationStateMachine.cs` (2.5)
6. `Internal/StateMachine/InvocationStateMachine.Protocol.cs` (2.6)
7. `Internal/StateMachine/InvocationStateMachine.Operations.cs` (2.7 — Send kept blocking-but-id-correct)
8. `Internal/InvocationHandler.cs` + `Internal/Log.cs` (2.8)
9. `Internal/DurableFuture.cs` resolve-thunk adaptation (2.10 Phase 1 — ALL futures park via
   the SM park API) + `Internal/Context/DefaultContext.cs` compile fixes (2.11)
10. Compile-fix existing tests: `Journal/JournalTests.cs` (rewrite against queue API),
    `Journal/CompletionManagerTests.cs` (drop Register-specific cases),
    `StateMachine/InvocationStateMachineTests.cs` (`Initialize(..., 0, ...)` → expects
    `ProtocolException`; other inits use `knownEntries: 1`),
    `DurableFutureTests.cs` (TCS-backed constructor cases → resolve-thunk shape),
    `Integration/*` (unchanged expectations — fresh-invocation flows must still pass).

GATE 1: `dotnet build` warnings-clean for changed files + `dotnet test` fully green.
Commit checkpoint(s) allowed within the phase, but the phase is one executor's linear work.

### Phase 2 — parallel lanes (independent files; each lane gets its own executor)

- Lane 2a (B6): `InvocationHandle.cs` (2.9) + `Operations.cs` Send sections only (2.7.4 final
  shape) + `MockContext`/samples compile sweep + `test/Restate.Sdk.Tests/OptionsTests.cs`
  (`InvocationHandle_StoresId` → §4.5.5-6 lazy-handle tests). Owns `Operations.cs` exclusively
  in this phase.
- Lane 2b (B10d): `DurableFuture.cs` dead-code deletion (2.10 Phase 2b) +
  `test/Restate.Sdk.Tests/DurableFutureTests.cs` (`VoidFuture_Completed_IsImmediate` deleted or
  ported — the deletion does NOT compile without this). No file overlap with 2a.
- Lane 2c (docs): update `docs/`/ARCHITECTURE notes describing the new model (no src/ files).

GATE 2: `dotnet build` + `dotnet test` green after lanes merge.

### Phase 3 — test authoring (parallel by area; see §4 for file/case spec)

- Step 0 (lane 3a, FIRST commit — other lanes depend on it): extract
  `Testing/ProtocolTestHarness.cs` (§4 preamble — framed-message helpers, `AwaitBounded`
  watchdog, wire-order assertion helper).
- Lane 3a: `StateMachine/ReplayTests.cs` + `StateMachine/SingleReaderTests.cs` (B1/B2/B3 +
  replay-mutation guards)
- Lane 3b: `StateMachine/EagerStateTests.cs` (B7) + `StateMachine/SignalIndexTests.cs` (B4)
- Lane 3c: `StateMachine/SuspensionTests.cs` (B8) + `StateMachine/SendHandleTests.cs` (B6)
- Lane 3d: `StateMachine/RunFanOutTests.cs` (B5/B10b) + `Journal/CompletionManagerRaceTests.cs` (B9)
- Lane 3e: `StateMachine/PromiseTests.cs` (§4.10) + the Template A/B replay smoke matrix for
  AttachInvocation / GetInvocationOutput / ResolveAwakeable / RejectAwakeable /
  CancelInvocationAsync (§4.1.16)

Test lanes only ADD files under `test/Restate.Sdk.Tests/`; any fix to src/ discovered during
test authoring is routed back to a single fixer (no parallel src edits).

GATE 3 (final): `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, AOT sample
publish (see §6).

---

## 4. Test plan

Harness: the existing patterns — `System.IO.Pipelines.Pipe` pairs + `ProtocolReader/Writer`
directly against `InvocationStateMachine` (as in `StateMachine/InvocationStateMachineTests.cs`),
and full-stack `InvocationHandler.HandleAsync` over framed `MemoryStream`/`Pipe` V4 messages (as
in `Integration/ProtocolIntegrationTests.cs` — reuse `WriteFramedMessage`/`ReadFramedMessage`;
extract them into a shared `Testing/ProtocolTestHarness.cs` helper).

WATCHDOG MECHANISM (load-bearing — several pre-fix failure modes are infinite hangs, e.g.,
§4.1.4/B2 and §4.7/B8, which must FAIL the run, not freeze it): the harness exposes
`Task<T> AwaitBounded<T>(Task<T> task)` = `task.WaitAsync(TimeSpan.FromSeconds(5))`, and EVERY
scenario wait goes through it; `[Fact(Timeout = 10_000)]` is the per-test backstop. NOTE:
xunit v2 (Directory.Packages.props pins 2.9.3) only honors `Timeout` for async test methods and
it aborts, not fails, sync blockage — therefore NO harness wait may be a sync block
(`Task.Wait`/`.Result` are banned in the suite); the `WaitAsync` watchdog is the primary
mechanism, the attribute is defense-in-depth.

WIRE-ORDER HELPER: `AssertFrameOrder(byte[] responseStream)` parses every frame header and
asserts (a) header validity, (b) NO frame of any kind follows a Suspension frame, (c) End is
final when present. Reused by every §4.7 scenario and the §4.4.3 stress test.

Every case below fails pre-fix and passes post-fix unless marked (parity).

### 4.1 `StateMachine/ReplayTests.cs` (B1, B2, B10b) — mirrors `tests/{sleep,run,state,input_output,failures}.rs`

1. Run replay success (run.rs analogue): Start{known_entries=3} + Input + RunCommand{id=1,
   name="x"} + RunCompletionNotification{id=1, value="42"} → `RunAsync<int>` returns 42, no
   RunCommand re-emitted. (Pre-fix: JsonException from protobuf bytes.)
2. Run replay terminal failure (B10b): notification carries failure{500} → `TerminalException`
   with same code/message (saga compensation determinism).
3. Run replay without buffered completion at the FRONTIER (decision 4 / 1.7 case 2): Start{2}
   + Input + RunCommand{id=1} (the LAST journaled command), no notification → closure EXECUTES
   exactly once (claimed via TryClaimForExecution), wire shows ProposeRunCompletion{id=1};
   deliver RunCompletionNotification{id=1} → value returned.
4. Sleep resume hang regression (sleep.rs known_entries=3): Input + SleepCommand{id=1} +
   SleepCompletionNotification{id=1}; handler sleeps then returns → completes < 2 s, no
   duplicate SleepCommand, Replaying→Processing after dequeue. (Pre-fix: infinite hang, B2.)
5. Skip-replay regression (0 notifications): Start{2} + Input + SetStateCommand; handler does
   identical SetState → no duplicate SetStateCommand on the wire.
6. Fresh invocation (input_output.rs): Start{1} + Input → State==Processing immediately.
7. known_entries=0 → ProtocolException (KNOWN_ENTRIES_IS_ZERO parity).
8. Mixed journal (failures.rs known_entries=5): input + 2 commands + 2 notifications →
   `IsReplaying` flips false exactly on the second dequeue.
9. Unavailable entry (defensive, journal-level): `InvocationJournal.DequeueReplay` on an empty
   queue → ProtocolException, no wire read (in the integrated SM this state is unreachable —
   `IsReplaying == false` flips State to Processing — so the guard is exercised as a unit test;
   see also §4.9 JournalTests).
10. Call replay + index divergence: journal [Input, CallCommand{invIdIdx=1, resultId=2},
    RunCommand{id=3}] + CallCompletionNotification{2} + RunCompletionNotification{3} → call
    value and run value each resolve against their OWN ids (pre-fix: positional collision).
11. Notification-after-command ordering (late-notification path — legal ONLY at the replay
    frontier, where the mutation guard permits parking): journal [Input, SleepCommand{id=1}]
    (Sleep is the LAST command), no buffered notification; pump delivers
    SleepCompletionNotification{1} AFTER StartAsync returns → the parked handler resolves.
12. Type/name mismatch: journal has RunCommand where handler executes Sleep → ProtocolException
    "type mismatch"; RunCommand name "a" vs handler name "b" → "command mismatch" (B5 replay
    validation).
13. Property test: random protobuf command payloads in the replay batch must never reach
    `JsonSerializer` (assert no JsonException for arbitrary commands — values only ever come
    from notifications/eager fields).
14. Uncompleted await MID-replay (UncompletedDoProgressDuringReplay parity): journal [Input,
    SleepCommand{id=1}, SetStateCommand], NO notification → handler awaiting the sleep gets
    ProtocolException ("journal mutation"), never a hang or a suspend loop.
15. Uncompleted Run MID-replay (1.7 case 3 — duplicate-side-effect guard): journal [Input,
    RunCommand{id=1}, SetStateCommand], NO notification → ProtocolException AND the closure's
    side-effect counter stays 0 (closure must NOT execute mid-replay).
16. Template A/B replay smoke matrix (lane 3e): AttachInvocation and GetInvocationOutput
    (happy path: value from notification; mismatch path: type mismatch → ProtocolException);
    ResolveAwakeable / RejectAwakeable / CancelInvocationAsync (happy dequeue + type-mismatch
    each). Covers the Template A/B consumers §4.1.1-13 miss (mod.rs:1192/1244).
17. Call replay TARGET validation (B5): journaled CallCommand{service="A", handler="h",
    invIdIdx=1, resultId=2} while code calls service "B" with the same id shape →
    ProtocolException ("target mismatch"), never silently cross-wired values. Same for
    OneWayCall.
18. Notifications-only batch: Start{known_entries=2} + Input + one CompletionNotification
    (ZERO non-input commands) → StartAsync lands in Processing; the notification is parked as
    an early-completion slot and the first matching live op consumes it without a wire wait.
19. Commands after Output (2.7.5): journal [Input, OutputCommand, SetStateCommand]; handler
    returns immediately → CompleteAsync dequeues Output, sees IsReplaying still true →
    ProtocolException ("commands after Output"), no End frame.
20. (parity — B10a documented non-bug) handler calls ctx.Now() twice; capture run-1's stream,
    replay it → both `__restate_now` Runs replay cleanly with deterministic ids; values equal
    run-1's.

### 4.2 `StateMachine/SingleReaderTests.cs` (B3) — mirrors `tests/state.rs:47-70`

1. Port of state.rs entry_already_completed over `InvocationHandler.HandleAsync`:
   Start{known_entries=3, partial_state=true} + Input + GetLazyStateCommand{id=1} +
   GetLazyStateCompletionNotification{id=1}; handler GetState then returns → no
   InvalidOperationException("Concurrent reads..."), bounded completion, Output+End present.
2. Same shape for Sleep and Call completions inside the batch.
3. Pure-command batch (k=0 notifications) → unchanged Processing transition.
4. Single-reader invariant: wrap the inbound `PipeReader` in a counting decorator
   (`Interlocked` pending-read counter, asserts ≤ 1 at all times) — run scenarios 1-3 plus
   §4.1 cases through it.
5. Fuzz: random interleavings of commands/notifications within known_entries (seeded,
   100 shapes) → invariant holds, every run terminates.
6. Pump-death unwind (2.6(b)): deliver a Command-typed frame AFTER preflight while the handler
   is parked on a Sleep → the pump's ProtocolException FailAlls both managers, the handler
   unwinds, an Error frame is written, HandleAsync returns bounded (no deadlock, nothing
   escapes into the host). Variant: fault the request `PipeReader` mid-stream (IO error) →
   same bounded unwind.

### 4.3 `StateMachine/SignalIndexTests.cs` (B4)

1. Two awakeables → decode ids ("sign_1" + Base64Url): trailing BE32 = 17 then 18 (never 0-16).
2. `BuiltInCancelSignal_IsIgnored_PendingCancellationSupport`: SignalNotification Idx=1
   (CANCEL shape, Void) after creating two awakeables → neither TCS completes. NOTE: this
   encodes the §5 DOCUMENTED DIVERGENCE (Rust buffers CANCEL into async_results for
   consumption; this fork logs-and-ignores it pending cancellation support) — the name and an
   in-test comment must say so, so future cancellation work flips the expectation deliberately
   instead of fighting a green test.
3. SignalNotification Idx=17 with payload → first awakeable resolves with payload.

### 4.4 `StateMachine/RunFanOutTests.cs` (B5) — mirrors `tests/run.rs` fan-out

ISOLATION: `RunFanOutTests` and `CompletionManagerRaceTests` (§4.8) live in a dedicated
`[CollectionDefinition(DisableParallelization = true)]` collection — `ThreadPool.SetMinThreads`
is process-global and the iteration loops would otherwise skew parallel collections (conflicts
with §6.6's zero-flakes gate). Capture the original min-thread counts and restore them in the
class fixture's `Dispose`.

1. Ordering: `RunAsync("A")` and `RunAsync("B")` gated by TCSes; complete B then A → wire
   shows RunCommand("A") before RunCommand("B") with distinct sequential ids; proposals
   reference matching ids irrespective of completion order.
2. Replay cross-wiring: journal Runs in reversed name order vs code → ProtocolException
   (command mismatch), never silently swapped values.
3. Concurrency stress: 64 RunAsync futures released by one barrier, min threads raised; parse
   the full output stream → every frame header-valid, journal Count == expected, AND the
   completion ids of the emitted commands appear in MONOTONICALLY INCREASING order in the
   stream (the structural proof that id-allocation order == journal order, 1.6). Repeat 100
   iterations.
4. Journal order under flush backpressure (the SemaphoreSlim-design killer made impossible by
   1.6 — keep as regression proof): wrap the outbound pipe in a writer whose FlushAsync awaits
   a manually-released gate (slow flush); interleave RunFutureAsync fan-out with sync
   SetState/ClearState from the handler; capture the journal; REPLAY the captured stream →
   zero type/name/id mismatches (journal order provably equals call order even under
   contention).
5. Output integrity under straggler proposals: handler creates an un-awaited RunFuture whose
   closure completes (and serializes its proposal) concurrently with the handler returning its
   own large result → parse the OutputCommand payload byte-for-byte; it must equal the
   expected serialization (regression for the torn `_serializeBuffer` race, 2.7.5/2.8).
6. Late-notification race at the frontier (TryClaimForExecution, 1.7 case 2): replay a
   frontier RunCommand while the pump concurrently delivers its RunCompletionNotification;
   loop 1k iterations → the closure executes at most once, and at most one
   ProposeRunCompletion appears per iteration (never both a proposal and a consumed
   notification for an executed closure that raced a delivery).

### 4.5 `StateMachine/SendHandleTests.cs` (B6)

1. Fire-and-forget: `SendAsync` returns after flush with NO notification delivered; wire has
   OneWayCallCommand with correct invocation_id_notification_idx (=1 for first op).
2. Lazy resolution: deliver CallInvocationIdCompletionNotification{id=1, invocation_id="inv_123"}
   → `await handle.GetInvocationIdAsync()` == "inv_123".
3. Stall regression: withholding the notification must not block `SendAsync` (bounded time).
4. Replay: journal [Input, OneWayCallCommand{idx=1}] + CallInvocationIdCompletionNotification{1,
   "inv_123"} → replayed handle resolves "inv_123" (pre-fix: UTF-8 garbage/empty).
5. Eager ctor: `new InvocationHandle("id")` → `GetInvocationIdAsync()` completes synchronously
   with "id". (Replaces OptionsTests.InvocationHandle_StoresId, 2.9.)
6. Thunk ctor: the resolve thunk is NOT invoked at construction (assert via counter); first
   `GetInvocationIdAsync()` invokes it exactly once; repeated awaits return the same value.
7. Suspension via handle: ctx.Send, EOF, then `await handle.GetInvocationIdAsync()` with no
   notification → invocation suspends with waiting_completions == [send's idx]; the in-flight
   await unwinds with SuspendedException (1.8 semantics).
8. Post-invocation await: handle stored beyond HandleAsync; awaiting after Dispose/CancelAll →
   TaskCanceledException (documented behavior, 1.8). Failure notification → TerminalException.

### 4.6 `StateMachine/EagerStateTests.cs` (B7) — mirrors `tests/state.rs`

1. Partial-state regression: Start{partial_state=true} + Input; SetState("a") then
   GetState("b") → GetLazyStateCommand("b") emitted and awaited (pre-fix: silent default).
2. Complete-state: state_map={x:v}; Get("x")→v with GetEagerStateCommand{Value}; Get("y")→
   default with GetEagerStateCommand{Void}.
3. Cleared marker: ClearState("x") then Get("x") → default, GetEagerStateCommand{Void}, no
   lazy command (complete AND partial variants).
4. clear_all: partial start, ClearAllState(), Get("z") → default, no lazy command
   (is_partial flipped false).
5. Replay determinism: capture run-1's full command stream, feed back as known_entries with
   partial_state=true → identical results, zero mismatch; SetState during replay repopulates
   the cache so post-replay Get("a") needs no roundtrip.
6. GetEagerStateCommand replay decode: embedded Void → default; embedded Value → value.
7. Counter determinism: eager Get consumes a completion id — follow it with a Sleep and assert
   the SleepCommand carries id = eagerGetId + 1 in both fresh and replayed runs.
8. GetEagerStateKeysCommand replay decode (keys variant of case 6): the keys embedded in the
   replayed command deserialize to the same string[] the live path produced.
9. Eager GetStateKeys order determinism: complete state map inserted in unsorted order →
   returned keys (and the journaled GetEagerStateKeysCommand) are SORTED (Rust sorts,
   context.rs:414-421) — identical fresh and replayed.
10. Empty-payload normalization (documented §5 divergence, 2.7.3): SetState(key,
    zero-length payload) then Get(key) → default on the FRESH run (eager path) AND on the
    REPLAYED run (embedded Value with empty content) — the two runs must agree; Void and
    empty-Value reach the same `DeserializeStateValue` outcome by the same rule on every path.

### 4.7 `StateMachine/SuspensionTests.cs` (B8) — mirrors `tests/suspensions.rs`

Every scenario runs `AssertFrameOrder` (§4 preamble) on the full response stream — no frame of
any kind may follow a Suspension frame. Both EOF orderings are first-class: cases 1-6 deliver
EOF AFTER the handler parks; cases 7-10 deliver EOF BEFORE (request-response/Lambda shape —
the ordering the original draft missed and the pre-revision design deadlocked on).

1. Happy path (EOF-after-park): handler ctx.Sleep; after SleepCommand flushed, complete the
   request-side PipeWriter (EOF), no notification → response contains Suspension frame whose
   waiting_completions == [sleep id]; HandleAsync returns < 5 s.
2. Leak regression: WeakReference to the per-request scope object; post-HandleAsync +
   GC.Collect → dead. (Pre-fix: test times out.)
3. Abort path: cancel the outer token while parked → HandleAsync returns promptly, pending
   TCSs faulted (OperationCanceledException), no Suspension frame.
4. Race: deliver the completion notification and EOF back-to-back → invocation completes
   normally with Output+End and no Suspension frame.
5. Run guard: EOF while a Run closure is mid-execution → no suspension until the proposal is
   flushed (`_executingRuns` defers); the Run-epilogue trigger then fires and suspension
   appears iff the handler is still awaiting the run notification. Variant: EOF lands in the
   Run prefix window (after RunCommand flush, before the closure starts) → the run still
   EXECUTES (Rust try_execute_run, async_results.rs:141-145), proposal is flushed, then
   suspension.
6. Awakeable suspension: parked awakeable → Suspension frame with waiting_signals == [17].
7. EOF-BEFORE-PARK, completion: EOF delivered immediately after the known-entries batch;
   handler then issues its FIRST ctx.Sleep → Suspension frame with waiting_completions ==
   [sleep id], bounded return. (Pre-revision design: permanent hang.) Repeat for lazy
   GetState and Call.
8. EOF-BEFORE-PARK, signal: same shape with an awakeable → waiting_signals == [17].
9. EOF-while-computing (NO premature suspension): handler creates a ctx.Timer future
   (registered, NOT awaited), EOF arrives while the handler is still computing → no
   Suspension frame yet (the awaiting set is empty — PendingIds-style over-reporting is the
   bug this guards); when the handler then awaits the future → suspension with exactly the
   timer's id.
10. EOF-before-park via DurableFuture and via the send handle: (a) ctx.Timer created before
    EOF, awaited after through `DurableFuture.GetResult` → suspends (the park-thunk path,
    2.10); (b) ctx.Send then `GetInvocationIdAsync` after EOF → suspends with the send's id
    (also §4.5.7).
11. NO spurious suspension from unawaited registrations: handler does ctx.Send (invocation-id
    never awaited), EOF, handler returns a value → Output+End, NO Suspension frame (Rust
    parity: sys_write_output is not an await point; fails under any PendingIds-based design).
12. Waiting set is the AWAITED set only (HitSuspensionPoint parity): handler creates three
    futures (Sleep, Call, Timer) but awaits only the Call; EOF → Suspension frame lists
    exactly the Call's result id — not the other registered ids.
13. Suspension-vs-failure wire exclusivity: force the race between a handler exception
    (FailAsync path) and pump-EOF suspension (e.g., closure throws non-terminal just as EOF
    lands) → whichever wins, the stream never contains frames after a Suspension frame and
    never both a Suspension and an Error frame.
14. Run terminal failure with input closed (B10b failure direction, 1.7): Run closure throws
    TerminalException; EOF already delivered; saga-compensation flag must NOT be set before
    the failure is durable — the attempt suspends (proposal flushed, then Suspension); on the
    replay attempt with the Failure notification buffered, the Run re-raises TerminalException
    and compensation runs exactly once.
15. Signal-only waiters + executing run: handler parks on an awakeable while a Run closure is
    mid-flight; EOF → `_executingRuns` defers; after the proposal flushes, the epilogue emits
    a Suspension with waiting_signals == [17] (and the run's completion id iff still awaited).

### 4.8 `Journal/CompletionManagerRaceTests.cs` (B9)

1. Lossy-window stress: 10k iterations; fresh manager; race `Task.Run(TryComplete(5, r))` vs
   `Task.Run(GetOrRegister(5))` → returned task always completes with r within 2 s.
   (Pre-fix: intermittent hang.)
2. Same for TryFail, and for GetOrRegister racing a pre-stored early completion.
3. Existing CompletionManagerTests semantics preserved (minus deleted Register).
4. FailAll/CancelAll/HasResultFor/TryClaimForExecution unit coverage, including the LATCH:
   GetOrRegister AFTER FailAll returns an already-faulted TCS (same exception); TryComplete
   after the latch returns false.
5. Duplicate-notification redelivery: TryComplete(5, r1) then TryComplete(5, r2) on a resolved
   slot → second returns false, the awaiter sees r1, no exception, no overwrite.
6. TryClaimForExecution races TryComplete for the same id (1k iterations) → exactly one wins;
   a claim never succeeds after a delivery and vice versa.
7. FailAll fault-observation: FailAll over slots nobody awaits → no UnobservedTaskException
   raised on GC/finalization (subscribe to TaskScheduler.UnobservedTaskException for the
   duration); an awaiter parked on a faulted slot still observes the exception.

### 4.9 Updated existing suites

- `InvocationStateMachineTests.cs`: Initialize(knownEntries=0) → ProtocolException; ops tests
  use knownEntries: 1; awakeable prefix test extended per §4.3.
- `JournalTests.cs`: rewritten — Enqueue/Dequeue type+name validation, IsReplaying lifecycle,
  Count/CommandIndex accounting, UnavailableEntry throw.
- `Integration/ProtocolIntegrationTests.cs`: existing cases unchanged (fresh-invocation
  parity); add one resumed-invocation end-to-end (Start{3} + Input + RunCommand + notification
  → Output equals the notification-derived value).
- `OptionsTests.cs`: `InvocationHandle_StoresId` replaced per 2.9 (coverage moves to §4.5
  cases 5-6); `DurableFutureTests.cs`: TCS-backed cases ported to the resolve-thunk shape,
  `VoidFuture_Completed_IsImmediate` deleted with B10d (2.10).

### 4.10 `StateMachine/PromiseTests.cs` — mirrors `third_party/sdk-shared-core/src/tests/promise.rs` (lane 3e)

Promise ops are 4 of the 26 rewritten replay sites and ResolvePromise/RejectPromise carry the
novel "Template B+id" allocation (a sync void API burning a completion id) — exactly the
B1-class accounting where an off-by-one silently bricks workflow replay:

1. GetPromise processing: GetPromiseCommand{key, id} emitted; notification value resolves it;
   Failure notification → TerminalException.
2. GetPromise replay: journal [Input, GetPromiseCommand{key="p", id=1}] + notification{1} →
   value from notification; key mismatch → ProtocolException.
3. PeekPromise processing + replay: empty/Void completion → default; value → deserialized.
4. ResolvePromise/RejectPromise processing: CompletePromiseCommand carries the allocated
   result_completion_id; the later ack lands as an early-completion slot (benign).
5. ResolvePromise/RejectPromise replay: dequeue + ValidateReplayCompletionId; mismatched id →
   ProtocolException.
6. Counter-burn determinism (the §4.6.7 analogue): ResolvePromise followed by ctx.Sleep →
   SleepCommand wire id == promiseCompletionId + 1 in BOTH fresh and replayed runs.

---

## 5. Resolved decisions & non-goals

FINAL decisions (from the repo owner; already baked into sections 1-4):

1. Scope = full CoreVM parity in one program of work. B1+B2+B3+B4 are one unified core
   redesign (completion-id accounting from 1, signal ids from 17, single-reader preflight
   buffering, ReplayCommand queue cursor); B5/B6/B7/B8/B9/B10b/B10d layer on top as specified.
2. Tests = full protocol-level suite mirroring shared-core's `src/tests/*.rs`, driven over
   in-memory pipes with synthetic V4 frames; every bug has a pre-fix-fails regression test;
   single-reader invariant + concurrency stress included (§4).
3. API shape follows Rust shared-core + Python SDK: `InvocationHandle` becomes lazy
   (`GetInvocationIdAsync()`), eager constructor retained for ingress/known-id sites. This is
   a PRE-RELEASE fork: no journal/wire compat shim for invocations recorded by the buggy build
   (in-flight journals from the old build will mismatch — release-note it).
4. Rust-way resolutions: `known_entries == 0` → ProtocolException (KNOWN_ENTRIES_IS_ZERO
   parity). Run during replay with no buffered completion → execute the closure inline in the
   replay branch and propose with the SAME deterministic id (thread/timing differs from Rust's
   DoProgress queue; wire frames and semantics identical) — NARROWED per adversarial review to
   the replay FRONTIER only (1.7 cases 2/3): inline execution applies exactly when the Run is
   the last journaled command (the resume case this decision was about); an uncompleted Run
   with LATER journaled commands is a journal-mutation error, the Rust-way outcome
   (UncompletedDoProgressDuringReplay). `GetEagerStateCommand` journaling
   layout adopted (eager hits become journaled commands). Suspension lands in THIS program of
   work.

Deliberate NON-changes:

- B10a — `ctx.Now()` reusing the fixed run name `"__restate_now"` (DefaultContext.cs:29-32):
  NOT a bug. protocol.proto imposes no name-uniqueness on RunCommandMessage.name (line 507);
  the Rust VM keeps names only for DEBUG logging and matches replay positionally. A fixed name
  is deterministic across attempts — uniquifying it (e.g., with a counter or random suffix)
  would be harmless but pointless, and a NON-deterministic suffix would break command-equality
  checks. Leave as-is; §4.1 case 12's inverse (two `__restate_now` runs replay cleanly) is
  covered by the deterministic-name property of Template D.
- B10c — retry-elapsed measured by process-local `DateTimeOffset.UtcNow`
  (Operations.cs:110/125/155/171), ignoring StartMessage fields 7/8: NOT a bug. The proto
  itself declares `retry_count_since_last_stored_entry` / `duration_since_last_stored_entry`
  best-effort ("might not be accurate, as it's not durably stored"); shared-core's
  `infer_entry_retry_info` is an optional refinement. Live behavior is correct; no change.
  (Optional XML-doc note on RetryPolicy that budgets are per-attempt-process, not durable.)
- Cancellation-signal handling (acting on CANCEL Idx=1) and named-signal (`SignalName`)
  consumption remain unimplemented — pre-existing gaps explicitly out of scope; the redesign
  routes them safely (logged + ignored) instead of corrupting awakeable state. Two documented
  consequences: `SuspensionMessage.waiting_named_signals` is always empty (2.3(d)), and
  §4.3.2's CANCEL-ignored expectation is named/commented as a divergence so cancellation work
  flips it deliberately (Rust buffers CANCEL into async_results, input.rs NewNotificationMessage).

Documented divergences from Rust (intentional, revisit-able):

- Replay payload-byte equality: Rust's `check_entry_header_match` compares whole commands
  including payloads (SetState value bytes, Call parameters) unless
  `non_deterministic_checks_ignore_payload_equality`. This program of work validates type +
  name + Call/OneWayCall target triple + completion ids (1.2/2.1) and DEFERS payload-byte
  equality — strictly more validation than the fork has today, strictly less than Rust's
  default. Tracked as a §6 release-note item.
- Empty state payloads: an empty `Value` payload normalizes to `default` on EVERY path (fresh
  eager, replayed eager, lazy notification — one shared `DeserializeStateValue`, 2.7.3),
  whereas Rust hands empty bytes to the deserializer. Fresh and replayed runs cannot diverge
  (§4.6.10 proves it); serializers that round-trip values through zero-length payloads are not
  supported.

Adversarial-review disposition (three Fable critics; every BLOCKER/HIGH accepted and folded in
above — recorded here so the rationale survives):

- Suspension was redesigned from pump-EOF-triggered to await-driven (1.5) — the critics'
  shared BLOCKER; the original design deadlocked on EOF-before-park (request-response mode)
  and over-reported waiting ids from unawaited registrations.
- The async SemaphoreSlim write gate was replaced by the synchronous `_commandLock` +
  `_flushGate` pair (1.6) — SemaphoreSlim's non-FIFO grants and sync-Wait barging broke the
  journal-order==call-order guarantee B5 exists to establish.
- The proposed ARM64 fence/Interlocked fixes for the `MarkInputClosed`-vs-`_executingRuns`
  store-load race became MOOT: all suspension state lives under `_commandLock` (no lock-free
  reads remain), which subsumes the fence-presence test the review suggested — §4.7.13/15
  exercise the trigger races behaviorally instead.
- Decision 4 ("Run during replay with no buffered completion executes inline") was NARROWED to
  the replay frontier only (1.7) — mid-replay inline execution would re-execute side effects
  on journal mutation, the exact class Run prevents; Rust errors there
  (UncompletedDoProgressDuringReplay) and so do we.

---

## 6. Verification checklist (final gate)

1. `dotnet build` — solution-wide, zero new warnings in changed files.
2. `dotnet test` — full suite green, including all Phase 3 additions; async tests bounded by
   watchdog timeouts (no test may rely on infinite waits).
3. `dotnet format` (then `--verify-no-changes` in CI mode) — clean.
4. AOT: the SDK is AOT-aware. This blueprint introduces NO new reflection: `ParseReplayCommand`
   uses protobuf generated parsers; `GetEagerStateKeysCommand` re-encode uses
   `JsonSerializer.SerializeToUtf8Bytes(string[])`, identical to the existing BUG-4 StateKeys
   path already covered by the codec's `UnconditionalSuppressMessage` (string[] is AOT-safe).
   `InvocationHandle` is a plain class. VERIFY: publish an AOT sample
   (`samples/` AOT-enabled project, `dotnet publish -c Release /p:PublishAot=true`) — zero new
   IL2026/IL3050 warnings versus the pre-change baseline.
5. Single-reader invariant test (§4.2.4) green across the full replay scenario matrix.
6. Concurrency stress (§4.4.3-6, §4.8.1/6) — stress iterations as specified, zero flakes over
   3 consecutive runs (the stress collection runs with parallelization disabled, §4.4).
7. Behavioral spot-checks against shared-core test expectations: frame-by-frame comparison for
   §4.1.4 (sleep resume), §4.7.1 (EOF-after-park suspension), AND §4.7.7 (EOF-BEFORE-park
   suspension — the request-response shape) versus
   `third_party/sdk-shared-core/src/tests/{sleep,suspensions}.rs` wire shapes.
8. Wire-order invariant: `AssertFrameOrder` (§4 preamble) green across every §4.7 scenario —
   no frame of any kind after a Suspension frame; never both Suspension and Error in one
   stream.
9. Hang-proofing audit: zero sync blocks (`.Wait()`/`.Result`) in the test suite; every
   scenario wait routed through `AwaitBounded` (§4 preamble) — verified by grep in review.
10. Release note: journals recorded by the pre-fix build are NOT replayable by this build
    (completion-id scheme, eager-state journaling, signal base, suspension frames all
    changed); replay validation covers type/name/target/ids but NOT payload bytes (§5
    documented divergence).
