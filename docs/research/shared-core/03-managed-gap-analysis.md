# Managed .NET SDK — Gap Analysis & Port Plan

> Decision: **keep the pure-managed implementation** (no native dep, AOT-friendly — the original
> author's philosophy) and **port the features `sdk-shared-core` has** into the managed state
> machine. The git-submodule idea applies to the **service-protocol `.proto`**, not a native binary.

## Current managed architecture (as-is)

`InvocationStateMachine` is an **eager command-streaming** model, not a faithful VM port:
- Writes commands immediately; tracks pending results with per-journal-index
  `TaskCompletionSource<CompletionResult>` in `CompletionManager` (+ a separate `_signalCompletions`).
- Replay via `_journal.IsReplaying` + `ReplayNextEntryAsync`; eager state via `_initialState` map.
- Suspension is implicit (input stream ends → pending TCS never complete → handler parks).

This is functionally equivalent to the shared-core VM for the **single-await happy path**, but
diverges on: multi-future combinators, V7 protocol, runtime-driven run retry, request identity.

## What's already present ✓

State (get/set/clear/clearAll/keys, lazy+eager) · Run (sync/async, **SDK-local** retry) ·
Sleep · Call/Send (+ idempotency key on one overload) · Awakeable (signal-based) resolve/reject ·
Promise get/peek/resolve/reject · Attach / GetOutput (by invocation id) · CancelInvocation ·
Now() · DurableRandom · typed clients (source generator) · ASP.NET Core + Lambda hosting ·
Mock contexts · Protocol **V5–V6**.

## Gaps to close (prioritized)

### P0 — Protocol currency (V7) + protobuf submodule
- **Submodule** `restatedev/sdk-shared-core` (for `service-protocol/dev/restate/service/protocol.proto`)
  pinned to the rev matching the chosen shared-core version (0.10 → Restate ≥1.5/1.6). Point
  protobuf codegen at the vendored proto so wire types stay synchronized upstream.
- Bump `EndpointManifest` min/max → **5 / 7**; content-type negotiation `v7` with fallback to v6/v5
  (currently hardcoded `v6` in `RestateEndpointRouteBuilderExtensions.cs:112`).
- Add V7 fields/messages: `Future`/`AwaitingOnMessage`, `should_pause`, scope/limit_key
  (parse-only — not surfaced), terminal `Failure.metadata` (V6).
- **Risk:** must keep V5/V6 behavior intact (negotiate down when server is older).

### P1 — Protocol-correct future combinators (All / Any / Race / AllSettled) — ✅ DONE
- All four modes implemented: `All`=AllSucceededOrFirstFailed (short-circuits first failure),
  `Any`=FirstSucceededOrAllFailed (new), `Race`=FirstCompleted, `AllSettled`=AllCompleted (new,
  returns `DurableSettled<T>[]`). `MockContext` carries deterministic overrides; tests exercise the
  real base `Context` virtuals via `MockObjectContext`.
- The V7 `AwaitingOn` combinator wire-tree is **not needed** in this SDK: it uses Restate's
  bidirectional-streaming mode and never emits a `SuspensionMessage` — completions stream in and
  resolve per-index `TaskCompletionSource`s, so combinators are pure SDK-side `Task` orchestration.
  Replay-safe because journal order is fixed by future *creation*, not await order.

### P1 — Runtime-driven run retry + Pause
- Today `ctx.Run(retryPolicy)` retries **in-process** (`ExecuteWithRetryAsync` + `Task.Delay`),
  blocking the attempt and losing durability across process restarts.
- Align with shared-core: pass the `RetryPolicy` to the runtime via `ProposeRunCompletion`
  (`RunExitResult::RetryableFailure{attempt_duration,error}`) and let Restate schedule retries;
  support `OnMaxAttempts::Pause` (V7). Keep the SDK-local mode available as a fallback/option.
- **Behavioral decision required** (see Open Questions).

### P2 — Request identity verification (Ed25519 JWT)
- Entirely **absent** today. Implement `x-restate-signature-scheme`/`x-restate-jwt-v1`,
  `publickeyv1_<base58>` Ed25519 keys, EdDSA, aud = normalized path, v1/unsigned schemes.
- .NET has `System.Security.Cryptography` Ed25519 (net10) + a base58 dependency or hand-rolled.
  Wire into the endpoint request pipeline + `RestateOptions`.

### P2 — Implicit cancellation of children
- shared-core default auto-cancels tracked child calls on cancel signal. Managed has only manual
  `CancelInvocation`. Track child invocation ids per invocation; on abort, cancel children.

### P3 — Parity polish
- Terminal `Failure.metadata` (V6) on `FailTerminalAsync`.
- `PayloadOptions.unstable_serialization` marker for non-deterministic serde.
- Attach by workflow-id / idempotency-id targets (currently invocation-id only).
- `RunOptions` builder aligning retry knob names with other SDKs
  (initialInterval/maxInterval/factor/maxAttempts/maxDuration).
- Ingress/admin client surface (separate from in-handler `Context`).

## Open questions (need product decision)

1. **Target protocol version** — go to V7 (negotiate down to V5/V6), or stay V6? Restate 1.6
   supports both; V7 unlocks combinator tree + Pause.
2. **Run retry semantics** — switch to runtime-driven (durable, survives restarts) vs keep
   SDK-local (simpler, blocks attempt)? Recommend runtime-driven with SDK-local as opt-in.
3. **Combinator scope** — minimal fix (add `Any`/`AllSettled`, keep task-based) vs full
   VM-faithful do_await/take_notification rewrite. Recommend incremental: correct + complete
   the combinators now, defer a full VM rewrite.

## Suggested sequencing

1. P0 submodule + proto regen + V7 negotiation (foundation; unblocks everything).
2. P1 combinators (highest user-visible value; needs V7 messages).
3. P1 runtime-driven run retry.
4. P2 request identity (security).
5. P2 implicit cancellation.
6. P3 polish.

Each step: keep existing samples/tests green as the conformance harness; add E2E against a
Restate 1.6 server (docker-compose already present) per feature.
