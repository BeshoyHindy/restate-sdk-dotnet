# 09 — CoreVM Parity Audit (sdk-shared-core v0.10.0)

Authoritative consolidated gap analysis for the Restate .NET SDK against `sdk-shared-core`
v0.10.0 (CoreVM trait `third_party/sdk-shared-core/src/lib.rs`, VM impl `src/vm/`, protocol
`service-protocol/dev/restate/service/protocol.proto`). This is the FINAL re-audit after the
Batch A–J implementation sweep; it supersedes all per-area drafts and the earlier
`NEAR_PARITY` snapshot.

## 1. Overall parity verdict

**FULL_PARITY.** Every CoreVM *operation* (state, calls/sends, run, awaitables, awakeables,
named signals, promises, combinators, lifecycle, suspension, inbound + child cancellation,
attach/get-output, request-identity, discovery) is implemented and wire-correct, including the
previously-open MEDIUM/HIGH clusters: durable retry accounting (G1–G3, G15–G17, G24), call/send
options (G5/G8/G20), attach/get-output targets (G4/G6/G7), diagnostics & error fidelity
(G9/G21/G22/G33), V6 Failure metadata (G11/G23), protocol-version negotiation (G12), state
key-set parity (G18), run fan-out + RetryPolicy ergonomics (G14/G25/G26), reject/complete
arbitrary codes (G28/G29/G30), typed clear (G32), manifest extras (G36), and the strict replay
payload-equality mode (G13/G19/G31/G38). There are **zero correctness/determinism-breaking
defects** in the implemented surface, and every journaled command option added in the sweep is
also REPLAY-VALIDATED (Batch E command-equality hardening).

What remains is a small set of **5 intentional, correct-by-design divergences** plus the
intentional NA/HAVE items in §4 — none of which is a parity defect. The two final LOW items
(G27 run-closure `EntryRetryInfo`, G33 replay-frontier debug-log suppression) are now CLOSED, and
the optional manifest `jsonSchema` field (G35) is documented as an accepted optional-field
divergence.

## 2. Per-gap status (G1–G36)

`Status` is CLOSED (implemented + tested) or ACCEPTED_DIVERGENCE (correct-by-design, not a
defect). `det?` flags determinism relevance.

