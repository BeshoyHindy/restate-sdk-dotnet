using Restate.Sdk.Hosting;
using ScopedClient;

// Scope and limit key: a service whose quotes run under a server-side concurrency scope, and an
// ingress client that addresses it with the scope bound once.
//
// Hosting mode (the default) serves the endpoint on port 9090:
//   dotnet run
//   restate deployments register http://localhost:9090
//   restate invocations invoke PricingService QuoteInScope \
//     --body '{"tenant": "tenant-a", "sku": "widget", "quantity": 2}'
//
// Client mode drives the registered service over the ingress, scope and all, and prints what a
// failed call reports through RestateIngressException:
//   dotnet run -- client [ingress-address]
if (args is ["client", ..])
{
    await IngressDemo.Run(args.Length > 1 ? args[1] : "http://localhost:8080");
    return;
}

await RestateHost.CreateBuilder()
    .WithPort(9090)
    .AddService<PricingService>()
    .Build()
    .RunAsync();
