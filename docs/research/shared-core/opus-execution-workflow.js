// Opus execution workflow for the Restate .NET SDK CoreVM-parity fix (B1-B10).
// Authored at design-time (Fable produced the blueprint; this script drives Opus executors).
// Run via: Workflow({ scriptPath: ".../docs/research/shared-core/opus-execution-workflow.js" })
// Spec of record: docs/research/shared-core/05-managed-fix-blueprint.md (§1 model, §2 per-file, §3 sequencing, §4 tests, §6 gates)
// Verified analysis: docs/research/shared-core/04-managed-bug-verification.json
// Baseline before any edit: 385 passed / 0 failed (net10.0).

export const meta = {
  name: 'restate-opus-execution',
  description: 'Opus implements the CoreVM-parity fix blueprint (B1-B10): foundation redesign, layered fixes, full protocol-level test suite, final verification — build+test gated at every phase',
  phases: [
    { title: 'Foundation', detail: 'B1-B4 core + B5/B7/B8/B9/B10b plumbing across the 6 entangled files (sequential, one executor)', model: 'opus' },
    { title: 'FoundationGate', detail: 'independent build+test verify, fix loop until green', model: 'opus' },
    { title: 'Layered', detail: 'B6 lazy send-handle, B10d dead-code, docs — parallel disjoint-file lanes', model: 'opus' },
    { title: 'LayeredGate', detail: 'build+test verify, fix loop', model: 'opus' },
    { title: 'TestHarness', detail: 'shared in-memory-pipe protocol test harness', model: 'opus' },
    { title: 'Tests', detail: 'protocol-level regression suite, parallel by area (§4)', model: 'opus' },
    { title: 'TestsGate', detail: 'build+test verify, route src fixes to one fixer', model: 'opus' },
    { title: 'Verify', detail: 'final gate: build/test/format/AOT + adversarial parity review (§6)', model: 'opus' },
  ],
}

const ROOT = '/home/steve/git/github.com/stevefan1999-personal/restate-sdk-dotnet'
const BLUEPRINT = `${ROOT}/docs/research/shared-core/05-managed-fix-blueprint.md`
const ANALYSIS = `${ROOT}/docs/research/shared-core/04-managed-bug-verification.json`
const TESTPROJ = `${ROOT}/test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj`
const VM = `${ROOT}/third_party/sdk-shared-core/src/vm`

const CONTEXT = `You are an OPUS EXECUTOR implementing a verified, hardened fix blueprint for a FORK of the Restate durable-execution .NET SDK (branch feat/sdk-shared-core).
Repo root: ${ROOT}
- SPEC OF RECORD (implement EXACTLY, it is code-level and was adversarially reviewed): ${BLUEPRINT}
- Verified bug analysis (evidence + shared-core citations): ${ANALYSIS}
- GROUND TRUTH reference VM to match: ${VM}/ (context.rs, mod.rs, transitions/{input,journal,async_results,terminal}.rs) and the proto under third_party/sdk-shared-core/service-protocol/.
- Target framework net10.0. Baseline BEFORE any edit: 385 tests pass / 0 fail. Tests that assert OLD buggy behavior are updated per blueprint §4.9; everything else must stay green.
RULES: Follow the blueprint section-by-section; do not invent a different design. Prefer shared-core parity. Comment the WHY, not the what. 2-space indent. No dead code, no TODOs. Do NOT git commit (the supervisor commits after review). Read the blueprint sections relevant to your task BEFORE editing.`

const GATE_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['buildGreen', 'testsGreen', 'totalTests', 'passed', 'failed', 'buildErrors', 'failingTests'],
  properties: {
    buildGreen: { type: 'boolean' },
    testsGreen: { type: 'boolean' },
    totalTests: { type: 'number' },
    passed: { type: 'number' },
    failed: { type: 'number' },
    buildErrors: { type: 'array', items: { type: 'string' } },
    failingTests: { type: 'array', items: { type: 'string' } },
  },
}

