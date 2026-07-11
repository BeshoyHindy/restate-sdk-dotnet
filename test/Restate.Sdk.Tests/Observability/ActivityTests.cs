using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Observability;

/// <summary>
///     Verifies span enrichment on the <c>Restate.Sdk</c> ActivitySource: invocation-level
///     tags (service/handler/invocation id/journal stats) and the opt-in per-operation
///     child activities. Activities from other test classes running in parallel are
///     filtered out by display name / trace id.
/// </summary>
public class ActivityTests
{
    private static ActivityListener CreateListener(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Restate.Sdk",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (stopped)
                {
                    stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static List<Activity> Snapshot(List<Activity> stopped)
    {
        lock (stopped)
        {
            return [.. stopped];
        }
    }

    [Fact]
    public async Task Invocation_EnrichesActivityWithServiceHandlerAndJournalTags()
    {
        var stopped = new List<Activity>();
        using (CreateListener(stopped))
        {
            await ObservabilityTestDriver.DriveAsync<ObsActivityGreeterService>(
                "Greet", "World", new InvocationHandler(), "obs-act-1");
        }

        var activity = Assert.Single(Snapshot(stopped), a => a.DisplayName == "ObsActivityGreeter/Greet");

        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("ObsActivityGreeter", activity.GetTagItem("restate.service"));
        Assert.Equal("Greet", activity.GetTagItem("restate.handler"));
        Assert.Equal("obs-act-1", activity.GetTagItem("restate.invocation.id"));
        Assert.Equal("restate", activity.GetTagItem("rpc.system"));

        // Post-completion tags: journal holds the input command only; nothing was replayed.
        Assert.Equal(1, activity.GetTagItem("restate.journal.commands"));
        Assert.Equal(false, activity.GetTagItem("restate.replayed"));
    }

    [Fact]
    public async Task OperationActivities_CreatedAsChildrenWhenEnabled()
    {
        var stopped = new List<Activity>();
        using (CreateListener(stopped))
        {
            var handler = new InvocationHandler(
                telemetryOptions: new RestateTelemetryOptions { EnableOperationActivities = true });
            await ObservabilityTestDriver.DriveAsync<ObsActivityRunnerService>(
                "Compute", "input", handler, "obs-act-2");
        }

        var activities = Snapshot(stopped);
        var invocation = Assert.Single(activities, a =>
            a.DisplayName == "ObsActivityRunner/Compute" && Equals(a.GetTagItem("restate.invocation.id"), "obs-act-2"));
        var run = Assert.Single(activities, a =>
            a.OperationName == "restate.run" && a.TraceId == invocation.TraceId);

        Assert.Equal("side-effect", run.GetTagItem("restate.run.name"));
        Assert.Equal(invocation.SpanId, run.ParentSpanId);

        // Journal now holds the input command + the run command.
        Assert.Equal(2, invocation.GetTagItem("restate.journal.commands"));
    }

    [Fact]
    public async Task OperationActivities_NotCreatedByDefault()
    {
        var stopped = new List<Activity>();
        using (CreateListener(stopped))
        {
            await ObservabilityTestDriver.DriveAsync<ObsActivityRunnerService>(
                "Compute", "input", new InvocationHandler(), "obs-act-3");
        }

        var activities = Snapshot(stopped);
        var invocation = Assert.Single(activities, a =>
            a.DisplayName == "ObsActivityRunner/Compute" && Equals(a.GetTagItem("restate.invocation.id"), "obs-act-3"));

        Assert.DoesNotContain(activities, a =>
            a.OperationName == "restate.run" && a.TraceId == invocation.TraceId);
    }

    [Fact]
    public async Task RunSync_PendingFlush_DoesNotLeakOperationActivityAsCurrent()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        // Outbound pipe with a 1-byte pause threshold: the first flush cannot complete
        // until the reader drains, forcing RunSync's pending-flush tail (the path taken
        // under real Kestrel backpressure).
        var outbound = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        var inbound = new Pipe();
        var reader = new ProtocolReader(inbound.Reader);
        var writer = new ProtocolWriter(outbound.Writer);
        using var sm = new InvocationStateMachine(reader, writer, enableOperationActivities: true);
        sm.Initialize("obs-act-4", "key", 1, 0);

        using var invocation = InvocationHandler.ActivitySource.StartActivity("obs-act-4-invocation");
        Assert.NotNull(invocation);

        var pending = sm.RunSync("side-effect", () => 42, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        // RunSync runs synchronously on the caller's execution context, so the span it
        // started became Activity.Current here. Once ownership moves to the async flush
        // tail, the caller's Current must be restored — otherwise every later span in
        // this handler parents under a stopped "restate.run" activity.
        Assert.Same(invocation, Activity.Current);

        // Drain the outbound pipe so the flush — and with it the Run — completes.
        var drained = await outbound.Reader.ReadAsync();
        outbound.Reader.AdvanceTo(drained.Buffer.End);

        Assert.Equal(42, await pending);

        var run = Assert.Single(Snapshot(stopped), a =>
            a.OperationName == "restate.run" && a.TraceId == invocation!.TraceId);
        Assert.Equal(invocation!.SpanId, run.ParentSpanId);

        inbound.Writer.Complete();
        inbound.Reader.Complete();
        outbound.Writer.Complete();
        outbound.Reader.Complete();
    }

    [Fact]
    public async Task CallActivity_SpansAsyncCompletion_WithRpcTargetTags()
    {
        var stopped = new List<Activity>();
        using var listener = CreateListener(stopped);

        var inbound = new Pipe();
        var outbound = new Pipe();
        var reader = new ProtocolReader(inbound.Reader);
        var writer = new ProtocolWriter(outbound.Writer);
        using var sm = new InvocationStateMachine(reader, writer, enableOperationActivities: true);
        sm.Initialize("obs-act-5", "key", 1, 0);

        // The concurrent incoming-message reader delivers the call completion,
        // exactly as InvocationHandler runs it during a real invocation.
        using var incomingCts = new CancellationTokenSource();
        var incomingTask = sm.ProcessIncomingMessagesAsync(incomingCts.Token);

        using var invocation = InvocationHandler.ActivitySource.StartActivity("obs-act-5-invocation");
        Assert.NotNull(invocation);

        var call = sm.CallAsync<string>("OtherService", null, "Echo", "ping", CancellationToken.None);

        // CallAsync appends the dummy invocation-id notification slot at index 0,
        // so the call itself awaits completion id 1.
        var notification = new Gen.NotificationTemplate
        {
            CompletionId = 1,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("pong")) }
        };
        await inbound.Writer.WriteAsync(Frame(MessageType.CallCompletion, notification.ToByteArray()));

        Assert.Equal("pong", await call);

        var callActivity = Assert.Single(Snapshot(stopped), a =>
            a.OperationName == "restate.call" && a.TraceId == invocation!.TraceId);
        Assert.Equal("OtherService", callActivity.GetTagItem("rpc.service"));
        Assert.Equal("Echo", callActivity.GetTagItem("rpc.method"));
        Assert.Equal(invocation!.SpanId, callActivity.ParentSpanId);

        await incomingCts.CancelAsync();
        try
        {
            await incomingTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: the reader's pending ReadAsync observes the cancellation.
        }

        inbound.Writer.Complete();
        inbound.Reader.Complete();
        outbound.Writer.Complete();
        outbound.Reader.Complete();
    }

    private static byte[] Frame(MessageType type, byte[] payload)
    {
        var frame = new byte[MessageHeader.Size + payload.Length];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(frame);
        payload.CopyTo(frame.AsSpan(MessageHeader.Size));
        return frame;
    }
}
