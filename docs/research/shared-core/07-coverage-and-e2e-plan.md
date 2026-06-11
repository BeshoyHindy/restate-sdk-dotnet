# 07 — Post-Fix Coverage + End-to-End Example Plan

Status: AUTHORITATIVE implementation spec for the test-coverage and E2E-example program that
follows the core rewrite. The executor implements this verbatim; no design decisions remain
open.

Ground rules:

- The POST-FIX behavior and API shape are defined by
  `docs/research/shared-core/05-managed-fix-blueprint.md` (the blueprint). The source under
  `src/Restate.Sdk/Internal` is being rewritten concurrently to match it — never design against
  the in-flux source; design against the blueprint (lazy `InvocationHandle`, await-driven
  suspension via `AwaitNotificationAsync`/`TrySuspendAsync`, completion ids from a dedicated
  counter starting at 1, signal ids from 17, `ReplayCommand` queue, `_commandLock`/`_flushGate`).
- The bug catalog (B1–B10) is `docs/research/shared-core/04-managed-bug-verification.json`.
- Blueprint §4 (Phase 3 lanes 3a–3e) already specifies a large protocol-level test suite. This
  plan EXTENDS it — every file added here is disjoint from the §4 file set, and every scenario
  here either (a) covers a branch §4 does not, or (b) adds a MECHANISM §4 does not have: a real
  restate-server, or a record-then-replay round trip that feeds VERBATIM command frames recorded
  from a real first attempt back through a fresh `InvocationHandler`. (Note: blueprint §4.2.1/§4.9
  already drive `InvocationHandler.HandleAsync` with source-generated invokers — the handler
  layer itself is NOT the novelty; the recorded-bytes loop is.)
- Nothing in this plan edits `src/Restate.Sdk/Internal/*` or any blueprint §2 file. Test files,
  sample projects, `eng/`, `.config/`, `Directory.Packages.props` (additive package lines only)
  and `.github/workflows/` are the entire write surface.

---

## 1. COVERAGE

### 1.1 Tooling and exact commands

Packages/tools to add (all in Phase 4a, one commit):

1. `Directory.Packages.props` — add (keep alphabetical grouping with the existing Test block):

   ```xml
   <!-- Coverage -->
   <PackageVersion Include="coverlet.collector" Version="6.0.4" />
   <!-- E2E (used by test/Restate.Sdk.E2E, added here so the props file is edited once) -->
   <PackageVersion Include="Testcontainers" Version="4.4.0" />
   ```

2. `test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj` — add to the package ItemGroup:

   ```xml
   <PackageReference Include="coverlet.collector">
     <PrivateAssets>all</PrivateAssets>
     <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
   </PackageReference>
   ```

3. New `.config/dotnet-tools.json` (repo has none today):

   ```json
   {
     "version": 1,
     "isRoot": true,
     "tools": {
       "dotnet-reportgenerator-globaltool": {
         "version": "5.4.7",
         "commands": ["reportgenerator"]
       }
     }
   }
   ```

4. New `eng/coverage.runsettings`:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <RunSettings>
     <DataCollectionRunSettings>
       <DataCollectors>
         <DataCollector friendlyName="XPlat Code Coverage">
           <Configuration>
             <Format>cobertura</Format>
             <!-- Coverage target is the SDK core only. -->
             <Include>[Restate.Sdk]*</Include>
             <!-- Generated protobuf (obj/**/Protocol.cs etc.) and §1.3 escape-hatch members only.
                  CompilerGeneratedAttribute is deliberately ABSENT: async state-machine types,
                  lambda display classes (every ctx.Run closure) and local functions are
                  [CompilerGenerated] — listing it would silently strip the async core from the
                  report and fake the §1.1(5) thresholds. Coverlet filters known compiler-injected
                  state-machine branch points natively; no attribute needed.
                  DeterministicReport/UseSourceLink are deliberately ABSENT: deterministic
                  reporting requires ContinuousIntegrationBuild (CI-only per
                  Directory.Build.props) and adds nothing to threshold gating — the gate must
                  run green locally too (GATE 4a). -->
             <ExcludeByAttribute>GeneratedCodeAttribute,ExcludeFromCodeCoverage</ExcludeByAttribute>
           </Configuration>
         </DataCollector>
       </DataCollectors>
     </DataCollectionRunSettings>
   </RunSettings>
   ```

   NOTE on generated protobuf: the `Gen.*` types are emitted into
   `obj/**/dev/restate/service/Protocol.cs` with `[global::System.CodeDom.Compiler.GeneratedCode]`
   on every member, so `ExcludeByAttribute=GeneratedCodeAttribute` removes them. Verify after the
   first run; if any `Restate.Sdk.Internal.Protocol.Generated.*` class still appears in the
   Cobertura output, add `<Exclude>[Restate.Sdk]Restate.Sdk.Internal.Protocol.Generated.*</Exclude>`.

5. New `eng/coverage-thresholds.json` (the gate's config — single source of truth):

   ```json
   {
     "comment": "Per-namespace-prefix thresholds enforced by eng/coverage-gate.ts on the merged Cobertura report. Longest matching prefix wins. Percentages are line / branch. The 98-branch budgets exist SOLELY to absorb genuinely-unreachable compiler-emitted await/finally branch twins (see plan §1.3(3)); any of these namespaces measuring 100 reachable in the post-GATE-3 snapshot is ratcheted to 100 in the Phase 4b completion commit. Thresholds only ever ratchet up.",
     "rules": [
       { "prefix": "Restate.Sdk.Internal.StateMachine", "line": 100, "branch": 98  },
       { "prefix": "Restate.Sdk.Internal.Journal",      "line": 100, "branch": 98  },
       { "prefix": "Restate.Sdk.Internal.Protocol",     "line": 100, "branch": 98  },
       { "prefix": "Restate.Sdk.Internal.DurableFuture","line": 100, "branch": 98  },
       { "prefix": "Restate.Sdk.Internal.InvocationHandler", "line": 100, "branch": 95 },
       { "prefix": "Restate.Sdk.Internal.Context",      "line": 95,  "branch": 90  },
       { "prefix": "Restate.Sdk.Internal",              "line": 95,  "branch": 90  },
       { "prefix": "Restate.Sdk",                       "line": 90,  "branch": 85  }
     ],
     "phaseInOverrides": {
       "comment": "Active until Phase 4b lands (delete this block in the Phase 4b commit).",
       "rules": [
         { "prefix": "Restate.Sdk.Internal.StateMachine", "line": 95, "branch": 90 },
         { "prefix": "Restate.Sdk.Internal.Journal",      "line": 95, "branch": 90 },
         { "prefix": "Restate.Sdk.Internal.Protocol",     "line": 92, "branch": 85 }
       ]
     }
   }
   ```

6. New `eng/coverage-gate.ts` (Deno, no Python per repo policy). Behavior spec:
   - `deno run --allow-read eng/coverage-gate.ts <cobertura.xml> [--phase-in] [--audit-internal <srcDir>]`
   - Parses Cobertura `<class name=...>` entries, aggregates `lines-covered/lines-valid` and
     `branches-covered/branches-valid` per namespace prefix from `eng/coverage-thresholds.json`
     (longest-prefix match per class; classes matching no rule fall to the `Restate.Sdk` rule).
   - Prints a per-rule table (covered/valid/percent vs threshold) and, for every rule below
     threshold, the 10 worst classes with their uncovered line numbers.
   - `--audit-internal <srcDir>` (the anti-gaming check, required by GATE 4a and CI): enumerate
     every top-level `class`/`struct`/`record` declaration in `.cs` files under `<srcDir>`
     (skipping `obj/`, `bin/`), and FAIL unless each type name appears as a `<class>` entry (or
     as the declaring-type prefix of one) in the merged report. This catches any attribute or
     filter change that silently deletes hand-written code from the coverage accounting —
     the failure mode that would make the §1.1(5) thresholds trivially green.
   - Exit 1 if any rule or the audit fails; exit 0 otherwise. `--phase-in` applies
     `phaseInOverrides` on top.
   - Use `jsr:@libs/xml` (or `npm:fast-xml-parser@4`) for parsing; no other deps.

Exact local/CI command sequence (also the CI job body, §2.6) — runs green LOCALLY with no
`CI=true` required (the runsettings deliberately omit DeterministicReport, see §1.1(4)):

```bash
dotnet tool restore
dotnet test test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj -c Release \
  --collect:"XPlat Code Coverage" \
  --settings eng/coverage.runsettings \
  --results-directory artifacts/coverage
