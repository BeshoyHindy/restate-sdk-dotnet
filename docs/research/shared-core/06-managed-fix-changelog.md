# 06 — Managed Fix Changelog: CoreVM-Parity Replay / Completion-Id / Suspension Redesign

Status: changelog for the managed SDK redesign specified in
[`05-managed-fix-blueprint.md`](./05-managed-fix-blueprint.md) and grounded in the confirmed
findings of [`04-managed-bug-verification.json`](./04-managed-bug-verification.json). It describes
WHAT changed and WHY for a reader who knows the old (pre-fix) `feat/sdk-shared-core` build — the
new execution model, the journal-incompatibility release note (blueprint §6 item 10), and the
deliberate divergences from the Rust CoreVM ground truth (blueprint §5).

Ground truth referenced throughout is the vendored Rust CoreVM under
`third_party/sdk-shared-core/src/vm/` (`context.rs`, `mod.rs`,
`transitions/{input,journal,async_results,terminal}.rs`) and the wire contract in
`third_party/sdk-shared-core/service-protocol/dev/restate/service/protocol.proto`.

---

## 1. Why the old model was wrong (one sentence per confirmed bug)

The pre-fix SDK accounted for the journal in a way that has no analogue in the Rust VM: it derived
wire completion ids from `_journal.Count`, stored raw replayed COMMAND protobuf bytes as user
results, compared a command-only journal count against `known_entries` (which the protocol counts
as commands AND notifications), and read the protocol pipe from two concurrent tasks. The ten
verified findings all fall out of that mismatch:

| Bug | Severity | One-line root cause (verified) |
| --- | --- | --- |
| B1 | CRITICAL | Replayed results were deserialized from the raw COMMAND protobuf, not from the completion NOTIFICATION value that actually carries the result/failure. |
| B2 | CRITICAL | `known_entries` (commands + notifications) was compared against a command-only `Count`, so resumed invocations either hung in `Replaying` or silently skipped replay. |
| B3 | CRITICAL | `ReplayNextEntryAsync` read the `PipeReader` concurrently with the incoming-message pump whenever the replay batch contained a notification. |
| B4 | HIGH | The awakeable/signal index started at 0, colliding with the reserved built-in signal range (CANCEL = 1; 1–16 reserved). |
| B5 | HIGH | Parallel `ctx.RunAsync` fan-out raced on unsynchronized shared state and journaled Run commands in completion order, cross-wiring results on replay. |
| B6 | MEDIUM | `ctx.Send` blocked awaiting the `CallInvocationIdCompletionNotification` instead of being fire-and-forget, and decoded the command protobuf as the invocation id on replay. |
| B7 | HIGH | The eager-state cache conflated "have eager state" with "eager state is complete"; partial-state Gets returned `default` after a `SetState`, and replay never repopulated the cache. |
| B8 | HIGH | Suspension was never implemented: input-close parked the handler forever, leaking the handler task, DI scope, state machine, and pooled buffers. |
| B9 | LOW | `CompletionManager` register/complete TOCTOU races could silently drop a completion (the lossy window being in `TryComplete`/`TryFail`). |
| B10 | LOW | Assorted: result-from-notification failure direction (B10b) and the cached void future (B10d) — the rest (B10a/B10c) confirmed NON-bugs, see §5. |

B1+B2+B3+B4 are not four independent patches; they are one unified core redesign. The remaining
fixes (B5/B6/B7/B8/B9/B10b/B10d) layer on top of that core.

---

## 2. The new execution model

The redesign replaces the ad-hoc journal accounting with the CoreVM structure. Each subsection
below names the new mechanism, the bug(s) it discharges, and the Rust construct it mirrors.

### 2.1 Deterministic completion-id and signal-id counters (B1, B4)

Completion ids and signal ids are now allocated from dedicated counters on the state machine —
NOT derived from `_journal.Count` — mirroring Rust `Journal::default()` in `vm/context.rs`:

- `_nextCompletionId` starts at **1** ("clever trick for protobuf": 0 means field-unset, so a
  completable command carrying id 0 is a corrupt/foreign journal).
