# 09 — CoreVM Parity Audit (sdk-shared-core v0.10.0)

Authoritative consolidated gap analysis for the Restate .NET SDK against `sdk-shared-core`
v0.10.0 (CoreVM trait `third_party/sdk-shared-core/src/lib.rs`, VM impl `src/vm/`, protocol
`service-protocol/dev/restate/service/protocol.proto`). This supersedes the per-area drafts and
deduplicates overlapping findings. Read-only audit — no `src`/`test` edits were made.

## 1. Overall parity verdict

**NEAR PARITY.** Every CoreVM *operation* (state, calls/sends, run, awaitables, awakeables,
named signals, promises, combinators, lifecycle, suspension, inbound + child cancellation,
attach/get-output, request-identity, discovery) is implemented and wire-correct on the happy
path. There are **zero correctness/determinism-breaking defects** in the implemented surface.

The remaining deltas fall into four buckets:

1. **Durable retry accounting (HIGH).** The `ctx.Run` retry loop is fully client-side and does
   not consume `StartMessage.retry_count_since_last_stored_entry` / `duration_since_last_stored_entry`
   (proto fields 7/8 — present in the generated `Protocol.cs` but never parsed). `MaxAttempts`/
   `MaxDuration` are therefore per-in-process-loop, not cumulative across runtime re-drives.
2. **Missing command options (MEDIUM).** Per-call/send custom `headers` and custom `name`;
   workflow/idempotency `Attach`/`GetOutput` targets; empty-idempotency-key validation.
3. **Diagnostic/observability fidelity (LOW–MEDIUM).** Protocol-violation error codes collapse
   to 500 (vs 570/571); `ErrorMessage.stacktrace` and `related_command_*` never set; replay-only
   debug-log suppression absent; `Failure.metadata` (V6) not round-tripped.
4. **VMOptions / PayloadOptions configurability (MEDIUM/LOW).** The Rust defaults
   (`cancel_children_calls=true`, `cancel_children_one_way_calls=false`, non-determinism checks
   on) are baked in correctly but not configurable; per-op `unstable_serialization` is absent.
   Note: .NET never compares payload bytes on replay, so it already behaves as
   `PayloadChecksDisabled` — the strict default mode is what's missing, not the relaxed one.

Counts (deduplicated, unique items): **6 HIGH-or-MEDIUM gaps to close**, plus a long LOW tail
of observability/configuration parity and one MEDIUM documented behavioral limitation
(unresolved-child-skip) that is an accepted scope limit, not a bug.

## 2. PARTIAL / MISSING gap table (deduplicated)

`priority` is the max across areas where an item appeared. `det?` flags determinism relevance.

