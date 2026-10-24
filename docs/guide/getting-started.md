# Getting Started

## Prerequisites

* .NET 10.0 SDK or later
* [Restate Server](https://restate.dev/get-restate/) or [Restate CLI](https://docs.restate.dev/develop/local_dev/)

  Alternatively, use the Docker Compose file included in the repository:

  ```bash
  docker compose up -d
  ```

## Install

```bash
dotnet add package Restate.Sdk
```

Optional packages:

```bash
dotnet add package Restate.Sdk.Testing   # Mock contexts for unit testing
dotnet add package Restate.Sdk.Lambda    # AWS Lambda adapter
```

> The Roslyn source generator is bundled with `Restate.Sdk` — typed clients and service
> definitions are generated automatically at compile time. No additional packages needed.

## Quick Start

Define a service and host it:

```csharp
using Restate.Sdk;
using Restate.Sdk.Hosting;

[Service]
public class GreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        // Side effect: journaled and replayed on retries
        var greeting = await ctx.Run("build-greeting",
            () => $"Hello, {name}!");

        return greeting;
    }
}

await RestateHost.CreateBuilder()
    .AddService<GreeterService>()
    .Build()
    .RunAsync();
```

Start the service and register it with Restate:

```bash
dotnet run

restate deployments register http://localhost:9080
```

Invoke the service:

```bash
curl -X POST http://localhost:8080/GreeterService/Greet \
  -H 'content-type: application/json' \
  -d '"World"'
```

## Error Handling

Restate automatically retries failed handlers. To signal a non-retryable failure (validation
errors, business rule violations), throw a `TerminalException`:

```csharp
// Non-retryable error -- Restate will NOT retry this invocation
throw new TerminalException("Order not found", 404);

// All other exceptions are retried automatically with exponential backoff
```

## ASP.NET Core Integration

For applications that need full dependency injection:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRestate(opts =>
{
    opts.AddService<GreeterService>();
    opts.AddVirtualObject<CounterObject>();
    opts.AddWorkflow<SignupWorkflow>();
});

builder.Services.AddScoped<IEmailClient, SmtpEmailClient>();

var app = builder.Build();
app.MapRestate();
await app.RunAsync();
```

## External Ingress Client

Call Restate services from outside the runtime using `RestateClient`:

```csharp
using Restate.Sdk.Client;

using var client = new RestateClient("http://localhost:8080");

// Call a service handler
var greeting = await client.Service("GreeterService").Call<string>("Greet", "World");

// Call a virtual object
var count = await client.VirtualObject("CounterObject", "my-key").Call<int>("Add", 1);

// Start a workflow
await client.Workflow("SignupWorkflow", "user-1").Call<bool>("Run", "alice@example.com");

// Fire-and-forget with delay (returns invocation ID)
var invocationId = await client.Service("EmailService")
    .Send("SendEmail", request, delay: TimeSpan.FromHours(1));
```

The client addresses the versioned ingress routes (`/restate/call/...`, `/restate/send/...`,
`/restate/scope/{scope}/...`, `/restate/attach/{id}`, `/restate/output/{id}`), and escapes every
service name, key, handler name, scope and invocation id it puts in a path, so a
virtual-object key such as `a/b c?d` addresses the key rather than a different route.

> On restate-server 1.8 the ingress silently drops every client-supplied `x-restate-*` request
> header except `x-restate-limit-key`, which it reads before the drop. `RestateClient` has no
> per-call header API, so this only affects callers that add such headers themselves — through
> `HttpClient.DefaultRequestHeaders` or a `DelegatingHandler` on the `HttpClient` passed to
> `new RestateClient(httpClient)`.

Reflection-based overloads use camelCase JSON by default. To match an endpoint with different
JSON conventions, configure the client explicitly:

```csharp
using System.Text.Json;

var options = new RestateClientOptions
{
    JsonSerializerOptions = JsonSerializerOptions.Default
};
using var defaultJsonClient = new RestateClient("http://localhost:8080", options);
```

## Next Steps

- [Service Types](service-types.md) — pick between Service, Virtual Object, and Workflow
- [Durable Building Blocks](durable-building-blocks.md) — the full `Context` API