- `_nextSignalId` starts at **17** (ids 1–16 are reserved; built-in `CANCEL` = 1). This single
  base change is the whole of B4: the old `_nextSignalIndex` field started at 0 and collided with
  the reserved range.

The heart of B1 is the INVARIANT that every completable operation allocates its completion id(s)
from `NextCompletionId()` in BOTH the Processing and the Replaying branch, **in the same order**,
so the counters advance identically across attempts and ids are deterministic. `Call` allocates
two ids (invocation-id notification, then result) in that fixed order. Eager-state Gets allocate
an id even though no wire notification ever arrives, because Rust does so before its eager/lazy
branch and in the replay branch too. The increments are plain (non-`Interlocked`) by design: both
counters are only ever touched inside the command lock (§2.6), the same section that
journals/dequeues the command, which is precisely what makes id order equal journal order under
fan-out. The old dummy-journal-slot hack (the `"BUG 1 FIX"` shims) is deleted.

### 2.2 Buffered replay queue + replay cursor (B1, B2, B3)

Replay is now driven by a `Queue<ReplayCommand>` filled once during `StartAsync` preflight — the
C# analogue of Rust's `State::Replaying { commands: VecDeque<RawMessage> }`, pre-decoded so replay
never re-parses or re-reads the wire:

- `ReplayCommand` is a pre-decoded struct (type, name, target triple, completion id, eager result)
  produced by the new `ParseReplayCommand` codec entry point. It replaces the deleted
  `JournalEntry` struct whose raw-command-bytes `Result` was the direct cause of B1.
- `IsReplaying => _replayCommands.Count > 0` — the analogue of Rust `!commands.is_empty()`. This
  REPLACES the old `Count < KnownEntries` test and discharges **B2**: the old test compared a
  command-only count against a commands-plus-notifications total, so a journal with any replayed
  notification either hung or skipped replay.
- `DequeueReplay(expectedType, expectedName[, target triple])` pops one command per sys-call and
  validates type + name (+ Call/OneWayCall target triple) strictly — the analogues of Rust's
  `UnavailableEntryError`, `CommandTypeMismatchError`, and `check_entry_header_match`. An empty
  queue throws a `ProtocolException` ("unavailable entry") and NEVER reads the wire.

Because the replay queue is filled entirely during `StartAsync` and consumed synchronously
thereafter, `ProcessIncomingMessagesAsync` becomes the **only** reader of `ProtocolReader` after
start. The old `ReplayNextEntryAsync`/`AdvanceReplayIndex` second reader is deleted — discharging
**B3** (the concurrent-`PipeReader` race) and the positional-mismatch half of B5.

`known_entries == 0` now throws a `ProtocolException` (Rust `KNOWN_ENTRIES_IS_ZERO` parity);
entry 0 is the Input entry consumed by `StartAsync`, so the provisional replay/processing decision
uses `known_entries > 1`.

### 2.3 Results come from notifications, not commands (B1, B10b)

User-visible results are now read from the completion NOTIFICATION value, never from the replayed
command bytes. On success the locally computed value is returned directly (no re-deserialization of
the notification payload); the notification await still serves as the ack barrier.

**Failure direction (B10b):** a terminal Run failure surfaces to user code ONLY via the
`RunCompletionNotification`, exactly like success — never by rethrowing the closure's
`TerminalException` directly. Rethrowing directly would let saga compensations run BEFORE the
runtime durably stored the failure proposal. So a failing Run proposes the failure and returns;
every Run path then awaits the notification and raises the `TerminalException` after durability
(bidi mode) or unwinds with `SuspendedException` under closed input — identical fresh-vs-replay
behavior, matching Rust in both directions.

### 2.4 Run command ordering and Run-during-replay (B5, B10b)

All Run variants journal `RunCommandMessage{ name, result_completion_id }` in the SYNCHRONOUS
locked prefix BEFORE executing the user action (Rust `SysRun` journals at creation), so **journal
order equals creation order, never completion order**. The result is attached afterwards via
`ProposeRunCompletionMessage` keyed by the CAPTURED completion id — never a fresh `_journal.Count`
read. This, together with the command lock (§2.6), discharges the fan-out half of **B5**: parallel
`ctx.RunAsync` calls can no longer journal out of order or cross-wire results on replay.