const IMPL_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['done', 'filesChanged', 'buildGreen', 'testsGreen', 'passed', 'failed', 'deviations', 'srcBugsFound', 'notes'],
  properties: {
    done: { type: 'boolean' },
    filesChanged: { type: 'array', items: { type: 'string' } },
    buildGreen: { type: 'boolean' },
    testsGreen: { type: 'boolean' },
    passed: { type: 'number' },
    failed: { type: 'number' },
    deviations: { type: 'array', items: { type: 'string' }, description: 'Anywhere you deviated from the blueprint and why' },
    srcBugsFound: { type: 'array', items: { type: 'string' } },
    notes: { type: 'string' },
  },
}

// Independent verify + bounded fix loop. Returns the final gate verdict.
async function gate(phaseTitle, maxFix) {
  const verifyPrompt = `${CONTEXT}\n\nTASK: From ${ROOT} run \`dotnet build -c Release --nologo\` then \`dotnet test ${TESTPROJ} -c Release --nologo\`. Report the precise build errors and the names of any failing tests. Do NOT edit any file — verify only.`
  let verdict = await agent(verifyPrompt, { label: `${phaseTitle}:verify`, phase: phaseTitle, model: 'opus', schema: GATE_SCHEMA })
  let attempt = 0
  while (verdict && !(verdict.buildGreen && verdict.testsGreen) && attempt < maxFix) {
    attempt++
    await agent(
      `${CONTEXT}\n\nTASK: Build/tests are RED after phase ${phaseTitle} (attempt ${attempt}/${maxFix}).\nBuild errors: ${JSON.stringify(verdict.buildErrors)}\nFailing tests: ${JSON.stringify(verdict.failingTests)}\nFix the ROOT CAUSE per the blueprint and shared-core parity. Do NOT weaken or delete a test to make it pass UNLESS that test asserts old buggy behavior explicitly covered by blueprint §4.9 (then update it per §4.9 and say so). Re-run build+test to confirm before returning. Report exactly what you changed and why.`,
      { label: `${phaseTitle}:fix#${attempt}`, phase: phaseTitle, model: 'opus' }
    )
    verdict = await agent(verifyPrompt, { label: `${phaseTitle}:verify#${attempt}`, phase: phaseTitle, model: 'opus', schema: GATE_SCHEMA })
  }
  return verdict
}

