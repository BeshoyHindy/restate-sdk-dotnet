using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Hosting;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Google.Protobuf;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests;

[Service(Name = "HostingGreeter")]
public class HostingGreeterService
{
    [Handler]
    public Task<string> Greet(Context ctx, string name) => Task.FromResult($"Hello {name}");
}

[VirtualObject(Name = "HostingCounter")]
public class HostingCounterObject
{
    [Handler]
    public Task<int> Add(ObjectContext ctx, int delta) => Task.FromResult(delta);
}

[Workflow(Name = "HostingFlow")]
public class HostingWorkflow
{
    [Handler]
    public Task<string> Run(WorkflowContext ctx) => Task.FromResult("done");
}

/// <summary>
///     Plan 07 §1.3 hosting-client lane. Covers the ASP.NET Core hosting surface that the §2 E2E
///     suite exercises against a real runtime, but here through the in-memory
///     <see cref="TestServer" /> and direct option resolution so the gate runs docker-free:
///       * <see cref="RestateHostBuilder" /> fluent registration + <c>Build</c>/<c>BuildAot</c>;
///       * <see cref="RestateWebHostBuilderExtensions.ConfigureRestate" /> Kestrel tuning (asserted
///         by resolving the configured <see cref="KestrelServerOptions" /> — no socket bind needed);
///       * the <c>MapRestate</c> invocation-dispatch arms (400 empty name, 404 missing
///         service/handler, 200 happy path) that the identity E2E tests do not reach;
///       * <see cref="ServiceDefinitionRegistry.Get{T}" /> + missing-definition throw;
///       * <see cref="ClientFactory.Create{T}" /> unregistered throw.
/// </summary>
public class HostingTests
{
    // ---- RestateHostBuilder ------------------------------------------------------------------

    [Fact]
    public void CreateBuilder_ReturnsBuilder()
    {
        Assert.NotNull(RestateHost.CreateBuilder());
    }

    [Fact]
    public void Builder_FluentRegistration_IsChainable()
    {
        // Bind / AddService / AddVirtualObject / AddWorkflow / WithPort all return the builder.
        var builder = RestateHost.CreateBuilder()
            .Bind<HostingGreeterService>()
            .AddService<HostingGreeterService>()
            .AddVirtualObject<HostingCounterObject>()
            .AddWorkflow<HostingWorkflow>()
            .WithPort(9123);

        Assert.NotNull(builder);
    }

    [Fact]
    public void Build_ConstructsWebApplication_WithRestateRegistered()
    {
        // Build() wires Kestrel + AddRestate + MapRestate. We construct (not Run) the app so no
        // socket binds, then prove the DI graph is present: the ServiceRegistry resolved here is
        // the same one MapRestate consumed.
#pragma warning disable IL2026, IL3050 // Build() uses reflection-based DI; acceptable in tests.
        using var app = RestateHost.CreateBuilder()
            .AddService<HostingGreeterService>()
            .WithPort(0)
            .Build();
#pragma warning restore IL2026, IL3050

        var registry = app.Services.GetRequiredService<ServiceRegistry>();
        Assert.True(registry.TryGetService("HostingGreeter", out _));
    }

    [Fact]
    public void BuildAot_UsesProvidedServiceConfiguration()
    {
        // BuildAot delegates registration to the caller's callback (the source-generated
        // AddRestateGenerated in production). Here we feed AddRestateAot directly with a
        // hand-resolved definition so the AOT path is exercised without the generator.
        var def = ServiceDefinitionRegistry.Get<HostingGreeterService>();

        using var app = RestateHost.CreateBuilder()
            .WithPort(0)
            .BuildAot(services => services.AddRestateAot([def]));

        var registry = app.Services.GetRequiredService<ServiceRegistry>();
        Assert.True(registry.TryGetService("HostingGreeter", out _));
    }

    // ---- RestateWebHostBuilderExtensions.ConfigureRestate ------------------------------------

    [Fact]
    public void ConfigureRestate_AppliesKestrelTuning()
    {
        // ConfigureKestrel registers an IConfigureOptions<KestrelServerOptions>; resolving the
        // options runs the callback (all 18 lines) WITHOUT binding the listen socket. We assert
        // every limit the method sets so a future regression that drops one is caught.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureRestate(7777);
        using var app = builder.Build();

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.Null(kestrel.Limits.MinRequestBodyDataRate);
        Assert.Null(kestrel.Limits.MaxRequestBodySize);
        Assert.Equal(1024 * 1024, kestrel.Limits.Http2.InitialConnectionWindowSize);
        Assert.Equal(512 * 1024, kestrel.Limits.Http2.InitialStreamWindowSize);
    }

    [Fact]
    public void ConfigureRestate_ReturnsSameBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        var returned = builder.WebHost.ConfigureRestate();
        Assert.Same(builder.WebHost, returned);
    }

    // ---- MapRestate invocation dispatch (via TestServer) -------------------------------------

    private static async Task<IHost> StartInvokeHostAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
#pragma warning disable IL2026 // AddRestate uses reflection; acceptable in tests.
                        services.AddRestate(options => options.AddService<HostingGreeterService>());
