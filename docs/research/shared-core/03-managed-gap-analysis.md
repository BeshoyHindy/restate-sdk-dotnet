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

### P1 — Runtime-driven run retry + next-retry-delay — ✅ DONE (Pause deferred to V7)
- **Finding:** at v0.10.0/V6 the `ProposeRunCompletionMessage` carries no retry policy and
  `ErrorMessage` has no `should_pause` field. Runtime-driven retry is achieved purely via
  `ErrorMessage.next_retry_delay` + the server re-invoking. The SDK's *default* `Run` (no policy)
  was **already** runtime-driven: failures bubble to a retryable `ErrorMessage` and Restate retries
  durably — i.e. "defer to server when unset" was already the behavior.
- **Added:** `RestateRetryableException` (public) → handler boundary maps it to
  `FailAsync(code, message, nextRetryDelay)` → `ErrorMessage.next_retry_delay` (field 8). This is
  shared-core's `Error::with_next_retry_delay_override`.
- The in-process `Task.Delay` loop (`ctx.Run(.., RetryPolicy)`) remains the explicit **SDK-local
  opt-in** for bounded retries within a single attempt.
- **Deferred:** `OnMaxAttempts::Pause` / `should_pause` — V7-only (absent from the v0.10.0 proto).

### P2 — Request identity verification (Ed25519 JWT) — ✅ DONE
- **Finding:** .NET 10 has **no** Ed25519 in the BCL, and NSec/libsodium is native (would break the
  no-native-deps/AOT promise). Chose **BouncyCastle.Cryptography** (pure-managed) for Ed25519 and
  **SimpleBase** for base58 — both managed.
- **Layering (AOT-safe):** core (`Restate.Sdk`) gets the boundary abstraction
  `IRequestIdentityVerifier` + `RequestIdentityResult` + a pass-through `NoOpRequestIdentityVerifier`
  default, and `MapRestate` verifies on `/discover` + `/invoke` (→ 401 on rejection). The crypto
  lives in a **new `Restate.Sdk.Identity` package** (`Ed25519RequestIdentityVerifier`,
  `AddRestateRequestIdentity(keys)`), so core — and the Native AOT samples — take **no** crypto
  dependency. Verifies scheme (`v1`/`unsigned`), JWT (EdDSA, required aud/exp/iat/nbf, leeway 0),
  `aud` = normalized path, and Ed25519 signature against any configured `publickeyv1_<base58>` key.
- Covered by unit tests (valid/expired/nbf/aud/wrong-key/tampered/unsigned/malformed-key) **and HTTP
  E2E tests** via `TestServer` (`/discover` + `/invoke` accept/reject).

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
