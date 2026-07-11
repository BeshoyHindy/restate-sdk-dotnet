using System.Diagnostics;
using Restate.Sdk.Internal;

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
}