// ---------- Phase 1: Foundation (single executor, the entangled core) ----------
phase('Foundation')
const foundation = await agent(
  `${CONTEXT}\n\nTASK — FOUNDATION (blueprint §3 Phase 1; implement §2.1-2.8, §2.10 Phase-1 thunk adaptation, §2.11, and the §1 target model).\n` +
  `Implement, IN THE ORDER §3 Phase 1 lists, the unified CoreVM-parity core across these entangled files (none compiles alone, so edit them as one series):\n` +
  `  1. Internal/Journal/JournalEntry.cs (ReplayCommand; drop result-carrying JournalEntry per §2.2)\n` +
  `  2. Internal/Journal/InvocationJournal.cs (ReplayCommand queue + cursor + DequeueReplay/RecordCommand; IsReplaying => queue non-empty; §2.1)\n` +
  `  3. Internal/Protocol/ProtocolTypes.cs + Internal/Protocol/ProtobufCodec.cs (ParseReplayCommand full switch, PartialState, CreateGetEagerState/Keys, CreateSuspensionMessage; §2.3)\n` +
  `  4. Internal/Journal/CompletionManager.cs (single lock, keyed by WIRE ids, FailAll/PendingIds/HasResultFor/TryClaimForExecution, terminal latch; delete Register; §2.4, B9)\n` +
  `  5. Internal/SuspendedException.cs (new) + Internal/StateMachine/InvocationStateMachine.cs (_nextCompletionId=1, _nextSignalId=17, eager-state model, _commandLock/_flushGate, AwaitNotificationAsync park API, TrySuspend, KNOWN_ENTRIES_IS_ZERO; §2.5, §1.1/1.4/1.5/1.6)\n` +
  `  6. Internal/StateMachine/InvocationStateMachine.Protocol.cs (StartAsync preflight buffering; sole-reader pump with suspension/abort unwind; synchronous DequeueReplayCommand + ValidateReplayCompletionId; delete ReplayNextEntryAsync/AdvanceReplayIndex/MapMessageTypeToEntry; §2.6, B2/B3)\n` +
  `  7. Internal/StateMachine/InvocationStateMachine.Operations.cs (BIGGEST: replay templates A/B/C/D applied to all ~26 call sites; result-from-notification not command bytes; Call two-id drops dummy-slot hack; Run creation-order journaling + ExecuteAndProposeRunAsync + B10b failure-via-notification; eager-state tri-state Get + unconditional cache mutation; in-lock serialize; §2.7, B1/B5/B7/B10b)\n` +
  `  8. Internal/InvocationHandler.cs (catch SuspendedException; broadened pump-fault handling; §2.8) and Internal/Log.cs (any new log ids)\n` +
  `  9. Internal/DurableFuture.cs thunk adaptation only (§2.10 Phase 1) + Internal/Context/DefaultContext.cs & siblings compile fixes (§2.11)\n` +
  ` 10. Compile-fix the EXISTING tests the redesign breaks (per §3 Phase 1 + §4.9): Journal/JournalTests.cs, Journal/CompletionManagerTests.cs, StateMachine/InvocationStateMachineTests.cs (knownEntries==0 now throws; JournalEntry/Register APIs gone), OptionsTests.cs InvocationHandle site, DurableFutureTests.cs VoidDurableFuture.Completed() site.\n` +
  `Iterate with \`dotnet build -c Release\` + \`dotnet test ${TESTPROJ} -c Release --nologo\` until BOTH are green (385 baseline minus the §4.9 updates). If you cannot reach green after a genuine effort, STOP and return done:false with a precise blocker report rather than hacking. Return the structured result.`,
  { label: 'foundation:core', phase: 'Foundation', model: 'opus', schema: IMPL_SCHEMA }
)
const g1 = await gate('FoundationGate', 4)

// ---------- Phase 2: Layered parallel lanes (disjoint files) ----------
phase('Layered')
const layered = await parallel([
  () => agent(
    `${CONTEXT}\n\nTASK — LANE 2a (B6, blueprint §1.8 + §2.7 Send sections + §2.9). The foundation left ctx.Send wire-id-correct but still blocking. Make InvocationHandle lazy (Lazy<Task<string>> with a Func<Task<string>> ctor, keep the eager string ctor for ingress/known-id sites — Rust SendHandle / Python parity), remove the blocking await from both SendAsync overloads (fire-and-forget), add GetInvocationIdAsync as an explicit suspension point, and compile-sweep all eager construction sites (RestateClient/route builder/InvocationHandler/MockContext + any sample using .InvocationId). Only edit InvocationHandle.cs, the Send sections of Operations.cs, MockContext.cs, and affected samples. Build+test green before returning.`,
    { label: 'layered:2a-send-handle', phase: 'Layered', model: 'opus', schema: IMPL_SCHEMA }
  ),
  () => agent(
    `${CONTEXT}\n\nTASK — LANE 2b (B10d, §2.10 Phase 2b). Delete the dead VoidDurableFuture.CachedCompleted / Completed() / CreateCompleted() members from Internal/DurableFuture.cs (zero PRODUCTION call sites; the DurableFutureTests.cs site was already handled in Phase 1 — verify and adjust if needed). Touch ONLY DurableFuture.cs (and that one test if still referencing it). Build+test green before returning.`,
    { label: 'layered:2b-deadcode', phase: 'Layered', model: 'opus', schema: IMPL_SCHEMA }
  ),
  () => agent(
    `${CONTEXT}\n\nTASK — LANE 2c (docs). Write/refresh docs/research/shared-core/06-managed-fix-changelog.md describing the new replay/completion-id/suspension model, the journal-incompatibility release note (§6.10), and the deliberate divergences from Rust (payload-equality deferral, empty-state normalization, cancellation/named-signal out of scope) per blueprint §5. Touch ONLY docs/. No code.`,
    { label: 'layered:2c-docs', phase: 'Layered', model: 'opus' }
  ),
])
const g2 = await gate('LayeredGate', 3)

