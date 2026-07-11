using System.Globalization;
using Greeter;
using Restate.Sdk.Hosting;

// Quick-start: self-hosted Restate endpoint with a stateless service.
// Register at http://localhost:9080 then call:
//   restate invocations invoke GreeterService Greet --body '{"name": "Alice"}'
var builder = RestateHost.CreateBuilder()
    .AddService<GreeterService>();

// Integration tests: verify request identity when comma-separated publickeyv1_... keys
// are provided (as printed by restate-server at startup), and allow a port override.
if (Environment.GetEnvironmentVariable("RESTATE_IDENTITY_KEYS") is { Length: > 0 } identityKeys)
    builder.WithIdentityKeys(identityKeys.Split(','));
if (Environment.GetEnvironmentVariable("GREETER_PORT") is { Length: > 0 } port)
    builder.WithPort(int.Parse(port, CultureInfo.InvariantCulture));

await builder.Build()
    .RunAsync();