#pragma warning restore IL2026
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapRestate());
                    });
            })
            .StartAsync();
    }

    private static byte[] FramedInvokeBody(string argument)
    {
        // Start{known_entries=1} + InputCommand carrying the JSON argument — the minimal request
        // the runtime sends for a side-effect-free handler that completes immediately.
        var start = CreateStartMessage("hosting-inv-1", knownEntries: 1, key: "k").ToByteArray();
        var input = CreateInputCommand(JsonSerializer.SerializeToUtf8Bytes(argument)).ToByteArray();
        using var stream = BuildRequestStream(start, input);
        return stream.ToArray();
    }

    [Fact]
    public async Task Invoke_KnownHandler_Returns200AndOutputFrame()
    {
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsync("/invoke/HostingGreeter/Greet",
            new ByteArrayContent(FramedInvokeBody("World")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.restate.invocation.v6",
            response.Content.Headers.ContentType?.ToString());

        // The streamed body carries an OutputCommand whose value is the JSON result.
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length > 0);
        var offset = 0;
        var (header, payload) = ReadFramedMessage(body, ref offset);
        Assert.Equal(MessageType.OutputCommand, header.Type);
        var output = Gen.OutputCommandMessage.Parser.ParseFrom(payload);
        Assert.Equal("\"Hello World\"", Encoding.UTF8.GetString(output.Value.Content.ToByteArray()));
    }

    [Fact]
    public async Task Invoke_UnknownService_Returns404()
    {
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsync("/invoke/NoSuchService/Greet",
            new ByteArrayContent(FramedInvokeBody("x")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("NoSuchService", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invoke_UnknownHandler_Returns404()
    {
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsync("/invoke/HostingGreeter/NoSuchHandler",
            new ByteArrayContent(FramedInvokeBody("x")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("NoSuchHandler", text);
        Assert.Contains("HostingGreeter", text);
    }

    // ---- MapRestate /discover negotiation (via TestServer) -----------------------------------

    [Fact]
    public async Task Discover_SupportedAccept_Returns200WithManifestAndServerHeader()
    {
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/discover");
        request.Headers.TryAddWithoutValidation("Accept",
            "application/vnd.restate.endpointmanifest.v3+json");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.restate.endpointmanifest.v3+json",
            response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.Contains("x-restate-server"));

        // The body is the JSON endpoint manifest carrying the registered service.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("HostingGreeter", body);
    }

    [Fact]
    public async Task Discover_UnsupportedAccept_Returns415()
    {
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        // A v4-only Accept has no mutually supported version → NegotiateVersion returns null → 415.
        var request = new HttpRequestMessage(HttpMethod.Get, "/discover");
        request.Headers.TryAddWithoutValidation("Accept",
            "application/vnd.restate.endpointmanifest.v4+json");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_MalformedStream_HandledGracefully_ClosesStream()
    {
        // A body that is NOT a valid Start frame makes HandleAsync fault after StartAsync begins
        // streaming; MapRestate's catch-around-HandleAsync swallows it and lets Kestrel close the
        // HTTP/2 stream. The request must still complete (no unhandled exception escapes the endpoint).
        using var host = await StartInvokeHostAsync();
        using var client = host.GetTestClient();

        var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x02 };
        var response = await client.PostAsync("/invoke/HostingGreeter/Greet",
            new ByteArrayContent(garbage));

        // The endpoint started the 200 stream before HandleAsync ran; the catch prevents a 500 leak.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Draining the (truncated/closed) body must not throw out of the test.
        _ = await response.Content.ReadAsByteArrayAsync();
    }

    // ---- RestateOptions registration helpers + WorkflowAttribute ------------------------------

    [Fact]
    public void RestateOptions_AddService_AddVirtualObject_AddWorkflow_AllBindAndChain()
    {
        // The three typed registration helpers each delegate to Bind<T>, accumulating the service
        // type and returning the same options for chaining.
        var options = new RestateOptions()
            .AddService<HostingGreeterService>()
            .AddVirtualObject<HostingCounterObject>()
            .AddWorkflow<HostingWorkflow>();

        Assert.Contains(typeof(HostingGreeterService), options.ServiceTypes);
        Assert.Contains(typeof(HostingCounterObject), options.ServiceTypes);
        Assert.Contains(typeof(HostingWorkflow), options.ServiceTypes);
    }

    [Fact]
    public void WorkflowAttribute_Name_And_Retention_RoundTrip()
    {
        // The attribute's optional Name / WorkflowRetention setters and getters are only read by the
        // source generator at build time; assert them directly so the property bodies are covered.
        var attribute = new WorkflowAttribute { Name = "MyFlow", WorkflowRetention = "1.00:00:00" };
        Assert.Equal("MyFlow", attribute.Name);
        Assert.Equal("1.00:00:00", attribute.WorkflowRetention);
    }

    // ---- ServiceDefinitionRegistry.Get<T> ----------------------------------------------------

    [Fact]
    public void ServiceDefinitionRegistry_Get_ReturnsRegisteredDefinition()
    {
        // The source generator registered HostingGreeterService at module init; Get<T> resolves it.
        var def = ServiceDefinitionRegistry.Get<HostingGreeterService>();
        Assert.Equal("HostingGreeter", def.Name);
    }

    private sealed class UnregisteredService;

    [Fact]
    public void ServiceDefinitionRegistry_Get_ThrowsForUnregisteredType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            ServiceDefinitionRegistry.Get<UnregisteredService>);
        Assert.Contains("UnregisteredService", ex.Message);
    }

    // ---- ClientFactory.Create ----------------------------------------------------------------

    private interface IUnregisteredClient;

    [Fact]
    public void ClientFactory_Create_ThrowsWhenNoFactoryRegistered_OrTypeMismatch()
    {
        // No generated factory yields IUnregisteredClient, so Create must throw NotSupported.
        // (If some other test registered a factory, it returns a non-matching type and Create
        // still throws via the same arm — either way the unregistered/mismatch path is exercised.)
        var ex = Assert.Throws<NotSupportedException>(() =>
            ClientFactory.Create<IUnregisteredClient>(context: null!));
        Assert.Contains("IUnregisteredClient", ex.Message);
    }
}