Run during replay follows the three cases of Rust's `DoProgress` Replaying arm:

1. **Completion already buffered** for the Run's id → consume it WITHOUT executing the closure
   (Rust `non_deterministic_find_id`).
2. **No completion AND the dequeue ended replay** (the Run is the last journaled command — the
   replay frontier) → execute the closure inline, propose with the SAME deterministic id, await the
   notification. This is the only resume case; the execute-vs-await decision is made atomically via
   `CompletionManager.TryClaimForExecution` so a late notification cannot cause a duplicate
   execution or proposal.
3. **No completion AND replay continues** (journaled commands exist after this uncompleted Run) →
   `ProtocolException` ("uncompleted Run during replay — journal mutation"), Rust
   `UncompletedDoProgressDuringReplay` parity. The SDK fails fast here; it does NOT re-execute a
   user side effect mid-replay.

### 2.5 Eager state as `{ is_partial, values: map<string, bytes?> }` (B7)

The single conflated `_initialState` dictionary is replaced by a tri-state cache mirroring Rust
`EagerState { is_partial: bool, values: HashMap<String, Option<Bytes>> }`:

- A value of `null` is a known-cleared marker (Rust `None`).
- An absent key with `is_partial = true` is **Unknown** → fall back to a lazy `GetLazyStateCommand`.
- An absent key with `is_partial = false` is **Empty** (definitively no value).

This discharges **B7**: the old cache could not distinguish "value cleared / absent" from "value
unknown because state is partial", so a partial-state Get after a `SetState` silently returned
`default`. `SetState`/`ClearState`/`ClearAllState` now mutate the cache UNCONDITIONALLY, including
during replay (Rust `mod.rs`), and `ClearAllState` flips `is_partial` to false. Eager hits are
journaled as `GetEagerStateCommand` with the observed result inline (Rust journal layout), and the
`StartMessage` partial-state flag is surfaced instead of being discarded.

### 2.6 Single mutual-exclusion domain: command lock + flush gate (B5)

A single `_commandLock` is the .NET analogue of Rust's exclusive `&mut self`. It guards ALL VM
state: the id counters, the journal (replay queue + counters + `RecordCommand`), the serialize
buffer and command writes, the `State`/`_suspended` transitions, the suspension condition inputs
(`_inputClosed`/`_executingRuns`/`_awaiting`), and the eager-state cache. It is NEVER held across an
await; all sections are short synchronous buffer work.

The critical correction baked into this design (from adversarial review): an async `SemaphoreSlim`
write gate does NOT guarantee journal order equals call order — `WaitAsync` is not FIFO, sync
`Wait()` barges queued async waiters, and a continuation journals at gate-grant time rather than
call time. The fix is structural: the serialize+write+record prefix is **synchronous and never
yields**, so a future-creating method journals its command before it first yields. A handler issuing
ops sequentially therefore gets sequential journal entries even when the returned futures are stored
un-awaited. `FlushAsync` happens AFTER releasing `_commandLock`, serialized only by a separate
`_flushGate` (frame order is already fixed at write time, so flush-grant order is irrelevant). This
is the structural heart of B5.

### 2.7 Await-driven suspension (B8)

Suspension is now implemented, mirroring Rust `do_progress` rather than being triggered solely by
pump EOF. The critical model correction (adversarial review): a design that only evaluates
suspension when the pump reads EOF deadlocks whenever EOF lands BEFORE the handler parks — the
normal ordering in request-response / Lambda-style delivery. So the suspension decision lives at the
PARK SITE, not only at the EOF site:

- **One park API.** Every waiter parks through a single state-machine method,
  `AwaitNotificationAsync(id, kind)`. No code path awaits a `CompletionManager` TCS directly.
- **Awaiting set.** The park API registers the awaited id in `_awaiting` before parking and removes
  it on unpark. The `SuspensionMessage` waiting sets are built from the still-UNRESOLVED members of
  this set — never from "all registered TCSs". This is `awaiting_on` parity: a `ctx.Send` whose
  invocation-id nobody awaits, or an un-awaited call future, can never cause or appear in a
  suspension.
