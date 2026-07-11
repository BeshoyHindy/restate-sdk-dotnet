using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Hosting;

namespace Restate.Sdk.Tests.Endpoint;

/// <summary>
///     Every hosting path must be able to enable per-operation activities: AddRestate (via
///     <see cref="RestateOptions.Telemetry" />), RestateHostBuilder.Build, BuildAot, and Lambda.
/// </summary>
public class TelemetryConfigurationTests
{
    [Fact]
    public async Task HostBuilder_ConfigureTelemetry_FlowsIntoBuiltApp()
    {
        var app = RestateHost.CreateBuilder()
            .Bind<GreeterService>()
            .ConfigureTelemetry(t => t.EnableOperationActivities = true)
            .Build();
        await using (app.ConfigureAwait(false))
        {
            var telemetry = app.Services.GetRequiredService<RestateTelemetryOptions>();
            Assert.True(telemetry.EnableOperationActivities);
        }
    }

    [Fact]
    public async Task HostBuilder_ConfigureTelemetry_FlowsIntoAotApp()
    {
        var app = RestateHost.CreateBuilder()
            .ConfigureTelemetry(t => t.EnableOperationActivities = true)
            .BuildAot(services => services.AddRestateAot([]));
        await using (app.ConfigureAwait(false))
        {
            var telemetry = app.Services.GetRequiredService<RestateTelemetryOptions>();
            Assert.True(telemetry.EnableOperationActivities);
        }
    }

    [Fact]
    public void HostBuilder_ConfigureTelemetry_NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RestateHost.CreateBuilder().ConfigureTelemetry(null!));
    }

    [Fact]
    public void Lambda_ConfigureTelemetry_FromRegister_Succeeds()
    {
        _ = new TelemetryHandler();
    }

    [Fact]
    public void Lambda_ConfigureTelemetry_AfterConstruction_Throws()
    {
        // Late configuration would be silently ignored — the pipeline was already built.
        Assert.Throws<InvalidOperationException>(() => new LateTelemetryHandler());
    }

    private sealed class TelemetryHandler : RestateLambdaHandler
    {
        public override void Register()
        {
            Bind<GreeterService>();
            ConfigureTelemetry(t => t.EnableOperationActivities = true);
        }
    }

    private sealed class LateTelemetryHandler : RestateLambdaHandler
    {
        public LateTelemetryHandler()
        {
            ConfigureTelemetry(t => t.EnableOperationActivities = true);
        }

        public override void Register()
        {
            Bind<GreeterService>();
        }
    }
}
