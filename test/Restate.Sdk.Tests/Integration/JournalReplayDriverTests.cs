using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Restate.Sdk;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Integration.RecordedJournal;

namespace Restate.Sdk.Tests.Integration;

// ── Probe ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
///     The §2.4 execution probe: a process-global counter map keyed by a caller-supplied probe id.
///     Every handler increments "attempt" as its FIRST statement (replay re-executes handler code,
///     so a second attempt bumps it to >= 2) and increments "run:{name}" inside each Run closure
///     EXACTLY where it executes. The discriminating assertions read these: "attempt >= 2" proves
///     the suspend+replay path genuinely ran, and "run:{name} == 1" proves a journaled side effect
///     was NOT re-executed on replay (exactly-once despite two handler attempts — the durable
///     guarantee the pre-fix bug violated).
/// </summary>
internal static class ReplayProbe
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> Counters = new();

    public static void Increment(string probeId, string counter) =>
        Counters.GetOrAdd(probeId, _ => new ConcurrentDictionary<string, int>())
            .AddOrUpdate(counter, 1, (_, current) => current + 1);

    public static int Get(string probeId, string counter) =>
        Counters.TryGetValue(probeId, out var map) && map.TryGetValue(counter, out var value) ? value : 0;
}

// ── Replay-bait services (the §2.4 service shapes, scoped to the P1–P9 needs) ──────────────────

[Service(Name = "RunSleepRun")]
public sealed class RunSleepRunService
{
    [Handler]
    public async Task<string> Execute(Context ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        var a = await ctx.RunAsync("a", () =>
        {
            ReplayProbe.Increment(probeId, "run:a");
            return Task.FromResult("value-a");
        }).GetResult();
        await ctx.Sleep(TimeSpan.FromSeconds(8));   // parks → suspension forcer
        var b = await ctx.RunAsync("b", () =>
        {
            ReplayProbe.Increment(probeId, "run:b");
            return Task.FromResult("value-b");
        }).GetResult();
        return $"{a}|{b}";
    }
}

[Service(Name = "FanOutRuns")]
public sealed class FanOutRunsService
{
    public const int FanOut = 16;

    [Handler]
    public async Task<int[]> Scatter(Context ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        var futures = Enumerable.Range(0, FanOut)
            .Select(i => ctx.RunAsync($"part-{i}", () =>
            {
                ReplayProbe.Increment(probeId, $"run:part-{i}");
                return Task.FromResult(i);
            }))
            .ToArray();
        // Await in creation order (futures resolve from notifications; completion order is jittered).
        var parts = new int[futures.Length];
        for (var i = 0; i < futures.Length; i++) parts[i] = await futures[i].GetResult();
        await ctx.Sleep(TimeSpan.FromSeconds(8));   // force suspension AFTER the fan-out journaled
        return parts;
    }
}

[VirtualObject(Name = "PartialStateCounter")]
public sealed class PartialStateCounterObject
{
    private static readonly StateKey<string> A = new("A");
    private static readonly StateKey<string> B = new("B");

    [Handler]
    public async Task<string> Mutate(ObjectContext ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        ctx.Set(A, $"a-{probeId}");
        await ctx.Sleep(TimeSpan.FromSeconds(8));   // suspension between Set and Gets
        var a = await ctx.Get(A);                    // from the eager cache rebuilt on replay (no command)
        var b = await ctx.Get(B);                    // never written → lazy roundtrip under partial state
        return $"{a}|{b ?? "<null>"}";
    }
}

[Service(Name = "SagaCompensation")]
public sealed class SagaCompensationService
{
    [Handler]
    public async Task<string> Book(Context ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        try
        {
            // void Run overload, NO RetryPolicy — TerminalException is never retried.
            await ctx.Run("book", () =>
            {
                ReplayProbe.Increment(probeId, "run:book");
                throw new TerminalException("no rooms", 500);
            });
            return "booked";
        }
        catch (TerminalException)
        {
            await ctx.Sleep(TimeSpan.FromSeconds(8));   // forces suspension AFTER the failed Run journaled
            await ctx.RunAsync("compensate", () =>
            {
                ReplayProbe.Increment(probeId, "run:compensate");
                return Task.FromResult(true);
            }).GetResult();
            return "compensated";
        }
    }
}

