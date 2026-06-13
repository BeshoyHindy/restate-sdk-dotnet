# 08 — Implicit child cancellation (cancel tracked Call children on inbound CANCEL)

Ground truth: `third_party/sdk-shared-core/src/vm/mod.rs`
(`tracked_invocation_ids` field at :95; push in `sys_call` :766-777 and `sys_send` :843-854; the
cancel loop inside `do_progress` :445-476; `sys_cancel_invocation` :1159-1173), and
`src/lib.rs:252-262` (`VMOptions::default`).

## What Rust does

When a parent invocation issues child Calls/Sends, the VM records each child's invocation-id
notification handle in `tracked_invocation_ids`. The push is gated by the `VMOptions` flags
`cancel_children_calls` (default **true**) and `cancel_children_one_way_calls` (default **false**).
On an inbound CANCEL that completes inside `do_progress`, the VM (1) resolves any unresolved tracked
invocation-ids — suspending `do_progress` if an id has not arrived yet — and (2) emits one
`SendSignalCommand{idx=1 (CANCEL), target_invocation_id}` per tracked child via
`sys_cancel_invocation`, BEFORE running `sys_end`.

## What this SDK does (and the precise divergences)

Implemented in `InvocationStateMachine` (`.cs` + `.Operations.cs`):

- **Registry.** `_trackedChildren : List<uint>` holds each child's invocation-id completion id,
  appended STRICTLY in journal order under `_commandLock` in `CallPrefixAsync` (the same section that
  allocates the id and journals/dequeues the `CallCommand`). One-way `SendPrefix` does NOT append.
  Because the append is inside the lock in BOTH the replay and processing branches, the registry
  rebuilds identically across attempts (same ids, same order) — the determinism the child-cancel
  emission relies on.