| # | Op / feature | Status | det? | Notes / where |
|---|---|---|---|---|
| G1 | `infer_entry_retry_info()` seeds first post-replay attempt's retry count/duration | CLOSED | YES | `InvocationStateMachine.cs` InferEntryRetryInfo + `…Operations.cs` RunWithRetryCore seed |
| G2 | `StartMessage.retry_count_since_last_stored_entry` (field 7) parsed | CLOSED | YES | `ProtobufCodec.ParseStartMessage` → `Initialize` |
| G3 | `StartMessage.duration_since_last_stored_entry` (field 8) parsed | CLOSED | YES | same; zeroed when retry_count==0 (Rust quirk preserved) |
| G4 | `get_call_invocation_id` on a blocking Call | CLOSED | no | `CallHandle<T>` exposes the invocation-id completion |
| G5 | Call/Send `headers` option | CLOSED | journaled | `CallOptions`/`SendOptions`.Headers; replay-validated as an unordered set |
| G6 | `sys_attach_invocation` WorkflowId & IdempotencyId targets | CLOSED | no | `Attach(AttachTarget)`; target replay-validated |
| G7 | `sys_get_invocation_output` WorkflowId & IdempotencyId targets | CLOSED | no | `GetOutput(AttachTarget)`; target replay-validated |
| G8 | Empty idempotency-key rejected before journaling | CLOSED | no | CallPrefix/SendPrefix validation |
| G9 | Protocol-violation codes JOURNAL_MISMATCH(570)/PROTOCOL_VIOLATION(571) | CLOSED | no | `ProtocolException` code threaded through `FailAsync` |
| G10 | Inbound CANCEL: cancel UNRESOLVED child invocation ids | ACCEPTED_DIVERGENCE | det. but diverges | unresolved-child-skip — see §3 item 1 |
| G11 | `Failure.metadata` / `FailureMetadata` (V6) round-tripped | CLOSED | no | `TerminalException.Metadata`; emission gated on negotiated V6 |
| G12 | Inbound protocol-version negotiation/validation from CONTENT_TYPE | CLOSED | gates V6 | host parses `/invoke` content-type, rejects <V5/>V6 with 415; threads Version |
| G13 | `VMOptions.non_determinism_checks` strict default + payload byte-equality on replay | ACCEPTED_DIVERGENCE | YES | default-Disabled — see §3 item 2 (opt-in via `RestateOptions.PayloadReplayChecks`) |
| G14 | Detached RunFuture takes a RetryPolicy | CLOSED | asymmetry removed | `RunAsync(name, action, RetryPolicy)` overload |
| G15 | Run retryable-failure: server-side re-drive vs in-process loop | CLOSED | — | HYBRID model: bounded → in-process fast path; unbounded → RunRedriveException → runtime re-drive |
| G16 | Run `next_retry_delay` driven by RetryPolicy on retryable failure | CLOSED | no | computed from the run's policy, rides the redrive Error frame |
| G17 | `RetryPolicy::default()` Infinite; no-policy Run = single attempt | CLOSED | default fixed | no-policy Run now defaults to Infinite (runtime re-drive) |
| G18 | `sys_state_get_keys` includes cleared-marker keys when map complete | CLOSED | journal byte-parity | filter removed |
| G19 | `PayloadOptions.unstable_serialization` per-op | CLOSED | YES | `PayloadOptions` on Set/Call/ResolveAwakeable/ResolvePromise/Output |
| G20 | Call/Send `name` (custom command/entry name, field 12) | CLOSED | cosmetic | `CallOptions`/`SendOptions`.Name; replay-validated |
| G21 | `ErrorMessage.stacktrace` (field 3) | CLOSED | no | populated from exception in the error emit paths |
| G22 | `ErrorMessage.related_command_index/name/type` (notify_error) | CLOSED | diagnostic | current command index/type/name threaded into ErrorMessage |
| G23 | Run terminal failure carries `TerminalFailure.metadata` | CLOSED | no | subset of G11 on the run proposal path |
| G24 | Run exhausted-retry terminal code preserves original error code | CLOSED | no | original code preserved through exhaustion |
| G25 | `RetryPolicy::FixedDelay` factory | CLOSED | no | `RetryPolicy.FixedDelay(interval, maxAttempts)` |
| G26 | `RetryPolicy.max_interval` Option (uncapped) | CLOSED | no | nullable/uncapped `MaxDelay` supported |
| G27 | `EntryRetryInfo` (retry_count/loop_duration) exposed to the `ctx.Run` closure | CLOSED | no | `IRunContext.RetryInfo` ← `InferEntryRetryInfo()` snapshot (see §5) |
| G28 | `sys_complete_awakeable` reject with arbitrary code | CLOSED | no | `RejectAwakeable(id, reason, ushort code)` overload |
| G29 | `sys_complete_signal` failure with arbitrary code | CLOSED | no | `SendSignalFailure(…, ushort code)` overload |
| G30 | `sys_complete_promise` reject with arbitrary code | CLOSED | no | `RejectPromise(…, ushort code)` overload |
| G31 | `sys_state_set` PayloadOptions.unstable_serialization | CLOSED | YES | subset of G19 on the Set path |
| G32 | Typed `Clear<T>(StateKey<T>)` overload | CLOSED | no | added for symmetry with typed Get/Set |
| G33 | `invocation_debug_logs` — suppress per-op debug log on replay-frontier resume | CLOSED | noise | `Log.SideEffectExecuted` gated on the closure actually executing (see §5) |
| G34 | DurableRandom: u64 seed folded to Int32 | ACCEPTED_DIVERGENCE | no | random-seed fold — see §3 item 5 |
| G35 | EndpointManifest input/output `jsonSchema` (optional) | ACCEPTED_DIVERGENCE | no | optional-field — see §3 item below; in-code note in `EndpointManifest.cs` |
| G36 | EndpointManifest service-level `documentation` & `metadata` | CLOSED | no | `ServiceDefinition.Documentation`/`Metadata` → manifest |

> The unresolved-child-cancel guarantee (G10) and the detached-RunFuture redrive degradation are
> the two behavioral divergences in the retry/cancel area; both are documented below.

## 3. The 5 accepted divergences (correct-by-design, NOT defects)

