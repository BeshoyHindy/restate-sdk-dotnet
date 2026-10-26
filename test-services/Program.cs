using Restate.Sdk.Hosting;
using Restate.Sdk.TestServices;

// Test services for the Restate SDK conformance suite (https://github.com/restatedev/sdk-test-suite).
// The suite starts this container with PORT, SERVICES (a comma-separated subset to bind) and,
// for the request-signing test, E2E_REQUEST_SIGNING.
var services = new Dictionary<string, Action<RestateHostBuilder>>
{
    ["AwakeableHolder"] = b => b.AddVirtualObject<AwakeableHolder>(),
    ["BlockAndWaitWorkflow"] = b => b.AddWorkflow<BlockAndWaitWorkflow>(),
    ["CancelTestRunner"] = b => b.AddVirtualObject<CancelTestRunner>(),
    ["CancelTestBlockingService"] = b => b.AddVirtualObject<CancelTestBlockingService>(),
    ["Counter"] = b => b.AddVirtualObject<Counter>(),
    ["Failing"] = b => b.AddVirtualObject<Failing>(),
    ["KillTestRunner"] = b => b.AddVirtualObject<KillTestRunner>(),
    ["KillTestSingleton"] = b => b.AddVirtualObject<KillTestSingleton>(),
    ["ListObject"] = b => b.AddVirtualObject<ListObject>(),
    ["MapObject"] = b => b.AddVirtualObject<MapObject>(),
    ["NonDeterministic"] = b => b.AddVirtualObject<NonDeterministic>(),
    ["Proxy"] = b => b.AddService<Proxy>(),
    ["TestUtilsService"] = b => b.AddService<TestUtilsService>(),
    ["VirtualObjectCommandInterpreter"] = b => b.AddVirtualObject<VirtualObjectCommandInterpreter>(),
};

var builder = RestateHost.CreateBuilder();

var requested = Environment.GetEnvironmentVariable("SERVICES");
IEnumerable<string> names = string.IsNullOrWhiteSpace(requested)
    ? services.Keys
    : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
foreach (var name in names)
{
    if (!services.TryGetValue(name, out var bind))
        throw new InvalidOperationException($"Unknown test service '{name}'");
    bind(builder);
}

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port))
    builder.WithPort(port);

var signingKey = Environment.GetEnvironmentVariable("E2E_REQUEST_SIGNING");
if (!string.IsNullOrEmpty(signingKey))
    builder.WithIdentityKeys(signingKey);

await builder.Build().RunAsync();
