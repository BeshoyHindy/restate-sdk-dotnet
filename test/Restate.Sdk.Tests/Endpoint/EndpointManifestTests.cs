using System.Text.Json;
using System.Text.Json.Serialization;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Hosting;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Discovery;

namespace Restate.Sdk.Tests.Endpoint;

public class EndpointManifestTests
{
    [Fact]
    public void FromRegistry_IncludesAllServices()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);

        Assert.Equal(2, manifest.Services.Count);
        Assert.Equal("BIDI_STREAM", manifest.ProtocolMode);
        Assert.Equal(5, manifest.MinProtocolVersion);
        Assert.Equal(6, manifest.MaxProtocolVersion);
    }

    [Fact]
    public void ServiceManifest_MapsServiceType()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!);
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(OrderWorkflow))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);

        var service = manifest.Services.First(s => s.Name == "GreeterService");
        Assert.Equal("SERVICE", service.Type);

        var obj = manifest.Services.First(s => s.Name == "CounterObject");
        Assert.Equal("VIRTUAL_OBJECT", obj.Type);

        var workflow = manifest.Services.First(s => s.Name == "OrderWorkflow");
        Assert.Equal("WORKFLOW", workflow.Type);
    }

    [Fact]
    public void HandlerManifest_SetsTypeForKeyedServices()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!;
        var addManifest = HandlerManifest.FromDefinition(
            def.Handlers.First(h => h.Name == "Add"), def.Type);
        var getManifest = HandlerManifest.FromDefinition(
            def.Handlers.First(h => h.Name == "Get"), def.Type);

        Assert.Equal("EXCLUSIVE", addManifest.Type);
        Assert.Equal("SHARED", getManifest.Type);
    }

    [Fact]
    public void HandlerManifest_NoTypeForStatelessService()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var greetManifest = HandlerManifest.FromDefinition(
            def.Handlers.First(h => h.Name == "Greet"), def.Type);

        Assert.Null(greetManifest.Type);
    }

    [Fact]
    public void HandlerManifest_SetsInputOutputDescriptors()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var greetManifest = HandlerManifest.FromDefinition(
            def.Handlers.First(h => h.Name == "Greet"), def.Type);

        Assert.NotNull(greetManifest.Input);
        Assert.Equal("application/json", greetManifest.Input.ContentType);
        Assert.True(greetManifest.Input.Required);

        Assert.NotNull(greetManifest.Output);
        Assert.Equal("application/json", greetManifest.Output.ContentType);
        Assert.False(greetManifest.Output.SetContentTypeIfEmpty);
    }

    [Fact]
    public void HandlerManifest_VoidHandler_HasEmptyPayloadDescriptors()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var sayHelloManifest = HandlerManifest.FromDefinition(
            def.Handlers.First(h => h.Name == "SayHello"), def.Type);

        // Void input: empty descriptor (serializes to {})
        Assert.NotNull(sayHelloManifest.Input);
        Assert.Null(sayHelloManifest.Input.ContentType);
        Assert.Null(sayHelloManifest.Input.Required);

        // Void output: only setContentTypeIfEmpty=false
        Assert.NotNull(sayHelloManifest.Output);
        Assert.Null(sayHelloManifest.Output.ContentType);
        Assert.False(sayHelloManifest.Output.SetContentTypeIfEmpty);
    }

    [Fact]
    public void Manifest_SerializesToExpectedJson()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("BIDI_STREAM", root.GetProperty("protocolMode").GetString());
        Assert.Equal(5, root.GetProperty("minProtocolVersion").GetInt32());

        var services = root.GetProperty("services");
        Assert.Equal(1, services.GetArrayLength());

        var svc = services[0];
        Assert.Equal("CounterObject", svc.GetProperty("name").GetString());
        Assert.Equal("VIRTUAL_OBJECT", svc.GetProperty("ty").GetString());
    }

    [Fact]
    public void Manifest_HasCorrectProtocolMode()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);

        Assert.Equal("BIDI_STREAM", manifest.ProtocolMode);
    }

    [Fact]
    public void Manifest_HasCorrectProtocolVersionRange()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);

        Assert.Equal(5, manifest.MinProtocolVersion);
        Assert.Equal(6, manifest.MaxProtocolVersion);
        Assert.True(manifest.MinProtocolVersion <= manifest.MaxProtocolVersion);
    }

    [Fact]
    public void HandlerManifest_TypeIsNull_ForServiceType()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        foreach (var handler in def.Handlers)
        {
            var handlerManifest = HandlerManifest.FromDefinition(handler, ServiceType.Service);
            Assert.Null(handlerManifest.Type);
        }
    }

    [Fact]
    public void HandlerManifest_ExclusiveAndShared_ForVirtualObject()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!;

        var addHandler = def.Handlers.First(h => h.Name == "Add");
        var addManifest = HandlerManifest.FromDefinition(addHandler, ServiceType.VirtualObject);
        Assert.Equal("EXCLUSIVE", addManifest.Type);

        var getHandler = def.Handlers.First(h => h.Name == "Get");
        var getManifest = HandlerManifest.FromDefinition(getHandler, ServiceType.VirtualObject);
        Assert.Equal("SHARED", getManifest.Type);
    }

    [Fact]
    public void HandlerManifest_WorkflowAndShared_ForWorkflow()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(OrderWorkflow))!;

        var runHandler = def.Handlers.First(h => h.Name == "Run");
        var runManifest = HandlerManifest.FromDefinition(runHandler, ServiceType.Workflow);
        Assert.Equal("WORKFLOW", runManifest.Type);

        var getStatusHandler = def.Handlers.First(h => h.Name == "GetStatus");
        var getStatusManifest = HandlerManifest.FromDefinition(getStatusHandler, ServiceType.Workflow);
        Assert.Equal("SHARED", getStatusManifest.Type);
    }

    [Fact]
    public void HandlerManifest_InputDescriptor_PresentWhenHasInput()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var greet = def.Handlers.First(h => h.Name == "Greet");

        Assert.True(greet.HasInput);
        var manifest = HandlerManifest.FromDefinition(greet, ServiceType.Service);

        Assert.NotNull(manifest.Input);
        Assert.Equal("application/json", manifest.Input.ContentType);
        Assert.True(manifest.Input.Required);
    }

    [Fact]
    public void HandlerManifest_InputDescriptor_EmptyWhenNoInput()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var sayHello = def.Handlers.First(h => h.Name == "SayHello");

        Assert.False(sayHello.HasInput);
        var manifest = HandlerManifest.FromDefinition(sayHello, ServiceType.Service);

        // Void input: empty descriptor (no contentType, no required)
        Assert.NotNull(manifest.Input);
        Assert.Null(manifest.Input.ContentType);
        Assert.Null(manifest.Input.Required);
    }

    [Fact]
    public void HandlerManifest_OutputDescriptor_PresentWhenHasOutput()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var greet = def.Handlers.First(h => h.Name == "Greet");

        Assert.True(greet.HasOutput);
        var manifest = HandlerManifest.FromDefinition(greet, ServiceType.Service);

        Assert.NotNull(manifest.Output);
        Assert.Equal("application/json", manifest.Output.ContentType);
        Assert.False(manifest.Output.SetContentTypeIfEmpty);
    }

    [Fact]
    public void HandlerManifest_OutputDescriptor_HasSetContentTypeIfEmpty_WhenNoOutput()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var sayHello = def.Handlers.First(h => h.Name == "SayHello");

        Assert.False(sayHello.HasOutput);
        var manifest = HandlerManifest.FromDefinition(sayHello, ServiceType.Service);

        // Void output: only setContentTypeIfEmpty=false, no contentType
        Assert.NotNull(manifest.Output);
        Assert.Null(manifest.Output.ContentType);
        Assert.False(manifest.Output.SetContentTypeIfEmpty);
    }

    [Fact]
    public void ServiceManifest_AllHandlersPresent()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!;
        var serviceManifest = ServiceManifest.FromDefinition(def);

        Assert.Equal(2, serviceManifest.Handlers.Count);
        Assert.Contains(serviceManifest.Handlers, h => h.Name == "Add");
        Assert.Contains(serviceManifest.Handlers, h => h.Name == "Get");
    }

    // ---- ServiceManifest.FromDefinition type switch: all 4 arms (EndpointManifest.cs:52-57) ------

    [Theory]
    [InlineData(ServiceType.Service, "SERVICE")]
    [InlineData(ServiceType.VirtualObject, "VIRTUAL_OBJECT")]
    [InlineData(ServiceType.Workflow, "WORKFLOW")]
    public void ServiceManifest_FromDefinition_MapsEachServiceType(ServiceType type, string expected)
    {
        // The three named arms of the `def.Type switch`. Each must map to its wire enum string.
        var def = MakeServiceDefinition(type);
        Assert.Equal(expected, ServiceManifest.FromDefinition(def).Type);
    }

    [Fact]
    public void ServiceManifest_FromDefinition_UnknownServiceType_FallsBackToService()
    {
        // EndpointManifest.cs:57 — the `_ => "SERVICE"` default arm. An out-of-range ServiceType value
        // (no enum member) must not throw; it defensively maps to the plain "SERVICE" wire type.
        var def = MakeServiceDefinition((ServiceType)999);
        Assert.Equal("SERVICE", ServiceManifest.FromDefinition(def).Type);
    }

    private static ServiceDefinition MakeServiceDefinition(ServiceType type) => new()
    {
        Name = "TypeSwitchService",
        Type = type,
        Factory = _ => new object(),
        Handlers =
        [
            new HandlerDefinition { Name = "H", IsShared = false, Invoker = (_, _, _, _) => Task.FromResult<object?>(null) }
        ]
    };

    [Fact]
    public void Manifest_Json_ExcludesNullType_ForServiceHandlers()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var doc = JsonDocument.Parse(json);
        var handlers = doc.RootElement.GetProperty("services")[0].GetProperty("handlers");
        var greetHandler = handlers.EnumerateArray().First(h => h.GetProperty("name").GetString() == "Greet");

        // For Service type, handler type should not be present in JSON (it's null and ignored)
        Assert.False(greetHandler.TryGetProperty("ty", out _));
    }

    [Fact]
    public void Manifest_Json_IncludesHandlerType_ForVirtualObjectHandlers()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(CounterObject))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var doc = JsonDocument.Parse(json);
        var handlers = doc.RootElement.GetProperty("services")[0].GetProperty("handlers");
        var addHandler = handlers.EnumerateArray().First(h => h.GetProperty("name").GetString() == "Add");
        var getHandler = handlers.EnumerateArray().First(h => h.GetProperty("name").GetString() == "Get");

        Assert.Equal("EXCLUSIVE", addHandler.GetProperty("ty").GetString());
        Assert.Equal("SHARED", getHandler.GetProperty("ty").GetString());
    }

    [Fact]
    public void Manifest_Json_VoidHandler_HasEmptyInputAndSetContentTypeIfEmpty()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var json = JsonSerializer.Serialize(manifest, DiscoveryJsonContext.Default.EndpointManifest);

        using var doc = JsonDocument.Parse(json);
        var handlers = doc.RootElement.GetProperty("services")[0].GetProperty("handlers");
        var sayHelloHandler = handlers.EnumerateArray().First(h => h.GetProperty("name").GetString() == "SayHello");

        // Void input: empty object {}
        Assert.True(sayHelloHandler.TryGetProperty("input", out var input));
        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        Assert.False(input.TryGetProperty("contentType", out _));
        Assert.False(input.TryGetProperty("required", out _));

        // Void output: {"setContentTypeIfEmpty": false}
        Assert.True(sayHelloHandler.TryGetProperty("output", out var output));
        Assert.Equal(JsonValueKind.Object, output.ValueKind);
        Assert.False(output.TryGetProperty("contentType", out _));
        Assert.True(output.TryGetProperty("setContentTypeIfEmpty", out var setContentType));
        Assert.False(setContentType.GetBoolean());
    }

    [Fact]
    public void Manifest_Json_NonVoidHandler_HasRequiredAndSetContentTypeIfEmpty()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var json = JsonSerializer.Serialize(manifest, DiscoveryJsonContext.Default.EndpointManifest);

        using var doc = JsonDocument.Parse(json);
        var handlers = doc.RootElement.GetProperty("services")[0].GetProperty("handlers");
        var greetHandler = handlers.EnumerateArray().First(h => h.GetProperty("name").GetString() == "Greet");

        // Non-void input: {"required": true, "contentType": "application/json"}
        Assert.True(greetHandler.TryGetProperty("input", out var input));
        Assert.Equal("application/json", input.GetProperty("contentType").GetString());
        Assert.True(input.GetProperty("required").GetBoolean());

        // Non-void output: {"contentType": "application/json", "setContentTypeIfEmpty": false}
        Assert.True(greetHandler.TryGetProperty("output", out var output));
        Assert.Equal("application/json", output.GetProperty("contentType").GetString());
        Assert.False(output.GetProperty("setContentTypeIfEmpty").GetBoolean());
    }

    [Fact]
    public void Manifest_Json_FieldNames_AreCamelCase()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var json = JsonSerializer.Serialize(manifest, DiscoveryJsonContext.Default.EndpointManifest);

        // Verify no snake_case field names appear
        Assert.DoesNotContain("inactivity_timeout", json);
        Assert.DoesNotContain("abort_timeout", json);
        Assert.DoesNotContain("idempotency_retention", json);
        Assert.DoesNotContain("journal_retention", json);
        Assert.DoesNotContain("ingress_private", json);
        Assert.DoesNotContain("workflow_completion_retention", json);
    }

    [Fact]
    public void Manifest_WorkflowService_MapsCorrectly()
    {
        var registry = new ServiceRegistry();
        registry.Register(ServiceDefinitionRegistry.TryGet(typeof(OrderWorkflow))!);
        registry.Freeze();

        var manifest = EndpointManifest.FromRegistry(registry);
        var service = manifest.Services.First(s => s.Name == "OrderWorkflow");

        Assert.Equal("WORKFLOW", service.Type);
        Assert.Equal(2, service.Handlers.Count);

        var runHandler = service.Handlers.First(h => h.Name == "Run");
        Assert.Equal("WORKFLOW", runHandler.Type);

        var getStatusHandler = service.Handlers.First(h => h.Name == "GetStatus");
        Assert.Equal("SHARED", getStatusHandler.Type);
    }

    [Fact]
    public void NegotiateVersion_NoAcceptHeader_DefaultsToV1()
    {
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(null);
        Assert.Equal("application/vnd.restate.endpointmanifest.v1+json", result);

        result = RestateEndpointRouteBuilderExtensions.NegotiateVersion("");
        Assert.Equal("application/vnd.restate.endpointmanifest.v1+json", result);
    }

    [Fact]
    public void NegotiateVersion_WildcardAccept_DefaultsToV1()
    {
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion("*/*");
        Assert.Equal("application/vnd.restate.endpointmanifest.v1+json", result);
    }

    [Fact]
    public void NegotiateVersion_V3Requested_ReturnsV3()
    {
        var accept = "application/vnd.restate.endpointmanifest.v2+json, application/vnd.restate.endpointmanifest.v3+json";
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(accept);
        Assert.Equal("application/vnd.restate.endpointmanifest.v3+json", result);
    }

    [Fact]
    public void NegotiateVersion_OnlyV2Requested_ReturnsV2()
    {
        var accept = "application/vnd.restate.endpointmanifest.v2+json";
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(accept);
        Assert.Equal("application/vnd.restate.endpointmanifest.v2+json", result);
    }

    [Fact]
    public void NegotiateVersion_V4OnlyRequested_ReturnsNull()
    {
        var accept = "application/vnd.restate.endpointmanifest.v4+json";
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(accept);
        Assert.Null(result);
    }

    [Fact]
    public void NegotiateVersion_PicksHighestMutual()
    {
        // Runtime sends v2, v3, v4 — we support v1, v2, v3 — should pick v3
        var accept = "application/vnd.restate.endpointmanifest.v2+json, application/vnd.restate.endpointmanifest.v3+json, application/vnd.restate.endpointmanifest.v4+json";
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(accept);
        Assert.Equal("application/vnd.restate.endpointmanifest.v3+json", result);
    }

    [Fact]
    public void NegotiateVersion_OnlyV1Requested_ReturnsV1()
    {
        // The lowest-priority substring arm: a runtime that only speaks v1 gets v1 (covers the
        // explicit "endpointmanifest.v1" branch distinct from the no-Accept/wildcard default).
        var accept = "application/vnd.restate.endpointmanifest.v1+json";
        var result = RestateEndpointRouteBuilderExtensions.NegotiateVersion(accept);
        Assert.Equal("application/vnd.restate.endpointmanifest.v1+json", result);
    }

    [Theory]
    // restate-server negotiates the per-invocation protocol version and sends it as the request
    // Content-Type; the /invoke response MUST echo the SAME version or the server rejects the
    // stream with RT0012. A version WITHIN [V5,V6] round-trips to that version number.
    [InlineData("application/vnd.restate.invocation.v5", 5)]
    [InlineData("application/vnd.restate.invocation.v6", 6)]
    // Parameters (charset etc.) are stripped — only the bare media type carries the version token.
    [InlineData("application/vnd.restate.invocation.v5; charset=utf-8", 5)]
    public void NegotiateInvocationVersion_SupportedVersion_ReturnsVersionNumber(string request, int expected)
    {
        var result = RestateEndpointRouteBuilderExtensions.NegotiateInvocationVersion(request);
        Assert.Equal(expected, result);
        // The negotiated number renders back to the echoed response content type.
        Assert.Equal(request.Split(';')[0].Trim(),
            Restate.Sdk.Internal.Protocol.ProtocolVersion.ContentTypeFor(result));
    }

    [Theory]
    // G12 — a recognizable invocation content type whose version is OUTSIDE [V5,V6] is RT0015:
    // NegotiateInvocationVersion returns 0, the /invoke endpoint rejects with 415.
    [InlineData("application/vnd.restate.invocation.v4")]
    [InlineData("application/vnd.restate.invocation.v7")]
    [InlineData("application/vnd.restate.invocation.v1")]
    [InlineData("application/vnd.restate.invocation.v99")]
    public void NegotiateInvocationVersion_UnsupportedVersion_ReturnsZero(string request)
    {
        Assert.Equal(0, RestateEndpointRouteBuilderExtensions.NegotiateInvocationVersion(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // A request with no recognizable invocation content type (a hand-crafted client, or our own
    // unit tests that post a bare ByteArrayContent) falls back to the manifest max (v6).
    [InlineData("application/json")]
    [InlineData("text/plain")]
    // A malformed invocation content type whose version token is not an integer is treated as
    // unrecognized and falls back to the max rather than rejecting.
    [InlineData("application/vnd.restate.invocation.vX")]
    public void NegotiateInvocationVersion_UnknownRequest_FallsBackToMax(string? request)
    {
        var result = RestateEndpointRouteBuilderExtensions.NegotiateInvocationVersion(request);
        Assert.Equal(Restate.Sdk.Internal.Protocol.ProtocolVersion.MaximumSupported, result);
    }
}