[Service(Name = "LazySend")]
public sealed class LazySendService
{
    [Handler]
    public async Task<string> SendAndReport(Context ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        var handle = await ctx.Send<string>("SlowEcho", "Echo", probeId);
        await ctx.Sleep(TimeSpan.FromSeconds(8));   // suspend; resume replays the OneWayCallCommand
        return await handle.GetInvocationIdAsync();
    }
}

[Service(Name = "CallAcrossSuspension")]
public sealed class CallAcrossSuspensionService
{
    [Handler]
    public async Task<string> Relay(Context ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        // SlowEcho durably sleeps then echoes — the caller suspends parked on the call result id.
        return await ctx.Call<string>("SlowEcho", "Echo", probeId);
    }
}

[Workflow(Name = "ApprovalWorkflow")]
public sealed class ApprovalWorkflow
{
    [Handler]
    public async Task<string> Run(WorkflowContext ctx, string probeId)
    {
        ReplayProbe.Increment(probeId, "attempt");
        var decision = await ctx.Promise<string>("approval");   // parks → suspension
        return $"approved:{decision}";
    }
}

/// <summary>
///     Plan 07 §2.5 — the in-process journal-replay driver (P1–P9). Each scenario runs attempt 1 of a
///     REAL handler through <c>InvocationHandler.HandleAsync</c> over live <see cref="System.IO.Pipelines.Pipe" />
///     pairs, forces suspension by closing the request pipe, RECORDS the emitted V4 command frames,
///     synthesizes attempt 2's known-entries batch from those VERBATIM bytes plus notifications whose
///     ids are PARSED from the recorded commands, feeds it through a FRESH handler, and asserts the
///     durable result survives. Every scenario is DISCRIMINATING: it would FAIL against the pre-fix
///     bug (B1 JSON-decoded command bytes; B2 hung in Replaying on notification-inflated known_entries;
///     B3 double-read the wire; B8 could never suspend). The assertions pin "resumed value == recorded
///     proposal", "run closure runs exactly once", "no second command re-emitted", and "no hang"
///     (the harness watchdog turns every pre-fix infinite hang into a bounded failure).
///
///     These do NOT re-test SM-level branch behaviour (blueprint §4 owns that); they pin the
///     end-to-end contract "recorded journal + notifications ⇒ same durable answer" through the full
///     handler stack including serialization, generated invokers, and InvocationHandler unwinding —
///     with VERBATIM recorded frames, which no §4 scenario uses.
/// </summary>
public sealed class JournalReplayDriverTests
{
    private const int WatchdogMs = 30_000;

    private static byte[] Json(string value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string ProbeId() => Guid.NewGuid().ToString("N");

    private static string DecodeJsonString(Gen.OutputCommandMessage output) =>
        JsonSerializer.Deserialize<string>(output.Value.Content.Span)!;

    // ── P1 — Run / Sleep / Run: byte-level durable-value continuity across attempts ────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P1_RunSleepRun_ResumedValueEqualsRecordedProposal()
    {
        var probeId = ProbeId();

        // Attempt 1: release the run "a" proposal with its ack, then close input once the Sleep
        // command is flushed — the park on the sleep id becomes a Suspension (B8), not a hang.
        var first = await RunFirstAttemptAsync(typeof(RunSleepRunService), "Execute", Json(probeId),
            async script =>
            {
                var proposal = await script.WaitForAsync(MessageType.ProposeRunCompletion);
                var runId = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload).ResultCompletionId;
                await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(proposal));
                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            });

