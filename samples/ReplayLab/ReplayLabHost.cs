using Microsoft.AspNetCore.Builder;
using Restate.Sdk.Hosting;

namespace ReplayLab;

/// <summary>
///     Shared composition root for the ReplayLab endpoint. Both entry points use it: the
///     standalone <c>Program.cs</c> (runnable by hand and by the integration-test smoke script)
///     and the E2E test fixture, which hosts the same services in-process on an ephemeral port so
///     its tests get direct read access to <see cref="ExecutionProbe" /> while a real
///     restate-server container drives them via <c>host.docker.internal</c>. Registering every
///     service here once keeps the two entry points from drifting.
/// </summary>
public static class ReplayLabHost
{
    /// <summary>
    ///     Builds the ReplayLab <see cref="WebApplication" /> bound to <paramref name="port" />
    ///     (use 0 for an OS-assigned ephemeral port), with every replay-bait service registered.
    /// </summary>
    public static WebApplication Build(int port)
    {
        return RestateHost.CreateBuilder()
            .WithPort(port)
            // B1/B2/B3/B8 — Run → Sleep → Run.
            .AddService<RunSleepRunService>()
            // B4/B8/B9 — two awakeables awaited out of order.
            .AddService<AwakeablePairService>()
            // B5 — 16-way jittered fan-out of Runs.
            .AddService<FanOutRunsService>()
            // B7 — partial-state VirtualObject across suspension.
            .AddVirtualObject<PartialStateCounterObject>()
            // B10b — failed Run re-raise on replay + compensation.
            .AddService<SagaCompensationService>()
            // B6 — lazy send id across suspension, with its slow echo target.
            .AddService<LazySendService>()
            .AddService<SlowEchoService>()
            // B1 two-id Call model across suspension (shares SlowEchoService as the target).
            .AddService<CallAcrossSuspensionService>()
            // Promise replay sites — workflow suspend on promise, shared-handler resolve.
            .AddWorkflow<ApprovalWorkflow>()
            // Out-of-process probe readout for the standalone sample.
            .AddService<ProbeService>()
            .Build();
    }
}