These must not be "fixed" into a regression:

1. **Unresolved-child-cancel skip (G10).** On inbound CANCEL the terminal-cancel unwinding path
   deterministically cancels only RESOLVED child invocations; children whose invocation-id has not
   yet been delivered are skipped, because suspending-to-resolve-child-id is impossible on the
   unwinding path. shared-core's `do_progress` suspends to fetch unresolved child ids then cancels
   all (`mod.rs:445-476`). The .NET behavior is deterministic, replay-safe, and a documented scope
   limit — closing it needs a suspend-to-resolve mechanic on the cancel path.

2. **Payload-checks default-Disabled (G13).** shared-core's `VMOptions.non_determinism_checks`
   defaults to comparing replayed payload bytes (state/request/awakeable) to catch
   non-deterministic serializers (`lib.rs:237-262`). The .NET default is `PayloadChecksDisabled`
   (it compares ids/targets/headers/idempotency/names but NOT value payload bytes), because
   System.Text.Json emits unordered-collection bytes that would spuriously fail a CORRECT replay.
   The strict mode IS implemented and opt-in via `RestateOptions.PayloadReplayChecks`
   (G13/G19/G31), plus per-op `PayloadOptions.UnstableSerialization` opt-outs — the divergence is
   only the safe-by-default mode, not a missing capability.

3. **Detached-RunFuture redrive degradation.** A blocking `ctx.Run` with an unbounded policy
   re-drives through the runtime (RunRedriveException → retryable Error frame), the canonical
   crash-safe path. A DETACHED fan-out run (`ctx.RunAsync` → RunFuture) runs off-stack and cannot
   unwind the handler to request a re-drive, so a non-terminal failure under an unbounded policy
   degrades to a per-future retryable `TerminalException(500)` carrying the failure reason rather
   than a runtime re-drive. Bounded policies still run the full in-process budget on both paths.
   This is a deliberate, documented asymmetry of the detached path.

4. **Exhausted-retry 500-on-non-terminal-only.** When a bounded run policy EXHAUSTS on a
   non-terminal failure, the SDK proposes a terminal failure coerced to HTTP 500 (the generic
   business-failure code) — the original non-terminal exception had no protocol error code to
   preserve. A user-thrown `TerminalException` keeps its own code (G24); only the
   policy-exhaustion-of-a-non-terminal case maps to 500, which is the correct terminal shape for
   an exhausted transient failure.

5. **Random-seed u64→Int32 fold (G34).** `DurableRandom` folds the u64 invocation seed to an
   Int32 via `seed ^ (seed >> 32)` to feed `System.Random`. This is deterministic and replay-safe
   WITHIN .NET; cross-SDK random values need not match (each SDK uses its own PRNG). A quality note
   only, not a correctness gap.

### Accepted optional-field divergence — manifest `jsonSchema` (G35)

The discovery-manifest schema marks the input/output `jsonSchema` field OPTIONAL, and the runtime
never requires it — it is discovery-only metadata (ingress request validation / OpenAPI
generation), not used for handler dispatch. This SDK does not generate JSON Schema for handler
payload types, so `PayloadDescriptor` omits it; the fields the runtime DOES need (`contentType`,
`required`, `setContentTypeIfEmpty`) are emitted. Generating `jsonSchema` would require a
payload-type → JSON-Schema generator (out of scope). The omission is an accepted, runtime-safe
optional-field divergence, documented in-code in `Internal/Discovery/EndpointManifest.cs`.

## 4. Intentional / acceptable design choices (NA / HAVE)

These are correct-by-design boundary/structural choices, not gaps:

- **Client ingress (`RestateClient`)** — Restate's ingress HTTP API has no shared-core module; the
  .NET `RestateClient` (Call/Send/Attach/GetOutput/Cancel + idempotency/delay) is correctly an
  SDK-side client, not a VM op. NA.
- **Combinators (All/Any/Race/AllSettled/WaitAll)** — shared-core exposes only `do_progress` over
  handle vecs; the .NET combinators are the correct SDK-layer mapping. HAVE.
- **`ctx.Now` as a named Run** — no `sys_now`/`sys_time` syscall exists; durable time via a named
  Run (`__restate_now`) matches the cross-SDK convention. HAVE.