dotnet reportgenerator \
  -reports:"artifacts/coverage/**/coverage.cobertura.xml" \
  -targetdir:artifacts/coverage/report \
  -reporttypes:"Html;Cobertura;MarkdownSummaryGithub" \
  -assemblyfilters:"+Restate.Sdk" \
  -classfilters:"-Restate.Sdk.Internal.Protocol.Generated.*"
deno run --allow-read eng/coverage-gate.ts artifacts/coverage/report/Cobertura.xml \
  --audit-internal src/Restate.Sdk/Internal
```

(`reportgenerator` merges the per-run Cobertura files into one; the gate always reads the
MERGED `artifacts/coverage/report/Cobertura.xml`, never the raw per-run files.)

### 1.2 Hard-to-cover paths introduced by the fix — and the test that covers each

The blueprint's Phase 3 suite (§4) already covers most branches. The table below maps each
hard path to its covering test: "§4.x.y" = blueprint test (already being added by the
concurrent workflow — do NOT re-add), "NEW:" = a Phase 4b gap test added by this plan.

| # | Hard path (post-fix code) | Branches that make it hard | Covering test |
|---|---|---|---|
| H1 | Suspension EOF-before-park (`AwaitNotificationAsync` calls `TrySuspendAsync` after registering; pump EOF landed first) | `_inputClosed` already true at park; `tcs.Task.IsCompleted` fast path false | §4.7.7 (sleep/lazy GetState/Call), §4.7.8 (awakeable), §4.7.10 (DurableFuture thunk + send handle) |
| H2 | `TrySuspendAsync` early-outs | `State==Closed` re-entry; `!_inputClosed`; `_executingRuns>0`; all-awaiting-resolved (`HasResultFor` skip) → `Count==0` return | §4.7.4 (race), §4.7.5+§4.7.15 (run guard both orientations), §4.7.9/§4.7.11 (empty awaiting set), §4.7.12 (HasResultFor skip with mixed resolved/unresolved); NEW G1 covers the double-suspend `State==Closed` re-entry from the third trigger site |
| H3 | Completion-manager terminal LATCH (`_terminal` arm in `GetOrRegister`/`TryComplete`/`TryFail`/`TryClaimForExecution`; pre-observed faulted TCS) | only reachable after `FailAll`/`CancelAll`, then a straggler call | §4.8.4 (blueprint explicitly specifies FailAll/CancelAll/HasResultFor/TryClaimForExecution latch coverage), §4.8.7 (fault observation); NEW G2 is CONDITIONAL — only the latch arms §4.8.4's enumeration leaves implicit (`TryFail`-after-latch return value, `TryClaimForExecution`-after-latch), authored only if the post-GATE-3 snapshot shows them uncovered |
| H4 | Replay type/name mismatch (`InvocationJournal.DequeueReplay` throws) + target-triple overload | compound `\|\|` in the triple comparison needs each operand exercised | §4.1.12 (type+name), §4.1.17 (Call + OneWayCall target), §4.1.9 (journal-level empty-queue throw); NEW G3 (per-operand triple matrix ONLY: service-only, handler-only, key-only mismatch — the OneWayCall variant and empty-queue throw stay owned by §4.1.17/§4.1.9, not duplicated) |
| H5 | `KNOWN_ENTRIES_IS_ZERO` (`Initialize` throws `ProtocolException`) | one-shot guard | §4.1.7, §4.9 (`InvocationStateMachineTests` update) |
| H6 | Partial-state lazy fallthrough (`GetStateAsync` 5-way branch: replay-eager / replay-lazy / eager hit / complete-absent / partial-absent→lazy; `DeserializeStateValue` Void vs empty-Value) | the silent pre-fix path returned `default`; post-fix the `else` (lazy) arm must be reached with a non-null `_eagerState` | §4.6.1 (partial fallthrough), §4.6.2 (Value+Void), §4.6.3 (cleared marker), §4.6.4 (clear_all flips is_partial), §4.6.6 (replay-eager decode), §4.6.10 (empty-value normalization) |
| H7 | Run frontier inline-execute (Template D case 2: `!stillReplaying && TryClaimForExecution`) and case 3 fail-fast; `ValidateReplayCompletionId` | atomic claim vs racing notification; frontier detection | §4.1.3 (case 2), §4.1.15 (case 3), §4.4.6 (claim race, 1k iters); NEW G4 (`ValidateReplayCompletionId`: journaled id `0` → "corrupt journal"; journaled id ≠ allocated → "non-deterministic replay" — neither is in §4) |
| H8 | Pump-death exhaustive unwind (`ProcessIncomingMessagesAsync` catch-`Exception` FailAll; command-after-preflight guard) | requires a fault injected mid-stream | §4.2.6 (both variants) |
| H9 | Detached Run infrastructure-failure containment (RunFuture's wrapper `TryFail(completionId, 500, ...)` when `ExecuteAndProposeRunAsync` dies of a non-Terminal write failure) | needs a pipe that faults on flush after the RunCommand prefix | NEW G5 |
| H10 | Terminal-op in-lock re-checks (`CompleteAsync`/`FailTerminalAsync`/`FailAsync`: raced-normal-close silent return; suspended → `SuspendedException`; replay Output dequeue; "commands after Output") | races with suspension | §4.7.13 (suspension-vs-failure exclusivity), §4.1.19 (commands after Output); NEW G6 (`CompleteAsync` AFTER normal close returns silently; `FailAsync` after suspension swallowed by the handler's bare catch — asserts no Error frame) |
| H11 | `EnsureActive`/`ThrowIfClosedLocked` suspended arm reached from a fan-out closure finishing concurrently with suspension | timing window | §4.7.13/§4.7.15 hit it probabilistically; NEW G7 makes it deterministic (suspend the SM, then call `SetState` → `SuspendedException`, and `CompleteAsync` → `SuspendedException`) |
| H12 | `ParseReplayCommand` default arm ("Unknown replayed command type") | needs a command-flagged frame with a bogus type id | NEW G8 (synthetic header with an unused type value in the command range inside the known-entries batch → `ProtocolException`, surfaced via StartAsync) |
| H13 | `ProtocolReader` partial-frame resume + examined-position (post-B3 single-reader; blueprint open question on hot-spin) | needs byte-dribbling writes | NEW G9 (drip-feed a frame 1 byte per flush through the Pipe; assert exactly one message decoded, bounded loop iterations via a counting PipeReader decorator — reuse §4.2.4's decorator) |
| H14 | `AwaitNotificationAsync` replay-mutation guard + completed-fast-path | both arms of `IsReplaying && !tcs.Task.IsCompleted` | §4.1.14 (guard throws), §4.1.18 (buffered early completion → fast path) |
| H15 | `GetStateKeysAsync` eager/sorted/lazy triple + `GetEagerStateKeysCommand` replay decode | sorted-order determinism | §4.6.8, §4.6.9; NEW G10 (lazy keys path under partial state — §4.6 only exercises eager keys) |
| H16 | Lazy `InvocationHandle` thunk lifecycle | thunk-not-invoked-at-construction; post-dispose `TaskCanceledException`; failure → `TerminalException` | §4.5.5–§4.5.8 |

Phase 4b NEW gap-test files (all under `test/Restate.Sdk.Tests/`, names chosen to be disjoint
from every §4 lane file):

- `StateMachine/ReplayEdgeTests.cs` — G3, G4, G8 (drives `InvocationStateMachine` over Pipe
  pairs exactly like §4.1, using the shared `Testing/ProtocolTestHarness.cs`).
- `StateMachine/TerminalOpsEdgeTests.cs` — G1, G6, G7, G11 (G11: `FailTerminalAsync` replay
  branch — journaled OutputCommand with failure — and the raced-close silent return of both
  `Fail*` methods).
- `StateMachine/RunDetachedFailureTests.cs` — G5 (wrap the outbound `PipeWriter` in a decorator
  whose `FlushAsync` throws `IOException` after N successful flushes; assert the
  `LazyRunFuture` resolves to a faulted completion with code 500 and nothing goes unobserved —
  subscribe `TaskScheduler.UnobservedTaskException` for the test duration like §4.8.7).
- `Journal/CompletionManagerLatchTests.cs` — G2 (CONDITIONAL file: blueprint §4.8.4 already
  specifies FailAll/CancelAll/HasResultFor/TryClaimForExecution latch coverage; this file is
  authored only if the post-GATE-3 snapshot shows the residual arms uncovered, and then covers
  ONLY those arms: `TryFail` after the latch (return value, no double-fault) and
  `TryClaimForExecution` after the latch returning `false` — extend-don't-duplicate).
- `Protocol/ProtocolReaderEdgeTests.cs` — G9 plus: zero-length payload frames, header split
  across reads, EOF mid-payload → `ProtocolException`, cancellation token honored mid-read.
- `Protocol/ProtocolWriterEdgeTests.cs` — `WriteHeaderOnly`, large payload spanning segments,
  flags round-trip (gets `MessageHeader`/`MessageFlags` to 100%).
- `Protocol/ProtobufCodecEdgeTests.cs` — round-trip every `Create*` factory the §4 suites do
  not hit (e.g. `CreateSuspensionMessage` empty/both-lists, `CreateGetEagerStateKeysCommand`,
  `CreateCancelInvocationCommand` uses the shared `CancelSignalId` constant), and
  `ParseReplayCommand` over every `MessageType` case arm (table-driven `[Theory]` — one
  member-data row per case in blueprint 2.3(b), asserting `EntryType`, `Name`, ids, eager
  fields).
- `Context/ContextSurfaceTests.cs` — drives `DefaultContext`/`DefaultObjectContext`/
  `DefaultWorkflowContext`/shared variants and `DurableRandom`/`DurableConsole`/`Awakeable`
  wrappers over a live in-memory SM (fresh invocation, `known_entries=1`) so the
  `Internal.Context` rule (95/90) is met without touching MockContext-based tests.

Method: after GATE 3 (blueprint Phase 3 complete), run the §1.1 command sequence ONCE, snapshot
`artifacts/coverage/report` and write the residual uncovered-line list into the Phase 4b task
before authoring — the G-tests above are the predicted gaps; the snapshot either confirms them
or shrinks the list. Tests that would duplicate a §4 case that already covers the line are NOT
added (extend, don't duplicate).

### 1.3 Legitimately-uncoverable lines and the exclusion policy

Position: the core (`Internal/StateMachine`, `Internal/Journal`, `Internal/Protocol` hand-written,
`DurableFuture`, `InvocationHandler`) contains ZERO `[ExcludeFromCodeCoverage]` members. Every
defensive throw the blueprint specifies is reachable from a test seam:

- `InvocationJournal.DequeueReplay` empty-queue throw — unreachable through the integrated SM
  (the `IsReplaying` flip closes the door) but unit-coverable directly on `InvocationJournal`
  (§4.1.9 does exactly this). Covered, not excluded.
- `ParseReplayCommand` default arm — coverable with a synthetic bogus command type (G8).
- `ValidateReplayCompletionId` zero-id arm — coverable with a crafted journal (G4).
- Pump catch-`Exception` — coverable with a faulted reader (§4.2.6).

Attribute-based exclusions (in the runsettings, not in source):

1. Generated protobuf (`GeneratedCodeAttribute`) — not our code.
2. `Internal/Log.cs` `[LoggerMessage]` partial-method bodies — source-generated
   (`GeneratedCodeAttribute` on the generated half); the hand-written declarations have no body
   to cover.
3. Compiler-generated state-machine `MoveNext` branches that the C# compiler emits for
   `await`-in-`finally` etc. are NOT excluded — they count (consistent with §1.1(4): the
   runsettings deliberately omit `CompilerGeneratedAttribute`, because async state machines,
   lambda display classes and local functions all carry it and listing it would strip the async
   core from the accounting), and the watchdog-driven tests reach them. If a specific
   compiler-emitted branch proves genuinely unreachable (e.g. the `ConfigureAwait`
   completed-synchronously twin), it is accepted as the gap inside the 98% BRANCH budgets of
   `Internal.StateMachine`/`Internal.Journal`/`Internal.Protocol`/`Internal.DurableFuture`
   (§1.1(5)) — NEVER attribute-excluded (an attribute cannot exclude a single compiler branch,
   only whole members). Each accepted gap is recorded (file + branch site + why unreachable) in
   the same "Coverage exclusions" appendix the escape hatch below uses, so the 98 budgets stay
   auditable and can be ratcheted to 100 when the snapshot shows full reachability.

Escape hatch (expected to stay unused): if implementation reveals a genuinely unreachable line,
the executor may apply `[ExcludeFromCodeCoverage(Justification = "...")]` ONLY when all of the
following hold: (a) the member is a defensive guard whose trigger state is
impossible-by-construction AND not constructible through any internal test seam; (b) the
justification names the invariant making it unreachable; (c) the member is listed in a new
"Coverage exclusions" appendix at the bottom of THIS file in the same commit. Whole-class or
whole-file exclusions are forbidden in the core namespaces.

Out-of-core code measured at lower thresholds (not excluded, just gated softer by §1.1 rules):
`Hosting/*` (exercised via `Microsoft.AspNetCore.TestHost` in existing Endpoint tests + E2E),
`Client/RestateClient.cs` (exercised by the §2 E2E suite, which does not feed the unit-coverage
report), `Internal/Discovery/EndpointManifest.cs`, attribute/option POCOs.

### 1.4 Coverage gate placement

- PR gate: the `coverage` job in `ci.yml` (§2.6) runs the §1.1 sequence; the Deno gate fails the
  job on any rule violation. During Phase 4a–4b the job passes `--phase-in`; the Phase 4b
  completion commit deletes `phaseInOverrides` and the flag (ratchet, never loosen).
- The existing `Build & Test` job stays as-is (fast signal without collectors).
- The `MarkdownSummaryGithub` report is appended to `$GITHUB_STEP_SUMMARY`; the HTML report is
  uploaded as the `coverage-report` artifact.

---

## 2. E2E EXAMPLES — replaying the faulty paths

### 2.1 Harness decision

BOTH harnesses, layered; they are gates for different claims:

1. REAL `restate-server` via Testcontainers (.NET) — REQUIRED, the merge gate. Genuine
   suspension (server closes input on inactivity), genuine resume (server re-sends the journal
   with real `known_entries` counting commands+notifications), genuine awakeable/signal delivery
   through ingress. This is the ONLY way to truly exercise B1/B2/B3/B8 end to end: the server —
   not our test code — decides batch composition, notification ordering, EOF timing, and retry
   cadence. It also pins protocol compatibility against a real runtime version.
2. IN-PROCESS journal-replay harness — deterministic, docker-free PR-time fallback. Its value
   is the RECORD-THEN-REPLAY round trip of REAL recorded frames, not the layer: blueprint
   §4.2.1/§4.9 already drive `InvocationHandler.HandleAsync` through `ServiceDefinitionRegistry`
   (so does the existing `Integration/ProtocolIntegrationTests.cs`), but those scenarios
   hand-craft their journals. Here, attempt 1 of a REAL handler runs over `Pipe` pairs,
   suspension is forced by completing the request pipe, the emitted command frames are RECORDED,
   and attempt 2's known-entries batch is composed from those VERBATIM recorded command bytes
   plus notifications whose ids are PARSED out of the recorded bytes — then fed through a fresh
   `InvocationHandler`. Exercises B1 (both the Run and the two-id Call shapes), B2, B3, B5, B6
   replay decode, B7, B10b and the workflow-promise replay sites with exact frame control —
   including shapes a healthy server rarely produces (partial_state=true, adversarial
   notification orderings). It cannot prove server interop; the container suite cannot produce
   deterministic adversarial frames. Hence both.

"Example fails ⇒ SDK broken" is enforced by construction: every scenario asserts BOTH the
durable post-condition AND that the faulty path actually ran (attempt counter ≥ 2 recorded by a
probe — a scenario that never suspended/replayed FAILS, so a future regression cannot silently
skate through an unexercised path).

### 2.2 Project layout

```
samples/ReplayLab/                      # one sample project, all replay-bait services
  ReplayLab.csproj                      # Exe, same shape as samples/Counter (+ Container.props import)
  Program.cs                            # RestateHost.CreateBuilder() ... .WithPort(9090)
  Probes.cs                             # ExecutionProbe (static ConcurrentDictionary) + ProbeService
  RunSleepRunService.cs                 # B1/B2/B3/B8
  AwakeablePairService.cs               # B4/B8/B9
  FanOutRunsService.cs                  # B5
  PartialStateCounterObject.cs          # B7 (VirtualObject)
  SagaCompensationService.cs            # B10b (+B1 failure direction)
  LazySendService.cs                    # B6 (+ slow target service SlowEchoService)
  CallAcrossSuspensionService.cs        # B1 two-id Call replay across suspension (E7/P8)
  ApprovalWorkflow.cs                   # [Workflow] promise replay sites, blueprint §4.10 (E8/P9)
test/Restate.Sdk.E2E/                   # NEW xunit project — Testcontainers suite
  Restate.Sdk.E2E.csproj
  RestateContainerFixture.cs            # container lifecycle + deployment registration
  DockerFactAttribute.cs                # skip when docker unavailable
  IngressClient.cs                      # thin HttpClient wrapper (invoke, awakeable resolve, attach)
  ReplayLabE2eTests.cs                  # the scenarios of §2.4
test/Restate.Sdk.Tests/Integration/
  RecordedJournal.cs                    # in-process record/replay helper (§2.5)
  JournalReplayDriverTests.cs           # in-process scenarios of §2.5
```

`samples/ReplayLab/ReplayLab.csproj` mirrors `samples/Counter/Counter.csproj` exactly
(ProjectReference to `Restate.Sdk` + generator as Analyzer, `Container.props` import,
`OutputType=Exe`, `IsPackable=false`).

`test/Restate.Sdk.E2E/Restate.Sdk.E2E.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <NoWarn>$(NoWarn);RESTATE003;RESTATE008</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App"/>
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk"/>
    <PackageReference Include="xunit"/>
    <PackageReference Include="xunit.runner.visualstudio" PrivateAssets="all"
                      IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive"/>
    <PackageReference Include="Testcontainers"/>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Restate.Sdk/Restate.Sdk.csproj"/>
    <ProjectReference Include="../../samples/ReplayLab/ReplayLab.csproj"/>
    <ProjectReference Include="../../src/Restate.Sdk.Generators/Restate.Sdk.Generators.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
  </ItemGroup>
</Project>
```

Plus `xunit.runner.json` (`"parallelizeTestCollections": false` — one container, one in-proc
endpoint, sequential scenarios) copied to output. Add the new project to `Restate.Sdk.slnx`.

The E2E project hosts the ReplayLab services IN-PROCESS (it project-references ReplayLab and
builds the same `RestateHost` app bound to `127.0.0.1:0`); the container reaches it via
`host.docker.internal`. This gives tests direct read access to `ExecutionProbe` while the
standalone `samples/ReplayLab` exe remains runnable by hand and by `integration-test.sh`.

### 2.3 Container fixture (exact spec)

`RestateContainerFixture` (collection fixture, `IAsyncLifetime`):

```csharp
public const string ImageTag = "docker.io/restatedev/restate:1.4";   // SINGLE constant; see §2.7 verify step

_container = new ContainerBuilder()
    .WithImage(ImageTag)
    .WithPortBinding(8080, true)      // ingress
    .WithPortBinding(9070, true)      // admin
    .WithEnvironment("RESTATE_WORKER__INVOKER__INACTIVITY_TIMEOUT", "5s")
    .WithEnvironment("RESTATE_WORKER__INVOKER__ABORT_TIMEOUT", "30s")
    .WithExtraHost("host.docker.internal", "host-gateway")
    .WithWaitStrategy(Wait.ForUnixContainer()
        .UntilHttpRequestIsSucceeded(r => r.ForPort(9070).ForPath("/health")))
    .Build();
```

Startup sequence in `InitializeAsync`:

1. Start the in-process ReplayLab endpoint: build the same WebApplication ReplayLab's
   `Program.cs` builds (factor ReplayLab's builder into a public
   `ReplayLabHost.Build(int port)` helper in the sample so both entry points share it), bound
   to `http://0.0.0.0:0`; read the bound port from `IServerAddressesFeature`.
2. Start the container.
3. Register the deployment: `POST http://localhost:{Mapped(9070)}/deployments` with body
   `{"uri": "http://host.docker.internal:{port}"}`; poll until 200/201; fail fast with the
   response body otherwise.
4. Record `IngressBase = http://localhost:{Mapped(8080)}`.

`DockerFactAttribute : FactAttribute` — constructor sets
`Skip = "Docker is not available"` unless `File.Exists("/var/run/docker.sock")` or
`Environment.GetEnvironmentVariable("DOCKER_HOST") is not null`. Local devs without docker skip;
CI always runs (the workflow treats a skipped E2E suite as failure via the final `grep` on the
trx for `NotExecuted` outcomes — see the §2.6(b) step; hangs are caught by the job
`timeout-minutes` plus the per-test xunit timeouts of §2.4).

The 5s inactivity timeout is the suspension forcer: any handler parked ≥5s with no traffic gets
its input closed by the server → post-fix SDK emits `SuspensionMessage` → server resumes when
the awaited notification materializes → genuine replay. (Pre-fix SDK: B8 means the stream hangs
until abort; B2 means the resume hangs in Replaying; either way the scenario times out and the
test FAILS — which is the point.)

`IngressClient` wraps one `HttpClient`:
- `InvokeAsync(service, handler, object? body, string? idempotencyKey = null, string? key = null)`
  → `POST {IngressBase}/{service}[/{key}]/{handler}` (JSON body when non-null;
  `idempotency-key` header when given); returns deserialized JSON + elapsed time.
- `ResolveAwakeableAsync(string awakeableId, object payload)` →
  `POST {IngressBase}/restate/awakeables/{awakeableId}/resolve`.
- `RejectAwakeableAsync(string awakeableId, string reason)` → `.../reject`.
- All calls `EnsureSuccessStatusCode` with response body in the failure message; default
  per-call timeout 120s (server abort/retry cadence dominates).

### 2.4 The example services and scenarios (testcontainer suite)

Probe contract (`samples/ReplayLab/Probes.cs`): `ExecutionProbe` is a
`static ConcurrentDictionary<string, ConcurrentDictionary<string, int>>` keyed by a
caller-supplied `probeId`, with `Increment(probeId, counter)` and `Snapshot(probeId)`. Every
handler below takes `probeId` in its request and calls `Increment(probeId, "attempt")` as its
FIRST statement (replay re-executes handler code, so attempts count re-invocations); every Run
closure increments `"run:{name}"`. A `[Service] ProbeService.Get(probeId)` handler exposes
snapshots through ingress so the standalone sample is probe-readable too; the E2E tests read
the static directly (same process). EVERY scenario asserts `attempt >= 2` — proof the
suspension+replay path genuinely ran. PRECEDENCE RULE: if any code snippet below conflicts with
this probe contract (e.g. a Run closure shown without its `run:{name}` increment), the probe
contract wins — every Run closure increments `run:{name}` exactly where it executes.

E1 — `RunSleepRunService.Execute(Context, ProbeRequest) -> string` (B1, B2, B3, B8):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
var a = await ctx.RunAsync("a", () => { ExecutionProbe.Increment(req.ProbeId, "run:a");
    return Task.FromResult(Guid.NewGuid().ToString("N")); });
await ctx.Sleep(TimeSpan.FromSeconds(8));            // > 5s inactivity → genuine suspension
var b = await ctx.RunAsync("b", () => { ExecutionProbe.Increment(req.ProbeId, "run:b");
    return Task.FromResult(Guid.NewGuid().ToString("N")); });
return $"{a}|{b}";
```

Forcing: the 8s sleep with 5s inactivity guarantees suspend→resume; the resume batch contains
`RunCommand{a}` + `RunCompletionNotification{a}` + `SleepCommand` + `SleepCompletionNotification`
— exactly the shape that pre-fix (a) JSON-deserialized protobuf command bytes (B1), (b) hung in
Replaying because notifications inflate known_entries (B2), (c) double-read the pipe (B3), and
(d) could never suspend in the first place (B8).
Post-conditions: ingress returns `200` within 60s; response matches `^[0-9a-f]{32}\|[0-9a-f]{32}$`;
probe: `attempt >= 2`, `run:a == 1`, `run:b == 1` (run "a" executed exactly once although the
handler ran twice — THE durable-result-survived-replay assertion).

E2 — `AwakeablePairService.AwaitTwo(Context, ProbeRequest) -> string` (B4, B8, B9):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
var ak1 = ctx.Awakeable<string>();                    // Awakeable<T> { Id, Value } per src/Restate.Sdk/Awakeable.cs
var ak2 = ctx.Awakeable<string>();
await ctx.RunAsync("publish", () => { AwakeableMailbox.Publish(req.ProbeId, ak1.Id, ak2.Id); return Task.FromResult(true); });
var second = await ak2.Value;                         // awaited FIRST — signal id 18
var first  = await ak1.Value;                         // then 17
return $"{first}+{second}";
```

(`AwakeableMailbox` is a static probe-keyed mailbox in `Probes.cs`; the test polls it for the
ids.) Driving: invoke without awaiting completion (fire the ingress call on a background task or
use the `/send` ingress form and attach later); read ids from the mailbox; WAIT 8s (forces
suspension parked on signals — pre-fix B8 hang); `ResolveAwakeableAsync(id2, "two")` — pre-fix
B4 this id encodes signal index 1 == CANCEL, so resolving it cancels/corrupts the invocation;
wait 8s again (second suspension, exercises resume with a signal notification in the batch —
B9's replayed-awakeable race window); `ResolveAwakeableAsync(id1, "one")`.
Post-conditions: invocation completes with `"one+two"`; `attempt >= 3`; decoding the trailing
BE32 of the base64url part of `id1`/`id2` yields 17 and 18 (user-visible B4 proof at the
ingress boundary).

E3 — `FanOutRunsService.Scatter(Context, ProbeRequest) -> int[]` (B5):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
var futures = Enumerable.Range(0, 16).Select(i =>
    ctx.RunAsync($"part-{i}", async () => { await Task.Delay(Random.Shared.Next(5, 120));
        ExecutionProbe.Increment(req.ProbeId, $"run:part-{i}"); return i; })).ToArray();
// Await in creation order (futures resolve from notifications; completion order is jittered).
var parts = new int[futures.Length];
for (var i = 0; i < futures.Length; i++) parts[i] = await futures[i].GetResult();
await ctx.Sleep(TimeSpan.FromSeconds(8));             // force suspension AFTER fan-out journaled
return parts;
```

Forcing: jittered delays make completion order ≠ creation order on attempt 1 (pre-fix B5
journals Runs in completion order → replay cross-wires or JsonExceptions); the sleep forces a
resume that replays all 16 RunCommands + notifications.
Post-conditions: response is exactly `[0,1,...,15]` (no cross-wiring); each `run:part-i == 1`;
`attempt >= 2`.

E4 — `PartialStateCounterObject` (VirtualObject, B7):

```csharp
[Handler] public async Task<string> Mutate(IObjectContext ctx, ProbeRequest req) {
    ExecutionProbe.Increment(req.ProbeId, "attempt");
    ctx.Set(StateKeys.A, $"a-{req.ProbeId}");
    await ctx.Sleep(TimeSpan.FromSeconds(8));         // suspension between Set and Gets
    var a = await ctx.Get(StateKeys.A);               // must come from eager cache rebuilt on replay
    var b = await ctx.Get(StateKeys.B);               // never written → must NOT silently default-without-command
    return $"{a}|{b ?? "<null>"}";
}
```

Honest scope (B7 caveat): against a real server the resume arrives with COMPLETE eager state —
key A is already in the eager map — so `Get(A)` succeeds even on the pre-fix cache-skip path,
and `Get(B)`'s default is legitimate (the lazy fallthrough is never reached). E4's container
discriminating power is therefore B2/B8 (pre-fix the resume HANGS in Replaying / never suspends
→ timeout → fail) plus state continuity across a genuine suspend/resume; the GENUINE B7 paths
(replay-SetState cache rebuild proven by command-emission, partial-state lazy fallthrough) are
owned by in-process P4, which asserts the exact attempt-2 command set. If §2.7(c) finds an
eager-state disable/limit knob on the pinned image, add a container variant that ALSO asserts —
via admin-API journal introspection, not just the response value — that a `GetLazyStateCommand`
for the unwritten key B actually appears in the journal (the response value alone cannot prove
the lazy path ran). If no knob exists the variant MUST NOT exist (no silent skip), and
real-server `partial_state=true` is recorded as a residual gap in §3.1.
Post-conditions: response `a-{probeId}|<null>`; `attempt >= 2`; a follow-up `Mutate` call with a
new probeId returns its own value (object state intact).

E5 — `SagaCompensationService.Book(Context, ProbeRequest) -> string` (B10b + B1 failure
direction):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
try {
    await ctx.Run("book", () => {                     // void Run overload, NO RetryPolicy —
        ExecutionProbe.Increment(req.ProbeId, "run:book");   // TerminalException is never retried
        throw new TerminalException("no rooms", 500);
    });
    return "booked";
} catch (TerminalException) {
    // FIRST statement of the catch — the discriminator. The 8s sleep (> 5s inactivity) forces
    // a genuine suspension AFTER RunCommand{book} + RunFailureNotification (+ SleepCommand)
    // are journaled, so the resume batch REPLAYS the failed Run.
    await ctx.Sleep(TimeSpan.FromSeconds(8));
    await ctx.RunAsync("compensate", () => { ExecutionProbe.Increment(req.ProbeId, "run:compensate");
        return Task.FromResult(true); });
    return "compensated";
}
```

Forcing: attempt 1 executes the `book` closure exactly once, the server acks the
`ProposeRunCompletion` failure, the catch is entered, and the post-catch sleep suspends the
invocation. Attempt 2's server-composed batch replays `RunCommand{book}` +
`RunFailureNotification` + `SleepCommand` + `SleepCompletionNotification` — attempt 2 only
reaches `"compensated"` if the REPLAYED failed Run re-raises `TerminalException` from the
journaled failure: the exact B10b path. Pre-fix this scenario FAILS in every direction: the
replayed Run never re-raised (empty-success journal entry → attempt 2 returns `"booked"` or
JsonException retry-loops), and even if the catch were reached without replay, `attempt >= 2`
cannot hold without a resume (pre-fix B8 also means the sleep never suspends → timeout).
Post-conditions: response `"compensated"`; `attempt >= 2` (the failed-Run replay genuinely
ran); `run:book == 1` (the closure is NOT re-executed on replay — exactly-once despite two
handler attempts); `run:compensate == 1`. (Deterministic frame-level proof of the failure
re-raise stays with §4.7.14 and in-process P5; E5 proves it against a real server's batch.)

E6 — `LazySendService.SendAndReport(Context, ProbeRequest) -> string` (B6) +
`SlowEchoService.Echo` (sleeps 6s, returns input):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
var handle = await ctx.Send<SlowEchoRequest>("SlowEchoService", "Echo", new(req.ProbeId));
await ctx.Sleep(TimeSpan.FromSeconds(8));             // suspend; resume replays the OneWayCallCommand
var id = await handle.GetInvocationIdAsync();         // post-fix: from replayed CallInvocationIdCompletionNotification
return id;
```

Post-conditions: response matches `^inv_[a-zA-Z0-9]` (a structurally valid invocation id — NOT
protobuf garbage, the pre-fix B6 replay symptom); `attempt >= 2`; the send-side latency of the
ingress call's first response chunk is NOT asserted (flakiness) — fire-and-forget timing is
owned by §4.5.1/§4.5.3.

E7 — `CallAcrossSuspensionService.Relay(Context, ProbeRequest) -> string` (B1's two-id Call
model, B2, B3, B8 — the single most common user-facing replay shape):

```csharp
ExecutionProbe.Increment(req.ProbeId, "attempt");
var reply = await ctx.Call<string>("SlowEchoService", "Echo", new SlowEchoRequest(req.ProbeId));
return reply;                                         // SlowEcho durably sleeps 6s, then echoes ProbeId
```

Forcing: `SlowEchoService.Echo` (the same target E6 uses) takes 6s > the 5s inactivity timeout,
so the CALLER suspends parked on the call's `result_idx` completion. When Echo completes, the
server-composed resume batch contains ONE replayed `CallCommand{invocation_id_idx, result_idx}`
plus BOTH notifications (`CallInvocationIdCompletionNotification` +
`CallCompletionNotification`) — the exact two-ids-one-wire-command shape whose positional-index
replay diverged pre-fix (B1: command count ≠ completion-id count) and whose
notification-inflated `known_entries` hung pre-fix (B2). Pre-fix the scenario FAILS (hang →
timeout, or garbage reply); post-fix the replayed command's ids are honored and the result
notification resolves the call.
Post-conditions: response equals `req.ProbeId` round-tripped through Echo (well-formed JSON
string, not protobuf bytes); `attempt >= 2`.

E8 — `ApprovalWorkflow` ([Workflow], blueprint §4.10 promise replay sites — 4 of the 26
rewritten replay sites; no other E/P scenario touches a workflow):

```csharp
[Workflow]
public class ApprovalWorkflow {
    [Handler] public async Task<string> Run(IWorkflowContext ctx, ProbeRequest req) {
        ExecutionProbe.Increment(req.ProbeId, "attempt");
        var decision = await ctx.Promise<string>("approval");   // parks > 5s → suspension
        return $"approved:{decision}";
    }
    [SharedHandler] public Task Approve(ISharedWorkflowContext ctx, ProbeRequest req) {
        ctx.ResolvePromise("approval", req.ProbeId);
        return Task.CompletedTask;
    }
}
```

Driving: start `Run` keyed by `probeId` on a background task (workflow run handlers complete
only once); WAIT 8s — the run suspends parked on the `GetPromiseCommand` completion (pre-fix
B8 hang); invoke `Approve` with the same key through ingress; await the background `Run`
response. The resume batch replays `GetPromiseCommand` + its completion notification — the
Template A promise replay path that pre-fix deserialized command protobuf bytes as the promise
value (B1 shape) or hung on `known_entries` (B2).
Post-conditions: response `approved:{probeId}`; `attempt >= 2`; a second `Approve` with the
same key is rejected or idempotent (workflow promise single-resolution — assert no 5xx).

Scenario plumbing shared by E1–E8: each test generates `probeId = Guid.NewGuid().ToString("N")`,
invokes through `IngressClient` with `idempotency-key: probeId`, asserts post-conditions, and
prints the probe snapshot on failure. Per-test xunit timeout 180_000 ms; every poll loop uses
bounded `Task.Delay` polling (no unbounded waits).

### 2.5 In-process journal-replay harness (docker-free)

`test/Restate.Sdk.Tests/Integration/RecordedJournal.cs` — builds ON TOP of the blueprint's
`Testing/ProtocolTestHarness.cs` (Phase 3 lane 3a step 0); do not duplicate its frame helpers.

API (exact):

```csharp
internal sealed record RecordedFrame(MessageType Type, byte[] Payload);

internal sealed class FirstAttemptResult
{
    public IReadOnlyList<RecordedFrame> Frames { get; }        // every frame the SDK emitted
    public IReadOnlyList<RecordedFrame> Commands { get; }      // IsCommand() subset, journal order
    public bool Suspended { get; }                             // last frame is Suspension
    public Gen.SuspensionMessage? Suspension { get; }
}

internal static class RecordedJournal
{
    /// Runs attempt 1: Start{known_entries=1} + InputCommand through the REAL
    /// InvocationHandler.HandleAsync (service resolved via ServiceDefinitionRegistry, i.e. the
    /// source-generated invoker — the SAME layer as blueprint §4.2.1/§4.9; the harness's
    /// novelty is recording, not the layer). The script receives the live duplex pipe to
    /// deliver notifications mid-flight; calling script.CloseInput() completes the request
    /// pipe (EOF → post-fix suspension). `key` populates the Start message for keyed services
    /// (VirtualObject P4, Workflow P9).
    public static Task<FirstAttemptResult> RunFirstAttemptAsync(
        Type serviceType, string handlerName, byte[] inputJson,
        Func<AttemptScript, Task> script, string? key = null);

    /// Synthesizes attempt 2's known-entries batch: Start{known_entries = 1 + commands.Count +
    /// notifications.Length, partial_state per arg} + InputCommand + the recorded commands
    /// (VERBATIM bytes — never re-encoded) interleaved/appended with the given notifications,
    /// then runs HandleAsync again on a FRESH handler/registry and returns the same shape.
    public static Task<FirstAttemptResult> RunResumeAttemptAsync(
        Type serviceType, string handlerName, byte[] inputJson,
        IReadOnlyList<RecordedFrame> commands,
        RecordedFrame[] notifications,
        bool partialState = false,
        Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null,
        Func<AttemptScript, Task>? script = null, string? key = null);
}
```

Notification synthesis helpers (on `RecordedJournal`): `RunCompletion(uint id, object value)`,
`RunFailure(uint id, ushort code, string msg)`, `SleepCompletion(uint id)`,
`CallCompletion(uint id, object value)`, `CallInvocationId(uint idx, string invocationId)`,
`StateCompletion(uint id, object? value)`, `PromiseCompletion(uint id, object value)` — each
returns a `RecordedFrame` built with the generated protobuf types. Completion ids for synthesis
are PARSED from the recorded command bytes via `ProtobufCodec.ParseReplayCommand` (never
positional guessing) — this keeps the harness honest against the completion-id model.

`test/Restate.Sdk.Tests/Integration/JournalReplayDriverTests.cs` — scenarios (each uses the
ReplayLab service classes, asserting at the OUTPUT-frame level; all waits through
`AwaitBounded`):

| # | Scenario | Bugs | Assertions |
|---|---|---|---|
| P1 | `RunSleepRunService`: attempt 1 → script closes input after `SleepCommand` flushed → `Suspended == true`, `waiting_completions == [sleep id]`; resume with `RunCompletion(a)` + `SleepCompletion` → attempt 2 emits `RunCommand{b}` + proposal + Output; Output's `a` half equals attempt 1's proposed value (parse the recorded `ProposeRunCompletion`) | B1,B2,B3,B8 | byte-level value continuity across attempts |
| P2 | Same, but resume batch withholds `RunCompletion(a)` while including the later `SleepCommand` → attempt 2 fails with `ProtocolException` (journal-mutation guard), an Error frame — never a hang (watchdog) | B2 guard | bounded failure |
| P3 | `FanOutRunsService`: attempt 1 with the script releasing run gates in REVERSE order; record; resume → Output is `[0..15]`, commands replay without mismatch | B5 | creation-order journal proved at handler level |
| P4 | `PartialStateCounterObject` with `partialState: true, eagerState: {}` on the RESUME attempt: batch = recorded `SetStateCommand{A}` + `SleepCommand` + `SleepCompletion` notification (so the replay actually reaches the Gets); then `Get(A)` answered from the rebuilt cache (no `GetLazyStateCommand{A}` emitted), `Get(B)` EMITS `GetLazyStateCommand{B}` (the pre-fix silent-default path) which the script answers with `StateCompletion(id, null)` | B7 | exact command-emission set of attempt 2 |
| P5 | `SagaCompensationService`: attempt 1 → failure proposal recorded, input closed before notification → suspended; resume with `RunFailure(book-id, 500, "no rooms")` → attempt 2's Output is `"compensated"`, and NO second `ProposeRunCompletion{book}` is emitted (closure not re-executed) | B10b,B1 | deterministic failure re-raise + exactly-once side effect |
| P6 | `LazySendService`: attempt 1 records `OneWayCallCommand`; resume with `CallInvocationId(idx, "inv_test123")` + `SleepCompletion` → Output == `"inv_test123"` | B6 | id from notification, not command bytes |
| P7 | Frame-order audit: every P-scenario's attempt-1 stream (P1–P9) passes `AssertFrameOrder` (§4 preamble helper) | B8 | no frame after Suspension |
| P8 | `CallAcrossSuspensionService`: attempt 1 → script closes input after `CallCommand` flushed, NO notifications delivered → `Suspended == true`, `waiting_completions == [result_idx]`; resume batch = recorded `CallCommand` (verbatim) + `CallInvocationId(invocation_id_idx, "inv_p8")` + `CallCompletion(result_idx, "pong")` — BOTH ids parsed from the recorded command bytes — → attempt 2 emits NO second `CallCommand` and Output == `"pong"` | B1 (two-id model), B2, B3 | one wire command consumes two completion ids; ids honored by value, never by position |
| P9 | `ApprovalWorkflow.Run` (keyed): attempt 1 records `GetPromiseCommand{key="approval"}`, input closed → `Suspended == true`, `waiting_completions == [promise id]`; resume with `PromiseCompletion(id, "yes")` → Output == `"approved:yes"`, no second `GetPromiseCommand` | promise replay sites (blueprint §4.10), B1, B2, B8 | Template A promise replay through the full handler stack |

P1–P9 deliberately do NOT re-test SM-level branch behavior (§4 owns that); they pin the
END-TO-END contract "recorded journal + notifications ⇒ same durable answer" through the full
handler stack including serialization, generated invokers, and `InvocationHandler` unwinding —
with VERBATIM recorded frames, which no §4 scenario uses.

### 2.6 CI wiring

(a) Extend `.github/workflows/ci.yml` with a `coverage` job (after the existing `build` job;
same checkout/setup-dotnet steps, plus `denoland/setup-deno@v2` with `deno-version: v2.x`):

```yaml
  coverage:
    name: Coverage Gate
    runs-on: ubuntu-latest
    needs: build
    steps:
      - uses: actions/checkout@v6
        with: { submodules: recursive }
      - uses: actions/setup-dotnet@v5
        with: { dotnet-version: '10.0.102' }
      - uses: denoland/setup-deno@v2
        with: { deno-version: v2.x }
      - name: Restore tools
        run: dotnet tool restore
      - name: Test with coverage
        run: |
          dotnet test test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj -c Release \
            --collect:"XPlat Code Coverage" \
            --settings eng/coverage.runsettings \
            --results-directory artifacts/coverage
      - name: Merge + report
        run: |
          dotnet reportgenerator \
            -reports:"artifacts/coverage/**/coverage.cobertura.xml" \
            -targetdir:artifacts/coverage/report \
            -reporttypes:"Html;Cobertura;MarkdownSummaryGithub" \
            -assemblyfilters:"+Restate.Sdk" \
            -classfilters:"-Restate.Sdk.Internal.Protocol.Generated.*"
          cat artifacts/coverage/report/SummaryGithub.md >> "$GITHUB_STEP_SUMMARY"
      - name: Enforce thresholds + class audit
        run: |
          deno run --allow-read eng/coverage-gate.ts artifacts/coverage/report/Cobertura.xml \
            --phase-in --audit-internal src/Restate.Sdk/Internal
        # Phase 4b completion commit removes "--phase-in"; --audit-internal stays forever
        # (it is the proof that no attribute/filter silently removed hand-written code).
      - uses: actions/upload-artifact@v7
        if: always()
        with: { name: coverage-report, path: artifacts/coverage/report }
