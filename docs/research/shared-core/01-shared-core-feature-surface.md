# sdk-shared-core (v0.10.0) — Feature Surface Inventory

> Research artifact for the `feat/sdk-shared-core` effort. Source: `restatedev/sdk-shared-core@main`.
> Goal: port what the Rust core implements into the **pure-managed** .NET SDK (no native dep).

## What shared-core is

A **sans-IO** state machine (`CoreVM` implementing the `VM` trait). It owns the journal/replay
logic and the wire protocol; it does **no networking** — the host SDK feeds it input bytes
(`notify_input`), pulls output bytes (`take_output`), and drives an await loop
(`do_await`/`take_notification`). Used by sdk-rust (native), sdk-python (PyO3), sdk-typescript (WASM).

The .NET port keeps its own managed state machine but must reproduce the **observable semantics**
of this VM.

## The `VM` trait (the contract — ~40 methods)

- Lifecycle: `new`, `get_response_head`, `notify_input`, `notify_input_closed`, `notify_error`,
  `take_output`, `is_ready_to_execute`, `state()`, `last_command_index()`, `sys_end`.
- Async results: `is_completed`, `do_await(UnresolvedFuture) -> AwaitResponse`,
  `take_notification(handle) -> Option<Value>`.
- Syscalls: `sys_input`, `sys_state_get`/`_get_keys`/`_set`/`_clear`/`_clear_all`,
  `sys_sleep`, `sys_call`, `sys_send`, `sys_awakeable`, `sys_complete_awakeable`,
  `create_signal_handle`, `sys_complete_signal`, `sys_get_promise`/`sys_peek_promise`/`sys_complete_promise`,
  `sys_run` + `propose_run_completion`, `sys_cancel_invocation`,
  `sys_attach_invocation`, `sys_get_invocation_output`, `sys_write_output`.

State enum: `WaitingPreFlight(0) -> Replaying(1) -> Processing(2) -> Closed(3)`.

## Protocol versions

`Version` enum `V1..V7`; `minimum_supported_version = V5`, `maximum_supported_version = V7`.
Content type `application/vnd.restate.invocation.v{N}`. **V7 is fully implemented.**

| Feature | Min version |
|---|---|
| Terminal-error `Failure.metadata` | V6 |
| `StartMessage.random_seed` | V6 |
| `scope` / `limit_key` on Start/Call/OneWayCall/targets | V7 |
| `idempotency_key` on targets | V5 (call), V7 (some targets) |
| `should_pause` on errors; `OnMaxAttempts::Pause` | V7 |
| `Future` combinator tree / `AwaitingOnMessage` | V7 |
| `ProposeRunCompletionAck` semantics | V7 |

### MessageType codes (16-bit, top bits of 64-bit header; len in low 32)
Masks: command `0x0400`, notification `0x8000`, custom `0xFC00`.

Control: Start `0x0000`, Suspension `0x0001`, Error `0x0002`, End `0x0003`,
ProposeRunCompletion `0x0005`, AwaitingOn `0x0006` (V7), ProposeRunCompletionAck `0x0007` (V7).

Commands `0x04xx` → completion notifications `0x80xx`:
Input `0400`, Output `0401`, GetLazyState `0402`→`8002`, SetState `0403`, ClearState `0404`,
ClearAllState `0405`, GetLazyStateKeys `0406`→`8006`, GetEagerState `0407`, GetEagerStateKeys `0408`,
GetPromise `0409`→`8009`, PeekPromise `040A`→`800A`, CompletePromise `040B`→`800B`,
Sleep `040C`→`800C`, Call `040D`→`800D`+`800E`(invocation-id), OneWayCall `040E`→`800E`,
SendSignal `0410` (also CancelInvocation, signal idx 1), Run `0411`→`8011`,
AttachInvocation `0412`→`8012`, GetInvocationOutput `0413`→`8013`, CompleteAwakeable `0414`.
Signal notification (named/unnamed) `0xFBFF`. Built-in signal `CANCEL = 1`; ids 2–15 reserved.

### V7 combinator `Future` message
`waiting_completions: [u32]`, `waiting_signals: [u32]`, `waiting_named_signals: [string]`,
`nested_futures: [Future]`, `combinator_type`:
`UNKNOWN=0` (≈FIRST_COMPLETED), `FIRST_COMPLETED=1`, `ALL_COMPLETED=2`,
`FIRST_SUCCEEDED_OR_ALL_FAILED=3`, `ALL_SUCCEEDED_OR_FIRST_FAILED=4`.

