# Restate SDK Context API — Parity Matrix (Rust / Python / TS)

> All three official SDKs sit on `sdk-shared-core`, so *protocol* capability is identical;
> differences are only in what each **surfaces to handler authors**. Used to define the target
> public surface for the managed .NET SDK.

## Context hierarchy (same shape everywhere)

Service / Object (exclusive) / ObjectShared (read-only) / Workflow (`run`) / WorkflowShared.
Write-state + durable promises only on exclusive object / workflow-run contexts.

## Feature matrix

| Feature | Rust | Python | TypeScript |
|---|---|---|---|
| **State** get/set/clear/clearAll/keys | `get/set/clear/clear_all/get_keys` | `get/set/clear/clear_all/state_keys` | `get/set/clear/clearAll/stateKeys` |
| **Run** | `ctx.run(closure).name().retry_policy()` | `ctx.run_typed(name, action, RunOptions, *args)` | `ctx.run(name?, action, opts?)` |
| Run retry knobs | initial/maxDelay/factor/maxAttempts/maxDuration | same 5 | same 5 |
| **Sleep** | `ctx.sleep(Duration)` | `ctx.sleep(timedelta, name?)` | `ctx.sleep(ms, name?)` |
| **Call** (req/resp) | client `.call()` | `ctx.{service,object,workflow}_call` | client / `genericCall` |
| **Send** (one-way) | `.send()` / `.send_after()` | `ctx.*_send(send_delay=)` | `*SendClient` / `genericSend(delay)` |
| idempotency key | `Request::idempotency_key` | `idempotency_key=` | `idempotencyKey` |
| custom headers | `Request::header` | `headers=` | `headers` |
| V7 scope / limit_key | **core-only** | **core-only** | **core-only** |
| **Awakeable** create/resolve/reject | `awakeable/resolve_awakeable/reject_awakeable` | same | `awakeable/resolveAwakeable/rejectAwakeable` |
| Named signals | **none (user-facing)** | **none** | **none** |
| **Promise** (workflow) get/peek/resolve/reject | `promise/peek_promise/resolve_promise/reject_promise` | `promise(name).value/peek/resolve/reject` | `promise(name).get/peek/resolve/reject` |
| **Combinators** | `select!{}` macro + `DurableFuturesUnordered` | `gather/select/as_completed/wait_completed` | `RestatePromise.all/any/race/allSettled` + `.orTimeout()` |
| **Attach invocation** | `ctx.invocation_handle(id)` | `ctx.attach_invocation(id)` | `ctx.attach(id)` |
| **Cancel** | `InvocationHandle::cancel()` | `ctx.cancel_invocation(id)` | `ctx.cancel(id)` |
| **Typed clients** | `#[service]/#[object]/#[workflow]` macros | decorators | `serviceClient/objectClient/workflowClient` |
| deterministic random / uuid | `ctx.rand()/rand_uuid()` | `ctx.random()/uuid()` | `ctx.rand.random()/uuidv4()` |
| durable now | — (sleep only) | `ctx.time()` | `ctx.date.now()/toJSON()` |
| replay-aware logging | `tracing` | logging filter | `ctx.console` |
| request introspection | `invocation_id/headers/key` | `ctx.request()` | `ctx.request()` |

## Section: combinators (the important one), JS Promise mapping

**TypeScript** `RestatePromise` static methods — the canonical 4, mapping 1:1 to shared-core modes:
- `all` → `AllSucceededOrFirstFailed`
- `any` → `FirstSucceededOrAllFailed` (else `AggregateError`)
- `race` → `FirstCompleted` (settle on first, success or failure)
- `allSettled` → `AllCompleted`
- per-promise `.orTimeout(ms)` (race vs durable timer); **`.map` not `.then`** to chain without
  suspending (TS-specific; .NET avoids this via explicit await/combinator methods).

**Rust** `select!{ pat = fut => …, on_cancel => …, else => … }` (≈race) + `DurableFuturesUnordered`
(as-completed stream). No `all`/`allSettled` helper — await each journaled future directly.

**Python** module-level `restate.asyncio`: `gather` (≈all), `select(**named)` (≈race),
`as_completed` (stream), `wait_completed` → `(completed, uncompleted)` (low-level primitive).
Must NOT use plain `asyncio.gather`/`wait`.

## Notes / decisions for .NET

1. **`run` shape diverges most** — pick one canonical `RunOptions`/builder. Retry knob set is
   identical across all three (5 knobs).
2. **Retry-policy defaults differ**: Rust 100ms/×2/2s cap/50s total/∞ attempts; TS 50ms/×2/10s;
   Python 50ms/×2 if any field set, else server policy. **Decide .NET defaults explicitly.**
3. **Signals are not user-facing** in any SDK yet — don't block parity on a signal primitive
   (awakeables already ride the signal mechanism).
4. **V7 scope/limit_key is core-only** — not surfaced by any SDK; safe to omit from first pass.
5. **Durable now**: TS + Python expose it, Rust doesn't. .NET already has `ctx.Now()` — keep.
6. **Attach by idempotency/workflow id** is not a distinct Context method anywhere — only
   attach-by-invocation-id is in-handler. Ingress/admin client is a separate surface.