// ---------- Phase 3: Test harness, then parallel test-authoring lanes ----------
phase('TestHarness')
await agent(
  `${CONTEXT}\n\nTASK — shared protocol test harness (blueprint §4 preamble + §4.2). Create test/Restate.Sdk.Tests/Testing/ProtocolTestHarness.cs: an in-memory duplex System.IO.Pipelines pair driving InvocationStateMachine / InvocationHandler with synthetic V4 frames (StartMessage/InputCommand/commands/notifications builders reusing ProtobufCodec + generated messages), an AwaitBounded(task, 5s) helper, a single-reader-invariant probe (Interlocked pending-read counter that asserts <=1), and an AssertFrameOrder wire-order helper. Model on existing Protocol/ProtocolReaderWriterTests.cs and Integration/ProtocolIntegrationTests.cs conventions. Build green (the harness compiles even before tests use it). Touch ONLY the new harness file.`,
  { label: 'tests:harness', phase: 'TestHarness', model: 'opus', schema: IMPL_SCHEMA }
)
phase('Tests')
const TEST_LANES = [
  { key: '3a-replay-singlereader', spec: '§4.1 ReplayTests.cs (B1/B2/B10b — replay returns notification value, terminal-failure re-raise, no-hang, mixed batch, mid-replay guards, commands-after-Output) and §4.2 SingleReaderTests.cs (B3 — state.rs:47-70 port, single-reader invariant, no Concurrent-reads exception).' },
  { key: '3b-eager-signals', spec: '§4.6 EagerStateTests.cs (B7 — partial-state silent-default regression, complete-state eager+GetEagerStateCommand, cleared marker, clear_all is_partial flip, replay determinism, GetEagerStateKeys) and §4.3 SignalIndexTests.cs (B4 — first awakeable idx 17, second 18, CANCEL idx=1 ignored, idx 17 value resolves).' },
  { key: '3c-suspension-send', spec: '§4.7 SuspensionTests.cs (B8 — EOF-before-park for Sleep/awakeable/DurableFuture/send-handle, SuspensionMessage waiting_completions correctness, no-premature/no-spurious suspension, suspension-vs-failure exclusivity, leak/WeakReference, abort path) and §4.5 SendHandleTests.cs (B6 — fire-and-forget returns without notification, GetInvocationIdAsync resolves, replay handle id).' },
  { key: '3d-runfanout-cmrace', spec: '§4.4 RunFanOutTests.cs (B5 — gated A/B completion-order journal order, distinct sequential completion ids, 64-task concurrency stress with min-thread bump + frame integrity, replay type/name mismatch throws) and §4.8 Journal/CompletionManagerRaceTests.cs (B9 — 10k-iteration TryComplete-vs-GetOrRegister race, TryFail race, latch/duplicate-redelivery).' },
  { key: '3e-promise-existing', spec: '§4.10 PromiseTests.cs (mirror promise.rs incl. counter-burn adjacency) and §4.9 updates to existing suites + one resumed-invocation e2e added to Integration/ProtocolIntegrationTests.cs.' },
]
const tests = await parallel(TEST_LANES.map((l) => () =>
  agent(
    `${CONTEXT}\n\nTASK — TEST LANE ${l.key} (blueprint ${l.spec}).\nAuthor ONLY the test files for this lane using the shared Testing/ProtocolTestHarness.cs. Each test must FAIL against the pre-fix behavior conceptually and PASS now. Use [Fact(Timeout=10000)] backstops and AwaitBounded for every wait (no naked sync blocks — xunit v2). If you discover a SOURCE bug (not a test bug), do NOT edit src — report it in srcBugsFound with file/line/symptom so a single fixer handles it. Run \`dotnet test ${TESTPROJ} -c Release --nologo --filter <yourClass>\` to confirm your lane. Return the structured result.`,
    { label: `tests:${l.key}`, phase: 'Tests', model: 'opus', schema: IMPL_SCHEMA }
  )
))
// Route any src bugs the test lanes found to one sequential fixer, then gate.
const srcBugs = tests.filter(Boolean).flatMap((t) => t.srcBugsFound || [])
if (srcBugs.length > 0) {
  await agent(
    `${CONTEXT}\n\nTASK — SRC FIXER. The test lanes reported these SOURCE-level defects (not test bugs):\n${JSON.stringify(srcBugs, null, 2)}\nFix each at the root per the blueprint + shared-core parity. Re-run the full \`dotnet test ${TESTPROJ} -c Release --nologo\` to confirm. Report what you changed.`,
    { label: 'tests:src-fixer', phase: 'Tests', model: 'opus' }
  )
}
const g3 = await gate('TestsGate', 4)