- **`do_progress`/`take_notification`/`is_completed`** — collapsed into the managed
  `AwaitNotificationAsync` await loop; a faithful async/await expression of the sans-IO loop. HAVE.
- **sans-IO inversion (`notify_input`/`notify_input_closed`)** — the SM owns the reader and pumps;
  observably equivalent. HAVE.
- **Response head produced by ASP.NET, not the SM** — correct boundary for a managed host. HAVE.
- **`sys_send` children NOT tracked for cancel** — matches the `cancel_children_one_way_calls=false`
  default. HAVE.
- **CANCEL → terminal 409** — shared-core defines no cancel code; 409 is the SDK convention. HAVE.
- **`now_since_unix_epoch` sleep debug param omitted** — debugging-only, no proto field.
  Wire-equivalent. HAVE.
- **`CommandAck`/`EntryAck` no-op** — run durability is signalled by `RunCompletion`, not the ack,
  in V5+. Matches shared-core. HAVE.
- **`last_command_index`/`is_replaying`/`is_processing` not on the public context** — used
  internally and correctly; kept internal as in most SDKs. NA for the user surface.

## 5. Final LOW closures — G27 & G33

The two trailing LOW items, closed in the final pass:

- **G27 — `EntryRetryInfo` on the run-closure context.** `IRunContext` now exposes
  `RetryInfo` (RetryCount + RetryLoopDuration), the .NET surface of the `EntryRetryInfo` shared-core
  passes to the run (`vm/context.rs:461-479`). `DefaultContext.Run(Func<IRunContext, …>)` builds the
  `RunContext` at closure-invocation time and snapshots the SM's current `InferEntryRetryInfo()` —
  the SAME seed the retry loop reads — so a backoff-aware closure observes `RetryCount==0` on a fresh
  first attempt and the StartMessage-seeded count after a runtime re-drive of the first committed run.
  `IRunContext` stayed source-compatible (a member was added; existing closures are unaffected; the
  `MockContext` test double surfaces `EntryRetryInfo.Zero`). Tests:
  `RetryModelParityTests.RunContext_FirstAttempt_SeesZeroRetryInfo` and
  `…RunContext_FirstRunAfterRedrive_SeesSeededRetryCount`.

- **G33 — replay-frontier debug-log suppression.** `Log.SideEffectExecuted` (`Operations.cs`
  RunAsync/RunSync) previously fired unconditionally after the ACK barrier, re-logging on replay when
  a buffered completion was consumed WITHOUT executing. It is now gated on `executesLocally` — the
  closure actually ran live or claimed the replay frontier — mirroring shared-core, which logs run
  execution only from `propose_run_completion` under `is_processing()` (`vm/mod.rs:204-210,
  1122-1131`). Tests:
  `RunReplayLogSuppressionTests.LiveRun_ExecutesClosure_LogsSideEffectExecuted` (logs once) and
  `…ReplayedRun_ConsumesBufferedCompletion_DoesNotLogSideEffectExecuted` (logs zero).

## 6. Batch E — replay command-equality hardening (DONE)

The Batch A/B/C reviews flagged that journaled command options were emitted but not replay-validated,
so a non-deterministic handler could silently swap them on replay. Batch E closed that, mirroring the
pre-existing Call target-triple + SendSignal replay validation:

- **Call/Send `headers`** validated as an order-INDEPENDENT key→value SET (not byte order — the live
  headers come from an UNORDERED `IReadOnlyDictionary`, so a byte-order compare would spuriously fail
  a CORRECT replay). Divergence → JOURNAL_MISMATCH(570).
- **Call/Send `idempotency_key`** — plain ordinal string compare. Divergence → 570.
- **Attach/GetOutput `target`** — whole oneof (InvocationId / WorkflowTarget{name,key} /
  IdempotentRequestTarget{…}) normalized and compared by value. Divergence → 570.
- **Named-signal `SendSignalFailure`** — arbitrary code (no longer hardcodes 500).

Out of scope by design: value PAYLOAD byte-equality on replay is the opt-in **G13** strict mode
(accepted divergence #2); .NET still compares ids/targets/headers/idempotency/names but not payload
bytes unless `PayloadReplayChecks` is enabled.