| # | Op / feature | Status | Shared-core behavior | .NET change needed | det? | Pri |
|---|---|---|---|---|---|---|
| G1 | Durable retry accounting: `infer_entry_retry_info()` seeds first post-replay attempt's `retry_count`/`retry_loop_duration` from StartInfo | MISSING | `journal.rs:748-754`, `context.rs:461-479`: on the first entry after replay, seed retry count/duration from `StartMessage` so `max_attempts`/`max_duration` are cumulative across runtime re-invocations | Parse `StartMessage` fields 7/8 (already in generated `Protocol.cs:449-478`), thread into the SM, and seed `ExecuteWithRetryAsync` (`Operations.cs:216-277`) attempt/elapsed from them instead of `attempt=0`/`startTime=now` | YES | HIGH |
| G2 | `StartMessage.retry_count_since_last_stored_entry` (field 7) not parsed | MISSING | `input.rs:42` stores it on StartInfo (feeds G1, EntryRetryInfo) | `ParseStartMessage` (`ProtobufCodec.cs:46-64`) read field 7 into `StartMessageFields`/`Initialize` | YES | HIGH |
| G3 | `StartMessage.duration_since_last_stored_entry` (field 8) not parsed | MISSING | `input.rs:43`; zeroed when retry_count==0 (`context.rs:466-473`) | Same as G2 for field 8 | YES | HIGH |
| G4 | `get_call_invocation_id` — a blocking `Call` cannot expose its invocation id | MISSING | `CallHandle` exposes both `call_notification_handle` and `invocation_id_notification_handle` (`lib.rs:141-145`); `Value::InvocationId` (`lib.rs:160-161`) | Return a richer call handle / `CallFuture` whose `GetInvocationIdAsync` parks on the already-allocated invocation-id completion id (`Operations.cs:427-429`). Plumbing exists for `Send` (`InvocationHandle.cs`) but not `Call` | no | HIGH |
| G5 | `sys_call`/`sys_send` option: `headers` (Target.headers → CallCommand.headers field 4 / OneWayCall.headers field 5) | MISSING | `mod.rs:752-756`, `824-828` map `Target.headers: Vec<Header>` (`lib.rs:121`) onto the command | Add `Headers` to `CallOptions`/`SendOptions`; populate `CallCommandMessage.Headers` / `OneWayCallCommandMessage.Headers` in `ProtobufCodec` | no (journaled) | MED |
| G6 | `sys_attach_invocation` — WorkflowId & IdempotencyId targets | MISSING | `AttachInvocationTarget` = InvocationId \| WorkflowId{name,key} \| IdempotencyId{…} (`lib.rs:205-218`, `mod.rs:1199-1234`); generated `WorkflowTarget`/`IdempotentRequestTarget` exist | Add `Attach` overloads; set `AttachInvocationCommandMessage.WorkflowTarget`/`.IdempotentRequestTarget` (`ProtobufCodec.cs:595-602`, `IContext.cs:116`) | no | MED |
| G7 | `sys_get_invocation_output` — WorkflowId & IdempotencyId targets | MISSING | `mod.rs:1238-1268`; same target oneof on `GetInvocationOutputCommandMessage` | Add `GetOutput` overloads; set the two targets (`ProtobufCodec.cs:604-611`, `IContext.cs:119`) | no | MED |
| G8 | `sys_call`/`sys_send` validation: empty idempotency_key rejected | MISSING | `mod.rs:735-740`, `810-815`: empty key → `HitError(EMPTY_IDEMPOTENCY_KEY)`; proto requires non-empty (`protocol.proto:420-421`, `472-473`) | Throw (before journaling) when a supplied idempotency key is empty, in `CallPrefixAsync`/`SendPrefix` (`Operations.cs:391-435`, `519-543`) | no | MED |
| G9 | Protocol-violation error codes JOURNAL_MISMATCH(570) / PROTOCOL_VIOLATION(571) | PARTIAL | `errors.rs:69-70,96-110,390-391`: known-entries-zero / unexpected-input / command-type-mismatch / uncompleted-do-progress map to 570/571 | Give `ProtocolException` a code field (570 journal-mismatch, 571 protocol-violation) and pass it through `FailAsync`; currently all emit 500 (`InvocationHandler.cs:116`) | no (runtime retries either way) | MED |
| G10 | Inbound CANCEL: cancel UNRESOLVED child invocation ids | PARTIAL | `mod.rs:445-476`: `do_progress` suspends to fetch unresolved child ids, then cancels all | Documented divergence #1 (`Operations.cs:1140-1148`): only RESOLVED children cancelled on the unwinding terminal path. Closing requires a suspend-to-resolve-child-id mechanic on the cancel path | deterministic but diverges from cancel-all guarantee | MED |
| G11 | `Failure.metadata` / `FailureMetadata` (V6) not round-tripped | MISSING | `proto:633-646`; `verify_error_metadata_feature_support` gates V6 (`mod.rs:118-124,1301`); `TerminalFailure.metadata` (`lib.rs:166-170`) | Add `Metadata` to `TerminalException`; emit `Failure.metadata` on output/awakeable/promise/signal/run failures; parse inbound; gate emission on negotiated V6 | no | MED |
| G12 | Inbound protocol-version negotiation/validation from CONTENT_TYPE | PARTIAL | `mod.rs:214-261`: reject outside [V5..V6] with 415 + RT0015; thread negotiated Version for feature gating | Parse the `/invoke` request content-type, reject <V5/>V6 with 415, thread `Version` into the SM. Currently only echoes for the response (`RestateEndpointRouteBuilderExtensions.cs:85-97`); no inbound validation | enables V6 feature gating | MED |
| G13 | `VMOptions.non_determinism_checks` strict default + payload byte-equality on replay | MISSING | `lib.rs:237-262`: default compares replayed payload bytes (state/request/awakeable), catching non-deterministic serializers | .NET never compares payloads on replay (only ids/targets/names) — it already behaves as `PayloadChecksDisabled`. Strict default mode + opt-out is absent | YES (can't catch serializer drift) | MED |
| G14 | RunFuture (detached fan-out) cannot take a RetryPolicy | MISSING | `propose_run_completion` takes a RetryPolicy regardless of blocking vs detached | `RunFutureAsync` hard-codes `retryPolicy: null` (`Operations.cs:345`); add `RunAsync(name, action, RetryPolicy)` overload (`IContext.cs:136`, `Context.cs:167`) | asymmetry vs blocking Run | MED |
| G15 | Run retryable-failure semantics: server-side retry vs in-process loop | PARTIAL | `journal.rs:756-766`: returns `Err(error)` with `next_retry_delay` from policy; the RUNTIME re-invokes (journal replays) | .NET loops in-process (`Task.Delay`) and only proposes terminal failure on exhaustion. Bounded fast retries OK; not equivalent for long/infinite backoff, leader-change survival, or server telemetry. (Architectural; couples with G1–G3) | YES (local sleep/count non-durable) | MED |
| G16 | Run retry: `next_retry_delay` driven by RetryPolicy on retryable failure | PARTIAL | `journal.rs:757-758`: policy → `Retry(Some(interval))` → `error.next_retry_delay` | Field is wired only from user-thrown `RestateRetryableException` (`InvocationHandler.cs:119-124`), never computed from the run's RetryPolicy. Tied to G15 | no | MED |
| G17 | `RetryPolicy::default()` is Infinite; .NET Run with no policy = single attempt | PARTIAL | `retries.rs:6-12`: default Infinite | `Run` overloads without a policy pass `retryPolicy=null` → zero retries (`Operations.cs:117-119`). Make no-policy default to the runtime/Infinite semantics, or document and add an explicit `Infinite` policy | behavioral default mismatch | MED |
| G18 | `sys_state_get_keys` includes cleared-marker keys when map is complete | PARTIAL | `context.rs:414-421`: `EagerState::get_keys` returns `values.keys()` unfiltered; cleared keys (Some(None)) ARE listed | `Operations.cs:812` filters `.Where(p => p.Value is not null)`, omitting cleared keys → different `GetEagerStateKeysCommand` key set vs CoreVM. Remove the filter (or confirm runtime quirk) | byte-parity of journal | MED |
| G19 | `PayloadOptions.unstable_serialization` per-op (state_set, call, complete_awakeable, complete_promise, write_output) | MISSING | `lib.rs:25-47,316,321,359,379,403`: per-entry flag to skip replay byte-check for non-deterministic serdes | Expose an opt-in on Set/Call/ResolveAwakeable/ResolvePromise/Output; only meaningful once G13's strict mode exists | YES (JSON stable here, low freq) | LOW |
| G20 | `sys_call`/`sys_send` option: `name` (custom command/entry name, field 12) | MISSING | `mod.rs:726,759,801,837` | Add `Name` to `CallOptions`/`SendOptions`; set `CallCommandMessage.Name` / `OneWayCallCommandMessage.Name` | no (cosmetic) | LOW |
| G21 | `ErrorMessage.stacktrace` (field 3) | MISSING | `transitions/mod.rs:91` sets from `Error.stacktrace` (often empty in Rust too) | Populate from `ex.StackTrace`/`ex.ToString()` in generic & ProtocolException catch arms (`ProtobufCodec.cs:613-624`, `Operations.cs:1184`) | no | LOW |
| G22 | `ErrorMessage.related_command_index/name/type` + `notify_error` CommandRelationship | MISSING | `mod.rs:344-358`, `transitions/mod.rs:92-102`; `CommandRelationship` Last/Next/Specific (`lib.rs:100-113`) | Thread current command index/type/name from journal into ErrorMessage on replay/journal-mismatch faults | no (diagnostic) | LOW |
| G23 | Run terminal failure cannot carry `TerminalFailure.metadata` | PARTIAL | `vm/mod.rs:1135-1139` gates V6 metadata; `journal.rs:741-743` | Subset of G11 — add metadata to `TerminalException` and `CreateRunProposalFailure` (`ProtobufCodec.cs:385-392`) | no | LOW |
| G24 | Run exhausted-retry terminal code preserves original error code | PARTIAL | `journal.rs:768-774`: DoNotRetry preserves `error.code` | .NET coerces to HTTP 500 (`Operations.cs:236-237,268-269`), losing the underlying exception's code | no | LOW |
| G25 | `RetryPolicy::FixedDelay` factory (interval optional → defer to invoker) | PARTIAL | `retries.rs:17-39,82-92` | .NET is exponential-only in shape; emulate fixed via factor=1.0. Add a `FixedDelay` factory; no `interval=None` equivalent | no | LOW |
| G26 | `RetryPolicy.max_interval` is Option (None ⇒ uncapped) | PARTIAL | `retries.rs:57`: `Option<Duration>` | `MaxDelay` non-nullable, defaults 5s; no uncapped config (must set `TimeSpan.MaxValue`) | no | LOW |
| G27 | `EntryRetryInfo` (retry_count/retry_loop_duration) exposed to `ctx.Run` closure | PARTIAL | `lib.rs:173-178` public type for backoff-aware run logic | `RunContext`/`IRunContext` expose neither. Depends on G2/G3. Add to RunContext | no | LOW |
| G28 | `sys_complete_awakeable` reject with arbitrary code | PARTIAL | `NonEmptyValue::Failure(TerminalFailure)` carries any code (`lib.rs:191-194`) | `RejectAwakeable(id, reason)` hardcodes 500 (`Operations.cs:888-903`, `ProtobufCodec.cs:546`). Add status-code/TerminalException overload | no | LOW |
| G29 | `sys_complete_signal` (named signal) failure with arbitrary code | PARTIAL | same as G28 | `SendSignalFailure(...,reason)` hardcodes 500 (`DefaultContext.cs:144-150`, `ProtobufCodec.cs:660`). Add code overload | no | LOW |
| G30 | `sys_complete_promise` reject with arbitrary code | PARTIAL | same as G28 | `RejectPromise(name, reason)` hardcodes 500 (`Operations.cs:943-951`, `ProtobufCodec.cs:584`). Add code overload | no | LOW |
| G31 | `sys_state_set` PayloadOptions.unstable_serialization | MISSING | `lib.rs:321` | Subset of G19 on the Set path (`Operations.cs:733`) | YES (low freq) | LOW |
| G32 | Typed `Clear<T>(StateKey<T>)` overload | PARTIAL | `sys_state_clear` takes plain String (`lib.rs:324`) — behavior identical | `IObjectContext.cs:13` has only `Clear(string)`; Get/Set are typed. Add typed overload for symmetry | no | LOW |
| G33 | `invocation_debug_logs` — suppress per-op debug logs during replay | PARTIAL | `mod.rs:204-210`: debug only when `is_processing()` | .NET logs unconditionally every op (replay re-logs). Gate user-facing per-op debug logs on `State==Processing` | no (noise) | LOW |
| G34 | DurableRandom: u64 seed folded to Int32 | PARTIAL | full u64 seed | `DurableRandom.cs:13` folds `seed ^ (seed>>32)` to int. Deterministic per-invocation (replay-safe); cross-SDK values need not match. Quality note only | no | LOW |
| G35 | EndpointManifest input/output `jsonSchema` | MISSING | schema lines 85,123 (optional) | `PayloadDescriptor` (`EndpointManifest.cs:150-163`) emits only contentType/required/setContentTypeIfEmpty. Optionally generate jsonSchema | no | LOW |
| G36 | EndpointManifest service-level `documentation` & `metadata` | MISSING | schema lines 37-39,159-165 (optional) | `ServiceManifest`/`ServiceDefinition` have neither (handler-level present). Add Documentation/Metadata to `ServiceDefinition` | no | LOW |
| G37 | `StartMessage` EntryRetryInfo surface (duplicate of G2/G3 in lifecycle area) | MISSING | — | Consolidated into G1–G3/G27 | YES | (dup) |
| G38 | `VMOptions` configurable toggles (implicit_cancellation, non_determinism_checks) | PARTIAL | `lib.rs:228-262` | Defaults baked in correctly; no host-facing knob. Add a VMOptions surface if configurability is required (covers G13/G19 opt-outs and one-way-cancel enable) | default-safe | LOW |

> Items G37 and the per-area duplicates of "headers", "PayloadOptions", "Failure.metadata",
> "related_command", "retry fields" were collapsed into the canonical rows above.

## 3. Ordered implementation plan (conflict-minimizing batches)

Batches are grouped so that files touched in one batch are largely disjoint from the next,
enabling parallel work. Within a batch, items are ordered by dependency.

### Batch A — Durable retry accounting (HIGH; the only correctness-adjacent cluster)
Files: `Internal/Protocol/ProtobufCodec.cs` (ParseStartMessage), `Internal/StateMachine/ProtocolTypes.cs`,
`Internal/StateMachine/InvocationStateMachine.cs` (Initialize/fields),
`Internal/StateMachine/InvocationStateMachine.Operations.cs` (ExecuteWithRetryAsync),
`RunContext.cs`/`IRunContext.cs`.
1. **G2 + G3** — parse `StartMessage` fields 7/8 into `StartMessageFields`, thread through `Initialize`.
2. **G1** — seed `ExecuteWithRetryAsync` attempt/elapsed from the parsed retry_count/duration on the first post-replay attempt (`infer_entry_retry_info` analogue).
3. **G27** — expose `EntryRetryInfo {RetryCount, RetryLoopDuration}` on `RunContext`.
4. **G16/G24** (optional within batch) — derive `next_retry_delay` from the run's RetryPolicy; preserve original error code on exhaustion.
   *(G15 full server-side-retry model is a larger architectural change; track separately — see §4.)*

### Batch B — Call/Send command options (MEDIUM)
Files: `CallOptions.cs`, `SendOptions.cs`, `Internal/Protocol/ProtobufCodec.cs` (CreateCall/CreateSend),
`Internal/StateMachine/InvocationStateMachine.Operations.cs` (CallPrefix/SendPrefix), `Context.cs`/`DefaultContext.cs`.
1. **G5** — add `Headers` to both option structs; populate command `Headers`.
2. **G8** — empty-idempotency-key validation in CallPrefix/SendPrefix.
3. **G20** — add `Name` to both option structs; set command `Name`.

### Batch C — Attach / GetOutput targets (MEDIUM)
Files: `IContext.cs`, `Context.cs`/`DefaultContext.cs`,
`Internal/Protocol/ProtobufCodec.cs` (CreateAttachInvocation/CreateGetInvocationOutput),
`Internal/StateMachine/InvocationStateMachine.Operations.cs`.
1. **G6** — Attach by WorkflowId / IdempotencyId.
2. **G7** — GetOutput by WorkflowId / IdempotencyId (mirror shape).
3. **G4** — expose `get_call_invocation_id`: richer call handle / CallFuture (touches Operations.cs CallAsync + a new handle type).

### Batch D — Diagnostics & error fidelity (MEDIUM/LOW)
Files: `Internal/StateMachine/ProtocolException.cs`, `InvocationHandler.cs`,
`Internal/Protocol/ProtobufCodec.cs` (CreateErrorMessage),
`Internal/StateMachine/InvocationStateMachine.{cs,Protocol.cs,Operations.cs}`.
1. **G9** — ProtocolException code (570/571) through FailAsync.
2. **G21** — ErrorMessage.stacktrace.
3. **G22** — related_command_index/name/type (notify_error analogue).
4. **G33** — gate per-op debug logs on `State==Processing`.

### Batch E — V6 Failure metadata (MEDIUM)
Files: `TerminalException.cs`, `Internal/Protocol/ProtobufCodec.cs` (all CreateFailure/parse paths),
`Internal/StateMachine/InvocationStateMachine.Protocol.cs` (signal/completion parse).
1. **G11 + G23** — add `Metadata` to `TerminalException`; round-trip `Failure.metadata` on all emit/parse paths; gate emission on negotiated V6 (depends on G12 for the gate).

### Batch F — Protocol-version negotiation (MEDIUM)
Files: `Hosting/RestateEndpointRouteBuilderExtensions.cs`, `Internal/StateMachine/InvocationStateMachine.cs`.
1. **G12** — parse `/invoke` content-type, reject <V5/>V6 with 415 + RT0015, thread `Version` into SM (prerequisite for V6 gating in Batch E).

### Batch G — State key-set parity (MEDIUM)
Files: `Internal/StateMachine/InvocationStateMachine.Operations.cs`.
1. **G18** — stop filtering cleared-marker keys in get_state_keys (verify against runtime expectation first).

### Batch H — Run fan-out & RetryPolicy ergonomics (MEDIUM/LOW)
Files: `IContext.cs`, `Context.cs`/`DefaultContext.cs`,
`Internal/StateMachine/InvocationStateMachine.Operations.cs` (RunFutureAsync), `RetryPolicy.cs`.
1. **G14** — `RunAsync(name, action, RetryPolicy)` detached overload.
2. **G17** — no-policy Run default → Infinite (or explicit `RetryPolicy.Infinite`).
3. **G25/G26** — `FixedDelay` factory; nullable/uncapped `MaxDelay`.

### Batch I — Reject/complete arbitrary code + typed clear + manifest extras (LOW)
Files: `Context.cs`/`DefaultContext.cs`, `IContext.cs`/`ISharedWorkflowContext.cs`/`IObjectContext.cs`,
`Internal/Protocol/ProtobufCodec.cs`, `ServiceDefinition.cs`, `Internal/Discovery/EndpointManifest.cs`,
`DurableRandom.cs`.
1. **G28/G29/G30** — status-code/TerminalException overloads on Reject/SendSignalFailure/RejectPromise.
2. **G32** — `Clear<T>(StateKey<T>)`.
3. **G35/G36** — manifest jsonSchema + service documentation/metadata.
4. **G34** — wider seed mapping (quality only).

### Batch J — VMOptions / PayloadOptions surface (MEDIUM/LOW; do last)
Files: a new `VMOptions`/`PayloadOptions` type + threading through hosting + Operations + ProtobufCodec.
1. **G13** — add strict replay payload-equality checks (default on) with an opt-out.
2. **G19/G31/G38** — per-op `unstable_serialization`; configurable implicit_cancellation/non_determinism_checks.
   *(Large, cross-cutting — keep isolated so it does not block Batches A–I.)*

## 4. Intentional / acceptable divergences (NOT gaps)

These are correct-by-design and must not be "fixed" into a regression:

- **Client ingress (`RestateClient`).** Restate's ingress HTTP API has no shared-core module
  (`grep` of `vm/` for ingress is empty). The .NET `RestateClient` (Call/Send/Attach/GetOutput/
  Cancel + idempotency-key/delay) is correctly an SDK-side client, not a VM op. NA.
- **Combinators (All/Any/Race/AllSettled/WaitAll).** Shared-core exposes only `do_progress` over
  handle vecs; there is no all/any/race VM primitive. The .NET combinators (`Context.cs:195-324`)
  are the correct SDK-layer mapping. HAVE.
- **`ctx.Now` as a named Run.** There is no `sys_now`/`sys_time` syscall in shared-core; durable
  time via a named Run (`DefaultContext.cs:29-31`, `__restate_now`) matches the cross-SDK
  convention. HAVE.
- **`do_progress` / `take_notification` / `is_completed`.** Collapsed into the managed
  `AwaitNotificationAsync` await loop (`InvocationStateMachine.cs:386-416`) — a faithful
  async/await expression of the sans-IO loop, not a literal trait surface. HAVE.
- **sans-IO inversion (`notify_input`/`notify_input_closed`).** The SM owns the reader and pumps
  (`Protocol.cs:98-139`); observably equivalent to the sans-IO model. HAVE.
- **Response head produced by ASP.NET, not the SM** (`RestateEndpointRouteBuilderExtensions.cs`).
  Correct boundary for a managed host. HAVE.
- **`sys_send` children NOT tracked for cancel.** Matches the `cancel_children_one_way_calls=false`
  default (`Operations.cs:540-543`). HAVE (default behavior).
- **CANCEL → terminal 409.** Shared-core defines no cancel code; the SDK convention is 409
  (`InvocationStateMachine.cs:79`). HAVE.
- **`now_since_unix_epoch` sleep debug param omitted.** Documented "debugging purposes" only
  (`lib.rs:328`), no proto field on `SleepCommandMessage`. Wire-equivalent. HAVE.
- **`CommandAck`/`EntryAck` is a no-op.** Run durability is signalled by `RunCompletion`, not the
  ack, in V5+. Ignoring it matches shared-core. HAVE.
- **DurableRandom not cross-SDK-identical.** Each SDK uses its own PRNG; values need not match
  across SDKs. Replay-safe within .NET. Acceptable (see G34 for the quality note only).
- **`last_command_index`/`is_replaying`/`is_processing` not on the public context.** Used
  internally and correctly; most SDKs keep them internal too. NA for the user surface.
- **Documented limitation — unresolved-child-skip (G10).** The terminal-cancel path
  deterministically skips children whose invocation-id has not yet been delivered
  (`Operations.cs:1140-1148`), because suspending-to-resolve is impossible on the unwinding path.
  This is a *documented, deterministic, replay-safe* scope limit, not a defect; closing it (a
  suspend-to-resolve mechanic) is tracked as MEDIUM but is explicitly an accepted limitation
  today. Listed in the gap table for completeness; treat as a known divergence.