## Future combinators (`do_await` + `UnresolvedFuture`) — maps to JS Promise

| UnresolvedFuture | Semantics | JS equiv |
|---|---|---|
| `Single(h)` | resolve handle | await |
| `FirstCompleted` / `Unknown` | any child completes (success OR failure) | `Promise.race` |
| `AllCompleted` | every child settles, always success | `Promise.allSettled` |
| `FirstSucceededOrAllFailed` | first success wins; fail only if all fail | `Promise.any` |
| `AllSucceededOrFirstFailed` | first failure short-circuits; success when all succeed | `Promise.all` |

The core resolves `ready` **non-destructively**; the SDK then consumes via `take_notification`
(destructive). `do_await` returns `AwaitResponse`:
- `AnyCompleted` — a subtree is ready; SDK should `take_notification`.
- `WaitingExternalProgress { waiting_input, waiting_run_proposal }` — need more input / a run proposal.
- `ExecuteRun(handle)` — SDK should run the pending `ctx.run` closure.
- `CancelSignalReceived` — only when implicit cancellation enabled.

During **Replaying**, if a combinator can't resolve, the core emits `UncompletedDoProgressDuringReplay`
(code 570) — this is how it detects that the user mutated handler code (added an await).

## Retry policy (`retries.rs`)

`RetryPolicy`: `Infinite` (default), `None`, `FixedDelay{interval,max_attempts,max_duration,on_max_attempts}`,
`Exponential{initial_interval,factor,max_interval,max_attempts,max_duration,on_max_attempts}`.
`OnMaxAttempts`: `FailAsTerminal` (default) | `Pause` (V7+).
Exponential delay = `min(max_interval, initial_interval * factor^(retry_count-1))`.

`propose_run_completion(handle, RunExitResult, RetryPolicy)` where
`RunExitResult = Success(Bytes) | TerminalFailure(TerminalFailure) | RetryableFailure{attempt_duration, error}`.
**The runtime evaluates the retry policy** — retryable failures emit an `ErrorMessage` with
`next_retry_delay`/`should_pause`; the run completion is cached per-attempt and surfaced as a
`RunCompletionNotification` once `ProposeRunCompletionAck` arrives (V7).

## Error model (`error.rs`)

`Error { code: u16, message, stacktrace, related_command, next_retry_delay, should_pause }`.
Well-known codes: BAD_REQUEST 400, UNSUPPORTED_MEDIA_TYPE 415, INTERNAL 500,
**JOURNAL_MISMATCH 570**, **PROTOCOL_VIOLATION 571**, AWAITING_TWO_ASYNC_RESULTS 572,
UNSUPPORTED_FEATURE 573, CLOSED 598, **SUSPENDED 599** (sentinel — not a real failure, signals yield).
No dedicated cancellation code — **cancellation is a signal** (`CANCEL_SIGNAL_ID=1`), surfaced as
`AwaitResponse::CancelSignalReceived`. Terminal (user) failures travel as `TerminalFailure{code,message,metadata}`.

## Implicit cancellation

`ImplicitCancellationOption::Enabled{ cancel_children_calls: true, cancel_children_one_way_calls: false }` (default).
Every `do_await` also waits on `CANCEL_NOTIFICATION_HANDLE(1)`. On cancel: resolve tracked child
invocation ids, `sys_cancel_invocation` each, return `CancelSignalReceived`. `sys_call`/`sys_send`
register tracked invocation ids.

## Request identity (`request_identity.rs`, feature-gated)

Headers `x-restate-signature-scheme` (`v1`|`unsigned`) + `x-restate-jwt-v1`. Keys `publickeyv1_<base58>`
→ base58-decode → 32-byte **Ed25519**. JWT alg **EdDSA**, required claims `aud`/`exp`/`iat`/`nbf`,
leeway 0, **audience = normalized request path** (`/invoke/<svc>/<handler>` or `/discover`).
No keys configured → pass-through. The .NET SDK needs: EdDSA JWT verify, base58 decode,
`publickeyv1_` prefix, path normalization, v1/unsigned scheme handling.
