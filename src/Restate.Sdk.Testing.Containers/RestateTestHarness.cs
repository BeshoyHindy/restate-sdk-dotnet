using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Client;
using Restate.Sdk.Hosting;

namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     One-call integration test harness: starts your Restate services as a local SDK
///     endpoint (on an ephemeral port), starts a Restate server container, registers the
///     endpoint as a deployment, and exposes an ingress <see cref="Client" /> — ready to
///     invoke handlers end-to-end against a real Restate server.
/// </summary>
/// <remarks>
///     This package intentionally does not reference a test framework. With xUnit, wrap the
///     harness in an <c>IAsyncLifetime</c> class fixture so one container serves all tests
///     in a class:
///     <code>
///     public sealed class HarnessFixture : IAsyncLifetime
///     {
///         public RestateTestHarness Harness { get; private set; } = null!;
///
///         public async Task InitializeAsync()
///         {
///             Harness = await RestateTestHarness.StartAsync(b => b.AddService&lt;GreeterService&gt;());
///         }
///
///         public async Task DisposeAsync()
///         {
///             await Harness.DisposeAsync();
///         }
///     }
///
///     public sealed class GreeterTests : IClassFixture&lt;HarnessFixture&gt;
///     {
///         private readonly HarnessFixture _fixture;
///
///         public GreeterTests(HarnessFixture fixture)
///         {
///             _fixture = fixture;
///         }
///
///         [Fact]
///         public async Task Greet_round_trips()
///         {
///             var reply = await _fixture.Harness.Client
///                 .Service("GreeterService")
///                 .Call&lt;string&gt;("Greet", "World");
///         }
///     }
///     </code>
/// </remarks>
public sealed class RestateTestHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RestateTestHarness(WebApplication app, RestateContainer container, RestateClient client)
    {
        _app = app;
        Container = container;
        Client = client;
    }

    /// <summary>Gets the ingress client pointing at the containerized Restate server.</summary>
    public RestateClient Client { get; }

    /// <summary>Gets the running Restate server container.</summary>
    public RestateContainer Container { get; }

    /// <summary>
    ///     Stops the SDK endpoint first, then the Restate container, and disposes the ingress client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Always reap the container, even when stopping the SDK endpoint throws;
            // otherwise it would keep running until the resource reaper catches up.
            await Container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Starts the harness: builds and starts the SDK endpoint on an ephemeral port,
    ///     starts a Restate server container, registers the endpoint as a deployment, and
    ///     waits until it is active.
    /// </summary>
    /// <param name="configure">
    ///     Configures the endpoint, e.g. <c>b =&gt; b.AddService&lt;Greeter&gt;()</c>.
    /// </param>
    /// <param name="options">Optional harness options (image, startup timeout).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A running harness; dispose it to tear everything down.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure" /> is <see langword="null" />.</exception>
    /// <exception cref="TimeoutException">
    ///     Startup did not complete within <see cref="RestateTestHarnessOptions.StartupTimeout" />.
    /// </exception>
    [RequiresUnreferencedCode("The Restate endpoint host uses reflection-based DI and JSON serialization.")]
    [RequiresDynamicCode("The Restate endpoint host uses reflection-based DI and JSON serialization.")]
    public static async Task<RestateTestHarness> StartAsync(
        Action<RestateHostBuilder> configure,
        RestateTestHarnessOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        options ??= new RestateTestHarnessOptions();

        var hostBuilder = RestateHost.CreateBuilder().WithPort(0);
        configure(hostBuilder);

        var app = hostBuilder.Build();
        RestateContainer? container = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.StartupTimeout);
            var token = timeoutCts.Token;

            await app.StartAsync(token).ConfigureAwait(false);
            var hostPort = GetBoundPort(app);

            // The port-forwarding container must be running before the RestateBuilder is
            // constructed: the host.testcontainers.internal extra-host entry is injected
            // into the container configuration at builder-initialization time.
            await HostPortForwarding.EnsureExposedAsync((ushort)hostPort, token).ConfigureAwait(false);

            container = new RestateBuilder(options.Image).Build();
            await container.StartAsync(token).ConfigureAwait(false);
            await container.RegisterDeploymentAsync(hostPort, token).ConfigureAwait(false);

            var client = container.CreateIngressClient();
            return new RestateTestHarness(app, container, client);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await CleanUpAsync(app, container).ConfigureAwait(false);
            throw new TimeoutException($"The Restate test harness did not start within {options.StartupTimeout}.");
        }
        catch
        {
            await CleanUpAsync(app, container).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CleanUpAsync(WebApplication app, RestateContainer? container)
    {
        if (container is not null)
            await container.DisposeAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private static int GetBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        var address = addresses?.FirstOrDefault();
        if (address is null)
            throw new InvalidOperationException("The Restate endpoint host did not report a bound address.");

        var port = BindingAddress.Parse(address).Port;
        if (port == 0)
            throw new InvalidOperationException($"The Restate endpoint host reported an unresolved port for address '{address}'.");

        return port;
    }
}