```

(b) New `.github/workflows/e2e.yml`:

```yaml
name: E2E
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
  workflow_dispatch:
permissions:
  contents: read
jobs:
  replay-e2e:
    name: Replay E2E (restate-server testcontainer)
    runs-on: ubuntu-latest          # docker preinstalled on GitHub runners
    timeout-minutes: 25
    steps:
      - uses: actions/checkout@v6
        with: { submodules: recursive }
      - uses: actions/setup-dotnet@v5
        with: { dotnet-version: '10.0.102' }
      - name: Pre-pull restate image
        run: docker pull docker.io/restatedev/restate:1.4
      - name: Run E2E suite
        run: |
          dotnet test test/Restate.Sdk.E2E/Restate.Sdk.E2E.csproj -c Release \
            --logger "trx;LogFileName=e2e.trx" --results-directory artifacts/e2e
      - name: Fail on skipped E2E tests (docker must be present in CI)
        run: |
          if grep -q 'outcome="NotExecuted"' artifacts/e2e/e2e.trx; then
            echo "E2E tests were skipped in CI — docker detection broke." >&2
            exit 1
          fi
      - uses: actions/upload-artifact@v7
        if: always()
        with: { name: e2e-results, path: artifacts/e2e }
```

Both jobs are required checks: any broken example ⇒ red build. The in-process P-suite lives in
`Restate.Sdk.Tests` and therefore already runs inside the existing `Build & Test` job AND the
`coverage` job (its lines count toward the gate).

(c) `.github/scripts/integration-test.sh` — additive: start `samples/ReplayLab` on port 9090,
register it, and run ONE smoke (`RunSleepRunService/Execute` + probe assertion via
`ProbeService/Get` using the existing `assert_response` helper, expecting `"run:a":1`). The
deep assertions belong to the E2E project; the script smoke just keeps the standalone sample
honest. Pin `RESTATE_IMAGE` in the script to the same `restatedev/restate:1.4` tag as the
fixture constant (replaces `:latest` — one tag everywhere).

### 2.7 Image-tag verification step (Phase 6, first task)

Before writing fixture code: `docker run --rm docker.io/restatedev/restate:1.4 --version` and a
1-minute manual handshake (run `samples/Counter` + register) to confirm (a) the tag exists and
speaks service protocol V4 (the fork is V4-only), (b) the
`RESTATE_WORKER__INVOKER__INACTIVITY_TIMEOUT`/`ABORT_TIMEOUT` env names are accepted (container
logs an error on unknown config), and (c) whether an eager-state size/disable knob exists for
the E4 container variant. Outcomes are recorded as a comment block at the top of
`RestateContainerFixture.cs`; if 1.4 is wrong, bump the single `ImageTag` constant + the script
+ the workflow pre-pull line (three greppable sites, same literal).

---

## 3. BUG → EXAMPLE/TEST MAP

"Would have caught it" = fails on the pre-fix code, passes post-fix.

| Bug | Faulty path | Unit/protocol test (blueprint §4 + Phase 4b) | In-process E2E (§2.5) | Testcontainer E2E (§2.4) |
|---|---|---|---|---|
| B1 | Replay deserializes command protobuf bytes as user results; CallCommand's two-id model (`invocation_id_idx`, `result_idx`) diverges positionally | §4.1.1, §4.1.2, §4.1.10, §4.1.13, §4.1.17 | P1 (run value continuity), P5, P6, P8 (two-id Call, ids by parse) | E1 (`run:a==1`, well-formed result), E5, E6, E7 (server-composed Call resume batch) |
| B2 | known_entries vs command-only Count → hang / skip-replay | §4.1.4 (hang), §4.1.5 (skip), §4.1.8, §4.1.18 | P1 (resume completes bounded), P2 (mutation guard), P8 (two notifications inflate the count for ONE command) | E1/E3/E4/E7 (any resume completing at all; E7 is the worst-case 2-notifications-per-command shape) |
| B3 | Dual PipeReader readers during replay | §4.2.1–§4.2.6 (incl. single-reader invariant + fuzz) | P1–P6, P8, P9 (notifications inside resume batch) | E1/E7 (server-composed batch with notifications) |
| B4 | Signal index 0/1 aliases CANCEL | §4.3.1–§4.3.3 | — (signal ids asserted in §4.3) | E2 (resolve 2nd awakeable id=18 via ingress; ids decode to 17/18) |
| B5 | Fan-out Run: completion-order journal, races, positional replay | §4.4.1–§4.4.6, §4.1.12, §4.1.17, G3/G4 | P3 (reverse completion order → correct replay) | E3 (16-way jittered fan-out across suspension) |
| B6 | Send blocks on invocation-id; replay UTF-8-decodes command bytes | §4.5.1–§4.5.8 | P6 (id from notification on resume) | E6 (id well-formed after suspend/resume) |
| B7 | Partial-state cache conflation; replay skips cache mutation | §4.6.1–§4.6.10, G10 | P4 (partial resume: cached A, lazy B — THE B7 gate) | E4 (state continuity only — complete eager state on a real resume cannot reach the cache-skip/lazy paths; real-server lazy path = residual gap §3.1, knob-variant per §2.7(c) with journal introspection) |
| B8 | No suspension: EOF parks handler forever, leaks | §4.7.1–§4.7.15, G1, G6, G7 | P1/P5/P8/P9 (Suspension frame + waiting set), P7 (frame order) | E1–E8 ALL (every scenario REQUIRES a real suspend/resume via `attempt >= 2`) |
| B9 | CompletionManager TOCTOU drops the only delivery | §4.8.1–§4.8.7, G2 | P1 (early-notification batch routed by wire id) | E2 (signal delivered while replaying/parked) |
| B10b | Terminal Run failure not re-raised on replay; empty-success journal entry | §4.1.2, §4.7.14, §4.4 (proposal-by-id), G5 | P5 (failure notification → compensation exactly once) | E5 (post-catch sleep forces the failed-Run REPLAY; `attempt >= 2` + `run:book == 1` — fails pre-fix) |
| Promise replay sites (B1/B2/B8 arm, blueprint §4.10 — 4 of the 26 rewritten sites) | GetPromise/PeekPromise/Resolve/Reject replay | §4.10.1–§4.10.x (lane 3e) | P9 (recorded GetPromiseCommand + synthesized completion) | E8 (workflow suspend on promise → shared-handler resolve → replayed promise resume) |

(B10a/B10c/B10d are non-bugs/dead-code per the blueprint §5; B10d's deletion is compile-gated
by `DurableFutureTests`, no E2E needed.)

### 3.1 Known residual gaps (documented, not hidden)

These faulty-path × layer cells remain uncovered BY DESIGN; each has a reason and an owner:

1. Real-server `partial_state=true` lazy fallthrough (B7): a healthy pinned server resumes with
   COMPLETE eager state, so no container scenario can reach the lazy path. Owner: in-process P4
   (deterministic, exact command-emission assertions). Upgrade path: if §2.7(c) finds an
   eager-state disable/limit knob, add the E4 variant asserting `GetLazyStateCommand` presence
   via admin journal introspection; until then this row is a documented gap — never an implied
   container coverage.
2. `AttachInvocation` / `GetInvocationOutput` replay through the handler stack or a real server:
   these have NO public context surface in the fork (internal `DefaultContext` members only —
   verified against `src/Restate.Sdk/IContext.cs` and `InvocationHandle`, which exposes only
   `GetInvocationIdAsync`). Coverage stays SM-level (blueprint §4.1.16 Template A/B smoke
   matrix). If the surface is made public later, add a P-scenario + container scenario in the
   same PR that adds the API.

---

## 4. OPUS EXECUTION SEQUENCING

Numbering continues the blueprint (its Phases 1–3 are the concurrent rewrite; GATE 3 =
`dotnet build` + `dotnet test` + format + AOT publish green). NOTHING below starts before
GATE 3. Phases here are file-disjoint from one another; lanes inside a phase are file-disjoint
from each other. Any src/ defect found by these tests is routed to a single fixer commit, never
fixed inside a test lane.

### Phase 4 — Coverage infrastructure + gap tests

- 4a (one executor, one commit): `Directory.Packages.props` (coverlet + Testcontainers lines),
  `test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj` (collector ref), `.config/dotnet-tools.json`,
  `eng/coverage.runsettings`, `eng/coverage-thresholds.json`, `eng/coverage-gate.ts`,
  `ci.yml` `coverage` job (with `--phase-in`).
  GATE 4a: the §1.1 command sequence runs green LOCALLY end-to-end (no `CI=true` needed — the
  runsettings omit DeterministicReport; gate passes under `--phase-in`); the
  `--audit-internal src/Restate.Sdk/Internal` class audit reports ZERO missing hand-written
  types (proves the attribute filters did not silently strip the async core); coverage job
  green in CI; snapshot report attached to the PR.
- 4b (parallel lanes, each owns only its new files; rebase against the FINAL Phase 3 test-file
  names before starting — see Open Issues):
  - 4b-i: `StateMachine/ReplayEdgeTests.cs` + `StateMachine/TerminalOpsEdgeTests.cs`
  - 4b-ii: `StateMachine/RunDetachedFailureTests.cs` + `Journal/CompletionManagerLatchTests.cs`
    (latch file CONDITIONAL per §1.2 H3 — only if the post-GATE-3 snapshot shows the
    TryFail/TryClaimForExecution after-latch arms uncovered)
  - 4b-iii: `Protocol/ProtocolReaderEdgeTests.cs` + `Protocol/ProtocolWriterEdgeTests.cs` +
    `Protocol/ProtobufCodecEdgeTests.cs`
  - 4b-iv: `Context/ContextSurfaceTests.cs`
  GATE 4b: full suite green; gate passes WITHOUT `--phase-in`; the same commit deletes
  `phaseInOverrides` from `eng/coverage-thresholds.json` and the flag from `ci.yml`, and
  RATCHETS any 98-branch namespace that the snapshot shows fully reachable up to 100 (§1.1(5)
  comment). Any rule still failing ⇒ either add the missing test, record an accepted
  compiler-emitted branch gap (file + branch + reason in the appendix, §1.3(3)), or apply the
  §1.3 escape hatch for a defensive-guard MEMBER — never lower a threshold, never attribute-out
  a compiler branch.

### Phase 5 — ReplayLab sample + in-process replay E2E

- 5a (one executor): `samples/ReplayLab/*` (csproj, Program + `ReplayLabHost.Build(int)`,
  Probes.cs, eight service files per §2.4 incl. `CallAcrossSuspensionService` and the
  `[Workflow] ApprovalWorkflow`), added to `Restate.Sdk.slnx`.
  GATE 5a: `dotnet build` green; `dotnet run --project samples/ReplayLab` serves `/discover`
  (manual or scripted curl with `--http2-prior-knowledge`).
- 5b (one executor; depends on 5a types): `Integration/RecordedJournal.cs` +
  `Integration/JournalReplayDriverTests.cs` (P1–P9). `Restate.Sdk.Tests.csproj` gains a
  ProjectReference to `samples/ReplayLab/ReplayLab.csproj` (additive ItemGroup line — disjoint
  from 4a's edit by content; if both land in one PR, 4a merges first).
  GATE 5b: P1–P9 green under the watchdog; coverage gate still green (the P-suite adds
  handler-path lines).

### Phase 6 — Testcontainer E2E + CI

- 6a (one executor): §2.7 image verification; then `test/Restate.Sdk.E2E/*` (csproj,
  `xunit.runner.json`, `DockerFactAttribute`, `RestateContainerFixture`, `IngressClient`,
  `ReplayLabE2eTests` with E1–E8), slnx entry.
  GATE 6a: `dotnet test test/Restate.Sdk.E2E` green locally with docker, 3 consecutive runs
  zero flakes (suspension timing is the flake risk; the `attempt >= 2` assertions plus bounded
  polling absorb cadence variance).
- 6b (one executor): `.github/workflows/e2e.yml` (§2.6(b)); `integration-test.sh` ReplayLab
  smoke + image-tag pin (§2.6(c)).
  GATE 6b (FINAL): CI fully green on a PR exercising all jobs: Build & Test, Format, Coverage
  Gate (no phase-in), Integration Test, AOT Deny-Gate, Replay E2E. Branch protection adds
  `coverage` and `replay-e2e` to required checks.

Cross-phase invariants:

- 2-space YAML/JSON, repo C# style for test code; `dotnet format` clean at every gate.
- No sync blocking waits anywhere in new tests (`.Wait()`/`.Result` banned — blueprint §4
  watchdog rule applies to all new suites; grep-audited at each gate).
- Every E2E scenario must FAIL (not skip, not pass) when its faulty path is reintroduced:
  enforced by the `attempt >= 2` probes (E-suite), the watchdog (hangs become failures), and
  the skipped-test grep in `e2e.yml`.

---

## Open issues (tracked; do not block Phase 4)

1. Phase 3 (blueprint) is in flight — Phase 4b/5b must rebase on the FINAL names of
   `Testing/ProtocolTestHarness.cs` helpers (`AwaitBounded`, `AssertFrameOrder`,
   `WriteFramedMessage`) before authoring; if a helper is missing, add it to the harness file
   in a standalone commit, never fork a copy.
2. Verify `restatedev/restate:1.4` per §2.7 (V4 protocol, env-var names for
   inactivity/abort timeouts, eager-state knob for the E4 container variant); bump the single
   `ImageTag` literal in three sites if needed.
3. Confirm coverlet's `ExcludeByAttribute=GeneratedCodeAttribute` actually strips the protobuf
   `Gen.*` classes and the `[LoggerMessage]`-generated bodies from the report; otherwise add
   the explicit `<Exclude>` filter noted in §1.1(4).
4. E3/P3 await fan-out futures via a sequential `GetResult()` loop (verified surface:
   `Context.WaitAll` returns `IAsyncEnumerable<(IDurableFuture, Exception?)>`, not a value
   aggregator). Blueprint 2.10/2.11 keeps `IDurableFuture` signatures; re-verify `GetResult`'s
   exact name/shape on the post-rewrite `DurableFuture` before authoring E3/P3.
5. `host.docker.internal` + `host-gateway` requires Docker ≥ 20.10 (fine on GitHub runners);
   local Podman users may need `DOCKER_HOST`/`TESTCONTAINERS_HOST_OVERRIDE` — document in the
   E2E project README header comment, do not engineer around it.
6. The `Restate.Sdk` assembly-wide 90/85 rule may need recalibration after the first Phase 4a
   snapshot if Hosting/Client dead-weight dominates; recalibrate by ADDING tests
   (TestHost-based hosting tests) — thresholds only ever ratchet up.
7. xunit 2.9.3 `[Fact(Timeout=...)]` aborts rather than fails sync-blocked tests — the
   AwaitBounded watchdog is the primary mechanism (blueprint §4 preamble); keep the grep audit.
8. E5 deliberately uses the void `ctx.Run(string, Func<Task>)` overload with NO `RetryPolicy`:
   a thrown `TerminalException` is never retried, and the verified `IContext` surface has no
   policy-bearing `RunAsync<T>` overload (policies exist only on the `Run`/`Run<T>` overloads,
   `src/Restate.Sdk/IContext.cs:38-49`). Verify the `TerminalException(message, code)` ctor
   order at implementation time (current surface: `TerminalException(string message,
   ushort code = 500)`).
9. E8 drives a `[Workflow]` through ingress with a key — verify the ingress URL shapes for the
   run handler and the shared `Approve` handler (`POST /{Service}/{key}/{Handler}` vs the
   workflow-specific routes) against the pinned server during the §2.7 verification step, and
   the idempotent/rejected behavior of a second `ResolvePromise` (E8's last post-condition) —
   relax that single assertion to "no handler crash" if the server returns a terminal error by
   design.