- **Three trigger sites**, each evaluating the full condition
  `{ input closed AND no Runs executing AND unresolved awaited ids exist }` atomically under
  `_commandLock`: the park site (EOF-before-park), the pump after reading null (EOF-after-park), and
  the Run epilogue after the executing-Run count decrements.
- **Effect.** Under the lock: write `SuspensionMessage { waiting_completions, waiting_signals }`,
  set `State = Closed`, set `_suspended`. After releasing the lock: flush, then `FailAll(new
  SuspendedException())` on both completion managers so every parked waiter unwinds. NO `End` frame
  follows a suspension. Because the suspension write, `State = Closed`, and every other terminal
  write all happen inside `_commandLock` with an in-lock state re-check, "Error/End after
  Suspension" is impossible by construction.
- **Replay-mutation guard.** Parking while still replaying with no buffered result is provably
  non-deterministic user code (added await point / code mutation); the park API throws a
  `ProtocolException` instead of degenerating into a silent suspend→resume→suspend loop.

`InvocationHandler.HandleAsync` catches `SuspendedException` and returns WITHOUT writing an error
frame. This discharges **B8**: input-close now suspends cleanly and unwinds the handler task, DI
scope, state machine, and pooled buffers instead of leaking them.

### 2.8 Lock-based completion manager (B9)

`CompletionManager` is keyed by WIRE completion/signal ids (never journal indices) and switches from
lock-free `ConcurrentDictionary` compositions to a plain `Dictionary` under one lock, mirroring the
Rust VM's exclusive access. This removes the four TOCTOU windows — chiefly the production-lossy one
where a `TryComplete`/`TryFail` whose failed `TryRemove` raced `GetOrRegister` silently dropped the
only notification — discharging **B9**. New members support suspension and Run replay:
`FailAll`/`CancelAll` LATCH the manager terminally (any later `GetOrRegister` returns a pre-faulted
TCS, so a straggler that parks post-suspension unwinds immediately), `HasResultFor`, and
`TryClaimForExecution`. There is intentionally no `PendingIds()`: the suspension waiting set comes
from the state machine's awaiting set, not the manager.

### 2.9 Lazy fire-and-forget send handle (B6)

`InvocationHandle` becomes a lazy handle wrapping `Lazy<Task<string>>`, mirroring Rust
`SendHandle.invocation_id().await` and the Python SDK. This discharges **B6**: `ctx.Send` returns
immediately after flush and is genuinely fire-and-forget; the invocation-id round trip happens only
if the caller awaits `GetInvocationIdAsync()`. The resolve is a THUNK, not an eagerly-started task —
an eager task would itself be a parked awaiter from creation, spuriously suspending a handler that
sends and returns, and faulting unobserved on suspension. With the thunk, awaiting the id is an
explicit suspension point routed through the park API. The old replay path that decoded the command
protobuf as the invocation id is gone; the sync `InvocationId` positional property is removed (this
is a pre-release fork — no compat obligation).

### 2.10 Cached void future removed (B10d)

The cached void `DurableFuture` (B10d) is removed; futures park via the resolve thunk through the
single park API rather than awaiting a bare TCS.

---

## 3. Release note — journal incompatibility (blueprint §6 item 10)

**Journals recorded by the pre-fix build are NOT replayable by this build.** This is a pre-release
fork; there is deliberately NO journal/wire-compatibility shim for invocations recorded by the buggy
build. Every dimension of the journal contract changed:

- **Completion-id scheme** — ids now start at 1 from a dedicated counter instead of being derived
  from `_journal.Count`, so a replayed command's `result_completion_id` will not line up with the
  old scheme.
- **Eager-state journaling** — eager-state hits are now journaled as `GetEagerStateCommand`
  entries that did not exist before.
- **Signal base** — signal ids now start at 17 (reserved range 1–16 honoured) instead of 0.
- **Suspension frames** — `SuspensionMessage` frames are now emitted on input-close where the old
  build parked forever.

In-flight invocations from the old build will mismatch on replay and must be drained or restarted
before deploying this build. Replay non-determinism validation covers entry **type**, **name**, the
Call/OneWayCall **target triple**, and **completion ids** — but NOT payload bytes (see §4).