// ---------- Phase 4: Final verification + adversarial parity review ----------
phase('Verify')
const FINAL_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['buildGreen', 'testsGreen', 'totalTests', 'passed', 'failed', 'formatClean', 'aotClean', 'blueprintDeviations', 'residualRisks', 'verdict'],
  properties: {
    buildGreen: { type: 'boolean' }, testsGreen: { type: 'boolean' },
    totalTests: { type: 'number' }, passed: { type: 'number' }, failed: { type: 'number' },
    formatClean: { type: 'boolean' }, aotClean: { type: 'boolean' },
    blueprintDeviations: { type: 'array', items: { type: 'string' } },
    residualRisks: { type: 'array', items: { type: 'string' } },
    verdict: { type: 'string', enum: ['SHIP', 'NEEDS_WORK'] },
  },
}
const [finalGate, review] = await parallel([
  () => agent(
    `${CONTEXT}\n\nTASK — FINAL GATE (blueprint §6). From ${ROOT} run, and report results of: (1) \`dotnet build -c Release --nologo\`; (2) \`dotnet test ${TESTPROJ} -c Release --nologo\` (report total/passed/failed; expect >=385 + new, 0 failed); (3) \`dotnet format --verify-no-changes\` (formatClean); (4) AOT smoke: \`dotnet publish samples/NativeAotGreeter/NativeAotGreeter.csproj -c Release -r linux-x64\` (or the repo's AOT sample) and confirm zero NEW IL2026/IL3050 warnings (aotClean). Do NOT edit code; if format is dirty, run \`dotnet format\` ONCE then re-verify. Report the structured verdict.`,
    { label: 'verify:final-gate', phase: 'Verify', model: 'opus', schema: FINAL_SCHEMA }
  ),
  () => agent(
    `${CONTEXT}\n\nTASK — ADVERSARIAL PARITY REVIEW. Read the final diff (\`git diff\` + \`git status\`) and the blueprint. Independently verify, against ${VM}/ and the proto, that the implementation actually matches CoreVM for: completion-id counter (1) and signal-id counter (17) advancing identically in replay and processing; Call=2 ids / Send=1 / Sleep=1 / promise=1; replay values sourced from notifications by wire id (NEVER command bytes); IsReplaying == replay-queue-non-empty; single-reader (pump is the only _reader consumer post-StartAsync); the Monitor command-lock ordering invariant (id order == journal order == call order); await-driven suspension (no EOF-before-park deadlock, no spurious suspension, SuspensionMessage waiting_completions correct); eager-state is_partial tri-state + unconditional cache mutation incl. replay; Run creation-order journaling + failure-via-notification (B10b). List any place the code diverges from the blueprint/VM with file:line. Do NOT edit. Return the most important issues as text.`,
    { label: 'verify:parity-review', phase: 'Verify', model: 'opus' }
  ),
])

return {
  foundation,
  foundationGate: g1,
  layered: layered.filter(Boolean),
  layeredGate: g2,
  tests: tests.filter(Boolean),
  srcBugsRouted: srcBugs.length,
  testsGate: g3,
  finalGate,
  parityReview: review,
}