- **Snapshot at CANCEL time.** `TriggerCancellation` (pump thread) captures the RESOLVED child
  invocation-id strings into `_cancelledChildInvocationIds`, in registry order, INSIDE `_commandLock`
  and BEFORE `_completions.FailAll` clears the table (the resolved id strings live in `_completions`
  under each child's invocation-id completion id and `FailAll` wipes them). The non-consuming
  `CompletionManager.TryGetResult` reads them without registering a waiter. An unresolved or failed
  child yields `false` and is omitted.

- **Emission by the single terminal writer.** `FailTerminalAsync` (handler thread, the existing
  `catch (TerminalException)` cancel path) calls `EmitChildCancelsLocked` at the TOP of its single
  `_commandLock` block, gated on `_cancelled`, BEFORE `WriteCommand(OutputCommand{409})` +
  `WriteHeaderOnly(End)`. It drains the snapshot and writes one cancel `SendSignalCommand{idx=1}` per
  resolved child (reusing the exact machinery `CancelInvocationAsync` uses), recording a
  `JournalEntryType.SendSignal` per emission. Wire/journal order is therefore
  `[Call commands][child-cancel SendSignals][OutputCommand{409}][End]`, matching Rust where
  `sys_cancel_invocation` runs before `sys_end`. The whole block is one atomic locked section, so no
  fan-out straggler can interleave a frame between the child-cancels and the Output.

### Divergences (intentional, documented in code at `EmitChildCancelsLocked`)

1. **Resolved-only, never suspend.** Only children whose invocation-id had ALREADY resolved at CANCEL
   time are cancelled. Rust suspends `do_progress` to fetch an unresolved id (its
   `call_then_cancel_without_invocation_id` test); here the cancel fires on the unwinding terminal
   path where suspending is impossible, so an unresolved child is deterministically SKIPPED. This is
   replay-stable: a child's invocation-id, once delivered, is a journaled `CallInvocationIdCompletion`
   replayed in the StartMessage known-entries on every later attempt, so the SAME set of children is
   cancelled and the SAME SendSignals are produced. A child unresolved on attempt 1 journaled no
   SendSignal and is still skipped on replay — consistent either way.

2. **Call children only.** One-way Sends are not tracked, baking in
   `cancel_children_one_way_calls = false` (lib.rs:257). The gate is expressed by code structure
   (`CallPrefixAsync` always appends, `SendPrefix` never does) rather than a runtime `if (false)`
   branch, which would be both an uncoverable runtime path and a CS0162/CA1805 build error — the
   precise, intentional divergence from Rust's runtime-configurable gate.

3. **Inbound-CANCEL only.** Child-cancel fires solely on the inbound-CANCEL terminal path
   (`_cancelled`), never on caller-ct teardown or a generic 500 failure.

### Replay safety

The child-cancel SendSignals are written by the SAME single writer (`FailTerminalAsync`, handler
thread), under the SAME lock, in the SAME journal position every attempt. To reach that writer on the
cancel path the handler must have PARKED (e.g. on Sleep) and had the await faulted by CANCEL — parking
is only legal once replay has drained (`AwaitNotificationAsync`'s replay-mutation guard), so
`State == Processing` whenever `EmitChildCancelsLocked` runs. The child-cancels are therefore always
written FRESH; a 409 terminal Output ends the invocation permanently, so they never re-enter a later
replay batch. They remain deterministic across the attempt that produces them because the registry
rebuilds identically from the replayed `CallCommand`s and the resolved-child snapshot reflects the
SAME set (each child's id is a replayed known-entry).

## Tests

`test/Restate.Sdk.Tests/StateMachine/ChildCancelTests.cs` (full-stack over
`InvocationHandler.HandleAsync`):

- (a) two resolved children → two cancel SendSignals in registry order, each before the 409 Output;
- (b) one unresolved child → it is skipped (only the resolved child is cancelled);
- (c) replay determinism — replayed Call + child-id + Sleep, then live CANCEL → identical single
  cancel SendSignal;
- (d) no children → no extra commands (the pre-existing cancel shape is unchanged);
- (e) a one-way Send child is NOT tracked even when its invocation-id resolved (pins divergence #2).

## Real-server E2E (E9)

`test/Restate.Sdk.E2E/NewFeaturesE2eTests.E9_ParentCancelled_ChildrenAutoCancelled` drives the
child-cancel through a REAL `restate-server` container: a parent (`ChildCancelLabService.SpawnAndPark`)
issues two request/response child Calls to `CancellableChildService.Block` (each blocks ~10 min) and
awaits both via `ctx.All`. The test cancels the parent through the admin API
(`PATCH /invocations/{id}/cancel`) and asserts — via the in-process `ChildCancelProbe` — that BOTH
children reached `cancelled:{i}` and NEITHER reached `completed:{i}`. A dropped child-cancel leaves the
children sleeping and the test times out (a fast, unambiguous failure).

### Why the cancel must land while the parent is LIVE-parked (not suspended)

This is the load-bearing timing constraint, discovered while bringing the E2E green. The SDK cancels
only children whose invocation-id is ALREADY resolved at CANCEL time (scope-limitation #1: it cannot
suspend to fetch an unresolved id on the unwinding terminal path). Two real-server facts interact:

1. **A suspended invocation is not woken by the SDK's child-cancel path.** When the parent SUSPENDS,
   its `SuspensionMessage` advertises only the ids it explicitly awaits — it does NOT list the CANCEL
   signal (idx=1). Shared-core's `do_progress` inserts `CANCEL_NOTIFICATION_HANDLE` into the await set
   whenever implicit cancellation is enabled (mod.rs:436-438), so a suspended Rust parent IS woken on
   cancel; the managed SDK does not replicate that, so an admin cancel of a *suspended* parent tears it
   down WITHOUT re-invoking the handler — `EmitChildCancelsLocked` never runs.
2. **Even when re-invoked, CANCEL delivered inside the resume batch is processed DURING replay**, before
   the handler body has re-executed the Calls that rebuild `_trackedChildren`. `TriggerCancellation`
   then snapshots an EMPTY tracked set (observed: `tracked=0, resolved=0, state=Replaying`), so no child
   is cancelled.

Both are avoided by cancelling while the parent is still on its FIRST attempt, actively Processing and
parked on `ctx.All` (within the server's 5s inactivity window). At that instant both children are
tracked AND their invocation-ids are resolved (a request/response Call resolves the child id eagerly),
so the inbound CANCEL is processed against fully-tracked live state and the fan-out emits one cancel
per child — exactly the in-process unit scenario `TwoResolvedChildren_InboundCancel_EmitsCancelSignalPerChild`,
now proven end to end. Waking a *suspended* parent on cancel, and resolving tracked ids on demand during
a cancel-in-replay, remain the documented residual gaps versus Rust (scope-limitation #1 generalized).