---

## 4. Deliberate divergences from the Rust CoreVM (blueprint §5)

These are intentional and revisit-able. They are recorded here so the rationale survives session
boundaries and so future contributors know they are choices, not oversights.

### 4.1 Replay payload-byte equality is DEFERRED

Rust's `check_entry_header_match` compares whole commands including payload bytes (SetState value
bytes, Call parameters) unless `non_deterministic_checks_ignore_payload_equality` is set. This
program of work validates entry **type + name + Call/OneWayCall target triple + completion ids** and
DEFERS payload-byte equality. This is strictly MORE validation than the pre-fix fork performed (it
validated essentially nothing positionally) and strictly LESS than Rust's default. Two swapped calls
with identical id shapes still fail loudly on the target triple instead of cross-wiring values; what
is not yet caught is a same-shape command whose payload bytes differ. Tracked as a release-note item;
whoever lands full payload equality extends `ReplayCommand` and `DequeueReplay`.

### 4.2 Empty state payloads normalize to `default`

An empty `Value` payload normalizes to `default` on EVERY path — fresh eager, replayed eager, and
lazy notification — through one shared `DeserializeStateValue`, whereas Rust hands empty bytes to the
deserializer. Fresh and replayed runs therefore cannot diverge (this is exactly what keeps replay
deterministic), but serializers that round-trip meaningful values through zero-length payloads are
NOT supported. This is a conscious trade of one exotic serializer shape for guaranteed
fresh-vs-replay determinism.

### 4.3 Cancellation and named-signal consumption are OUT OF SCOPE

Acting on the built-in CANCEL signal (Idx = 1) and consuming named signals (`SignalName`) remain
unimplemented — pre-existing gaps explicitly out of scope for this program of work. The redesign
routes them SAFELY (logged and ignored) instead of corrupting awakeable state as the old build did.
Two documented consequences:

- `SuspensionMessage.waiting_named_signals` (proto field 3) is ALWAYS empty. Rust fills it from
  `NotificationId::SignalName`. Whoever implements named-signal waits must extend the suspension
  factory or the runtime will never resume a named-signal park.
- The CANCEL-ignored behavior is named and commented as a divergence in the test suite so that
  future cancellation work flips it deliberately (Rust buffers CANCEL into `async_results`).

### 4.4 Confirmed NON-bugs left unchanged (B10a, B10c)

For completeness, two items investigated as potential bugs are deliberate non-changes:

- **B10a** — `ctx.Now()` reusing the fixed run name `"__restate_now"` is NOT a bug. The protocol
  imposes no name-uniqueness on `RunCommandMessage.name`; the Rust VM keeps names only for debug
  logging and matches replay positionally. A fixed name is deterministic across attempts; a
  non-deterministic suffix would actually break command-equality checks.
- **B10c** — retry-elapsed measured by process-local `DateTimeOffset.UtcNow`, ignoring the
  best-effort StartMessage retry fields, is NOT a bug. The proto itself declares those fields
  best-effort and not durably stored; Rust's `infer_entry_retry_info` is an optional refinement.
  Live behavior is correct (an optional XML-doc note clarifies that retry budgets are
  per-attempt-process, not durable).

---

## 5. Cross-reference

- Code-level spec of record: [`05-managed-fix-blueprint.md`](./05-managed-fix-blueprint.md) — model
  (§1.1–§1.8), per-file edits (§2), sequencing (§3), test plan (§4), resolved decisions and non-goals
  (§5), verification checklist (§6).
- Verified evidence and shared-core citations:
  [`04-managed-bug-verification.json`](./04-managed-bug-verification.json) (`result.findings`).
- Feature surface and parity context: [`01-shared-core-feature-surface.md`](./01-shared-core-feature-surface.md),
  [`02-sdk-parity-matrix.md`](./02-sdk-parity-matrix.md),
  [`03-managed-gap-analysis.md`](./03-managed-gap-analysis.md).
- Rust ground truth: `third_party/sdk-shared-core/src/vm/` and the service-protocol proto under
  `third_party/sdk-shared-core/service-protocol/`.