        Assert.True(first.Suspended, "Attempt 1 must end in a Suspension (B8).");
        // The suspension parks on exactly the Sleep completion id.
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);
        Assert.Contains(sleepId, first.Suspension!.WaitingCompletions);

        // The durable value the SDK proposed for run "a" — attempt 2's Output must reproduce it.
        var recordedProposalA = first.Frames.First(f => f.Type == MessageType.ProposeRunCompletion);
        var proposedValueA = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(recordedProposalA.Payload)
            .Value.ToByteArray();
        var aValue = JsonSerializer.Deserialize<string>(proposedValueA)!;

        // Attempt 2: replay the recorded RunCommand{a} + SleepCommand verbatim, plus the run "a"
        // completion (id parsed from the recorded command) and the sleep completion. Once replay
        // drains, run "b" executes LIVE in Processing mode and emits a fresh proposal; the resume
        // script releases that proposal's ack so the handler runs to completion.
        var runAId = CompletionIdOf(first.Commands, MessageType.RunCommand, "a");
        var resume = await RunResumeAttemptAsync(typeof(RunSleepRunService), "Execute", Json(probeId),
            commands: first.Commands.Where(c => c.Type is MessageType.RunCommand or MessageType.SleepCommand).ToArray(),
            notifications: [RunCompletion(runAId, aValue), SleepCompletion(sleepId)],
            script: async script =>
            {
                var runB = await script.WaitForFrameAsync(f =>
                    f.Type == MessageType.ProposeRunCompletion &&
                    Gen.ProposeRunCompletionMessage.Parser.ParseFrom(f.Payload).ResultCompletionId != runAId);
                await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(runB));
                script.CloseInput();
            });

        Assert.False(resume.Suspended, "Attempt 2 must complete, not re-suspend.");
        var resumedOutput = DecodeJsonString(resume.Output!);
        Assert.Equal($"{aValue}|value-b", resumedOutput);          // run "a" value survived replay byte-for-byte
        Assert.Equal(1, ReplayProbe.Get(probeId, "run:a"));        // closure "a" never re-executed
        Assert.Equal(1, ReplayProbe.Get(probeId, "run:b"));
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2, "the suspend+replay path genuinely ran");
    }

    // ── P2 — withhold the Run completion: bounded ProtocolException, never a hang ───────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P2_ResumeWithholdsRunCompletion_FailsBoundedNotHang()
    {
        var probeId = ProbeId();
        var first = await RunFirstAttemptAsync(typeof(RunSleepRunService), "Execute", Json(probeId),
            async script =>
            {
                var proposal = await script.WaitForAsync(MessageType.ProposeRunCompletion);
                await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(proposal));
                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            });
        Assert.True(first.Suspended);

        // Resume batch WITHHOLDS RunCompletion(a) while still declaring the later SleepCommand in
        // known_entries: run "a" is left uncompleted during replay even though a later journaled
        // command exists — the journal-mutation guard (UncompletedDoProgressDuringReplay parity, B2).
        // The post-fix SDK raises a ProtocolException → an Error frame, bounded; the pre-fix SDK hung
        // in Replaying forever (the watchdog would turn that hang into a TimeoutException failure).
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);
        var resume = await RunResumeAttemptAsync(typeof(RunSleepRunService), "Execute", Json(probeId),
            commands: first.Commands.Where(c => c.Type is MessageType.RunCommand or MessageType.SleepCommand).ToArray(),
            notifications: [SleepCompletion(sleepId)]);   // run-a completion deliberately missing

        // No fabricated value: an Error frame (the guard) and NO Output — never a hang.
        Assert.Null(resume.Output);
        Assert.Contains(resume.Frames, f => f.Type == MessageType.Error);
        var error = resume.Frames.First(f => f.Type == MessageType.Error);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(error.Payload);
        Assert.Contains("journal mutation", parsed.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── P3 — fan-out: creation-order journal proved at the handler level, gates released reversed ─

    [Fact(Timeout = WatchdogMs)]
    public async Task P3_FanOut_ReverseReleaseOrder_OutputIsCreationOrdered()
    {
        var probeId = ProbeId();

        // Attempt 1: wait for all 16 proposals, then deliver their completions in REVERSE id order
        // before closing input — completion order != creation order, the B5 trap.
        var first = await RunFirstAttemptAsync(typeof(FanOutRunsService), "Scatter", Json(probeId),
            async script =>
            {
                var proposals = new List<Gen.ProposeRunCompletionMessage>();
                for (var seen = 0; seen < FanOutRunsService.FanOut; seen++)
                {
                    var proposal = await script.WaitForFrameAsync(f =>
                        f.Type == MessageType.ProposeRunCompletion &&
                        proposals.All(p => p.ResultCompletionId !=
                            Gen.ProposeRunCompletionMessage.Parser.ParseFrom(f.Payload).ResultCompletionId));
                    proposals.Add(Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload));
                }

                foreach (var proposal in proposals.OrderByDescending(p => p.ResultCompletionId))
                    await script.DeliverAsync(MessageType.RunCompletion, RunCompletionFor(proposal));

                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            });
        Assert.True(first.Suspended);

        // Attempt 2: replay the 16 RunCommands + SleepCommand verbatim with all completions. The
        // Output must be [0..15] in creation order — no cross-wiring (B5).
        var runCommands = first.Commands.Where(c => c.Type == MessageType.RunCommand).ToArray();
        Assert.Equal(FanOutRunsService.FanOut, runCommands.Length);
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);

        var notifications = runCommands
            .Select(c =>
            {
                var parsed = ProtobufCodec.ParseReplayCommand(c.Type, c.Payload);
                var index = int.Parse(parsed.Name!.AsSpan("part-".Length), provider: CultureInfo.InvariantCulture);
                return RunCompletion(parsed.ResultCompletionId, index);
            })
            .Append(SleepCompletion(sleepId))
            .ToArray();

        var resume = await RunResumeAttemptAsync(typeof(FanOutRunsService), "Scatter", Json(probeId),
            commands: first.Commands.Where(c => c.Type is MessageType.RunCommand or MessageType.SleepCommand).ToArray(),
            notifications: notifications);

        Assert.False(resume.Suspended);
        var resultArray = JsonSerializer.Deserialize<int[]>(resume.Output!.Value.Content.Span)!;
        Assert.Equal(Enumerable.Range(0, FanOutRunsService.FanOut).ToArray(), resultArray);
        for (var i = 0; i < FanOutRunsService.FanOut; i++)
            Assert.Equal(1, ReplayProbe.Get(probeId, $"run:part-{i}"));   // each closure exactly once
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── P4 — partial-state object: exact attempt-2 command-emission set (B7) ────────────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P4_PartialStateObject_LazyFallthroughEmitsExactCommandSet()
    {
        var probeId = ProbeId();

        var first = await RunFirstAttemptAsync(typeof(PartialStateCounterObject), "Mutate", Json(probeId),
            async script =>
            {
                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            }, key: "obj-1");
        Assert.True(first.Suspended);

        // Resume with partial_state=true and an EMPTY eager map: only the recorded SetStateCommand{A}
        // + SleepCommand replay, plus the SleepCompletion so execution reaches the Gets. Get(A) is
        // answered from the cache the replayed SetState rebuilt (no GetLazyStateCommand{A}); Get(B)
        // — never written, partial — EMITS GetLazyStateCommand{B}, the pre-fix silent-default path.
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);
        var lazyBId = 0u;
        var resume = await RunResumeAttemptAsync(typeof(PartialStateCounterObject), "Mutate", Json(probeId),
            commands: first.Commands
                .Where(c => c.Type is MessageType.SetStateCommand or MessageType.SleepCommand).ToArray(),
            notifications: [SleepCompletion(sleepId)],
            partialState: true,
            eagerState: new Dictionary<string, ReadOnlyMemory<byte>>(),
            key: "obj-1",
            script: async script =>
            {
                // The batch is pre-seeded; deliver nothing yet — wait for the live lazy Get(B), answer
                // it with a known-absent (null) completion, then the handler completes on its own.
                var lazyGet = await script.WaitForAsync(MessageType.GetLazyStateCommand);
                var parsed = ProtobufCodec.ParseReplayCommand(lazyGet.Type, lazyGet.Payload);
                lazyBId = parsed.ResultCompletionId;
                Assert.Equal("B", parsed.Name);
                await script.DeliverAsync(MessageType.GetLazyStateCompletion,
                    GetStateAbsentFor(parsed.ResultCompletionId));
                script.CloseInput();
            });

        Assert.False(resume.Suspended);
        Assert.Equal($"a-{probeId}|<null>", DecodeJsonString(resume.Output!));

        // Exact command-emission set: Get(A) answered from cache → an EagerState command, never a
        // lazy one; Get(B) is the ONLY lazy state command emitted on attempt 2.
        var lazyStateCommands = resume.Commands.Where(c => c.Type == MessageType.GetLazyStateCommand).ToArray();
        Assert.Single(lazyStateCommands);
        var onlyLazy = ProtobufCodec.ParseReplayCommand(lazyStateCommands[0].Type, lazyStateCommands[0].Payload);
        Assert.Equal("B", onlyLazy.Name);
        Assert.Equal(lazyBId, onlyLazy.ResultCompletionId);
        Assert.DoesNotContain(resume.Commands, c =>
            c.Type == MessageType.GetLazyStateCommand &&
            ProtobufCodec.ParseReplayCommand(c.Type, c.Payload).Name == "A");
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── P5 — saga: deterministic failure re-raise + exactly-once side effect (B10b) ────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P5_Saga_ReplayedFailureReRaises_ClosureNotReExecuted()
    {
        var probeId = ProbeId();

        // Attempt 1: the "book" closure throws TerminalException once; the SDK proposes a failure.
        // Close input after the proposal+sleep so the catch's sleep parks (suspension), WITHOUT
        // delivering the run-failure ack — it lands in the resume batch instead.
        var first = await RunFirstAttemptAsync(typeof(SagaCompensationService), "Book", Json(probeId),
            async script =>
            {
                // The book closure throws, so the SDK emits a FAILURE proposal; ack it (mirroring the
                // failure) so the blocking Run re-raises, the catch is entered, and the catch's sleep
                // parks → suspension. The run-FAILURE replay then lands in the resume batch (B10b).
                var proposal = await script.WaitForAsync(MessageType.ProposeRunCompletion);
                await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(proposal));
                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            });
        Assert.True(first.Suspended);
        Assert.Equal(1, ReplayProbe.Get(probeId, "run:book"));   // closure ran exactly once on attempt 1

        var bookId = CompletionIdOf(first.Commands, MessageType.RunCommand, "book");
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);

        // Attempt 2: replay RunCommand{book} + SleepCommand verbatim with a RunFailure ack and the
        // sleep completion. The catch is reached ONLY if the replayed failed Run re-raises the
        // TerminalException from the journaled failure (B10b). The compensate Run then proposes.
        var resume = await RunResumeAttemptAsync(typeof(SagaCompensationService), "Book", Json(probeId),
            commands: first.Commands.Where(c => c.Type is MessageType.RunCommand or MessageType.SleepCommand).ToArray(),
            notifications: [RunFailure(bookId, 500, "no rooms"), SleepCompletion(sleepId)],
            script: async script =>
            {
                // Release the compensate proposal that attempt 2 emits, then let it complete.
                var compensate = await script.WaitForFrameAsync(f =>
                    f.Type == MessageType.ProposeRunCompletion &&
                    Gen.ProposeRunCompletionMessage.Parser.ParseFrom(f.Payload).ResultCompletionId != bookId);
                await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(compensate));
                script.CloseInput();
            });

        Assert.False(resume.Suspended);
        Assert.Equal("compensated", DecodeJsonString(resume.Output!));
        Assert.Equal(1, ReplayProbe.Get(probeId, "run:book"));        // NOT re-executed on replay
        Assert.Equal(1, ReplayProbe.Get(probeId, "run:compensate"));
        // No SECOND ProposeRunCompletion for the book id — the failed closure is not re-run.
        Assert.DoesNotContain(resume.Commands, c =>
            c.Type == MessageType.ProposeRunCompletion &&
            Gen.ProposeRunCompletionMessage.Parser.ParseFrom(c.Payload).ResultCompletionId == bookId);
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── P6 — lazy send: invocation id from notification, not command bytes (B6) ─────────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P6_LazySend_InvocationIdFromNotification()
    {
        var probeId = ProbeId();

        var first = await RunFirstAttemptAsync(typeof(LazySendService), "SendAndReport", Json(probeId),
            async script =>
            {
                await script.WaitForAsync(MessageType.OneWayCallCommand);
                await script.WaitForAsync(MessageType.SleepCommand);
                script.CloseInput();
            });
        Assert.True(first.Suspended);

        // The send-handle resolves its invocation id by parking on the OneWayCall's
        // invocation_id_notification_idx — parsed from the recorded command, answered by a
        // CallInvocationId notification. Pre-fix B6 fed the command protobuf bytes here instead.
        var idIdx = InvocationIdIdxOf(first.Commands, MessageType.OneWayCallCommand);
        var sleepId = CompletionIdOf(first.Commands, MessageType.SleepCommand);
        var resume = await RunResumeAttemptAsync(typeof(LazySendService), "SendAndReport", Json(probeId),
            commands: first.Commands.Where(c => c.Type is MessageType.OneWayCallCommand or MessageType.SleepCommand).ToArray(),
            notifications: [CallInvocationId(idIdx, "inv_test123"), SleepCompletion(sleepId)]);

        Assert.False(resume.Suspended);
        Assert.Equal("inv_test123", DecodeJsonString(resume.Output!));
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── P7 — frame-order audit: no frame after Suspension on every attempt-1 stream (B8) ────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P7_EveryAttempt1Stream_PassesFrameOrderAudit()
    {
        // Drive the suspending scenarios' attempt 1 and assert AssertFrameOrder: each header valid,
        // no frame follows a Suspension, End (if present) is last. This is the wire-order invariant
        // (B8) at the recorded-stream level for the full handler stack.
        var streams = new List<IReadOnlyList<RecordedFrame>>
        {
            (await RunFirstAttemptAsync(typeof(RunSleepRunService), "Execute", Json(ProbeId()),
                async script =>
                {
                    var proposal = await script.WaitForAsync(MessageType.ProposeRunCompletion);
                    await script.DeliverAsync(MessageType.RunCompletion, CreateRunCompletionFrom(proposal));
                    await script.WaitForAsync(MessageType.SleepCommand);
                    script.CloseInput();
                })).Frames,
            (await RunFirstAttemptAsync(typeof(CallAcrossSuspensionService), "Relay", Json(ProbeId()),
                async script =>
                {
                    await script.WaitForAsync(MessageType.CallCommand);
                    script.CloseInput();
                })).Frames,
            (await RunFirstAttemptAsync(typeof(ApprovalWorkflow), "Run", Json(ProbeId()),
                async script =>
                {
                    await script.WaitForAsync(MessageType.GetPromiseCommand);
                    script.CloseInput();
                }, key: "wf-p7")).Frames
        };

        foreach (var stream in streams)
        {
            Assert.Equal(MessageType.Suspension, stream[^1].Type);
            ProtocolTestHarnessAssertFrameOrder(stream);
        }
    }

    // ── P8 — Call across suspension: one wire command consumes TWO completion ids (B1) ─────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P8_CallAcrossSuspension_TwoIdsHonoredByValue()
    {
        var probeId = ProbeId();

        // Attempt 1: close input right after the CallCommand flushes, delivering NO notifications —
        // the caller parks on the call's result id.
        var first = await RunFirstAttemptAsync(typeof(CallAcrossSuspensionService), "Relay", Json(probeId),
            async script =>
            {
                await script.WaitForAsync(MessageType.CallCommand);
                script.CloseInput();
            });
        Assert.True(first.Suspended);

        // The CallCommand carries BOTH ids: result_completion_id (the park id) and
        // invocation_id_notification_idx. BOTH are parsed from the recorded bytes — never by position.
        var call = ParseCommand(first.Commands, MessageType.CallCommand);
        Assert.Contains(call.ResultCompletionId, first.Suspension!.WaitingCompletions);

        var resume = await RunResumeAttemptAsync(typeof(CallAcrossSuspensionService), "Relay", Json(probeId),
            commands: first.Commands.Where(c => c.Type == MessageType.CallCommand).ToArray(),
            notifications:
            [
                CallInvocationId(call.InvocationIdNotificationIdx, "inv_p8"),
                CallCompletion(call.ResultCompletionId, "pong")
            ]);

        Assert.False(resume.Suspended);
        Assert.Equal("pong", DecodeJsonString(resume.Output!));
        // No SECOND CallCommand — the replayed one consumed both ids.
        Assert.Single(first.Commands, c => c.Type == MessageType.CallCommand);
        Assert.DoesNotContain(resume.Commands, c => c.Type == MessageType.CallCommand);
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── P9 — workflow promise replay through the full handler stack (B1/B2/B8) ─────────────────

    [Fact(Timeout = WatchdogMs)]
    public async Task P9_WorkflowPromiseReplay_ResumesWithResolvedValue()
    {
        var probeId = ProbeId();

        var first = await RunFirstAttemptAsync(typeof(ApprovalWorkflow), "Run", Json(probeId),
            async script =>
            {
                await script.WaitForAsync(MessageType.GetPromiseCommand);
                script.CloseInput();
            }, key: "wf-p9");
        Assert.True(first.Suspended);

        var promiseId = CompletionIdOf(first.Commands, MessageType.GetPromiseCommand, "approval");
        Assert.Contains(promiseId, first.Suspension!.WaitingCompletions);

        // Resume: replay GetPromiseCommand{approval} verbatim + a PromiseCompletion("yes"). The
        // Template A promise replay must yield "approved:yes" through the full handler stack, with no
        // second GetPromiseCommand emitted.
        var resume = await RunResumeAttemptAsync(typeof(ApprovalWorkflow), "Run", Json(probeId),
            commands: first.Commands.Where(c => c.Type == MessageType.GetPromiseCommand).ToArray(),
            notifications: [PromiseCompletion(promiseId, "yes")],
            key: "wf-p9");

        Assert.False(resume.Suspended);
        Assert.Equal("approved:yes", DecodeJsonString(resume.Output!));
        Assert.DoesNotContain(resume.Commands, c => c.Type == MessageType.GetPromiseCommand);
        Assert.True(ReplayProbe.Get(probeId, "attempt") >= 2);
    }

    // ── local helpers ──────────────────────────────────────────────────────────────────────────

    // Mirrors a recorded ProposeRunCompletion into the matching RunCompletion notification: the
    // runtime acks a proposed run by echoing its id+outcome back as a completion. A FAILURE proposal
    // (the saga's "book" run) is echoed as a failure completion so the blocking Run re-raises (B10b).
    private static Gen.RunCompletionNotificationMessage CreateRunCompletionFrom(RecordedFrame proposalFrame) =>
        RunCompletionFor(Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposalFrame.Payload));

    private static Gen.RunCompletionNotificationMessage RunCompletionFor(Gen.ProposeRunCompletionMessage proposal) =>
        proposal.ResultCase == Gen.ProposeRunCompletionMessage.ResultOneofCase.Failure
            ? new Gen.RunCompletionNotificationMessage
            {
                CompletionId = proposal.ResultCompletionId,
                Failure = new Gen.Failure { Code = proposal.Failure.Code, Message = proposal.Failure.Message }
            }
            : new Gen.RunCompletionNotificationMessage
            {
                CompletionId = proposal.ResultCompletionId,
                Value = new Gen.Value { Content = proposal.Value }
            };

    private static Gen.GetLazyStateCompletionNotificationMessage GetStateAbsentFor(uint completionId) =>
        new() { CompletionId = completionId, Void = new Gen.Void() };

    // AssertFrameOrder works on a raw byte stream; re-frame the recorded frames and delegate so P7
    // reuses the exact §4 preamble wire-order invariant (no frame after Suspension, End last).
    private static void ProtocolTestHarnessAssertFrameOrder(IReadOnlyList<RecordedFrame> frames)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Span<byte> header = stackalloc byte[MessageHeader.Size];
        foreach (var frame in frames)
        {
            MessageHeader.Create(frame.Type, MessageFlags.None, (uint)frame.Payload.Length).Write(header);
            buffer.Write(header);
            buffer.Write(frame.Payload);
        }

        ProtocolTestHarness.AssertFrameOrder(buffer.WrittenSpan.ToArray());
    }
}
