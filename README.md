[![CI](https://github.com/BeshoyHindy/restate-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/BeshoyHindy/restate-sdk-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Restate.Sdk)](https://www.nuget.org/packages/Restate.Sdk)
[![Documentation](https://img.shields.io/badge/doc-reference-blue)](https://docs.restate.dev)
[![Discord](https://img.shields.io/discord/1128210118216007792?logo=discord)](https://discord.gg/skW3AZ6uGd)
[![Twitter](https://img.shields.io/twitter/follow/restatedev.svg?style=social&label=Follow)](https://twitter.com/intent/follow?screen_name=restatedev)

# Restate .NET SDK

> **Pre-release (0.1.0-alpha.1)** -- Under active development. APIs may change between releases.
>
> This is a community-driven project, not an official Restate SDK.
> Built by reverse-engineering the Java, TypeScript, and Go SDKs.
> For official SDKs, see [github.com/restatedev](https://github.com/restatedev).

[Restate](https://restate.dev/) is a system for easily building resilient applications using
*distributed durable async/await*. This repository contains a .NET SDK for writing services
that run on the Restate runtime.

## Community

* [Join our online community](https://discord.gg/skW3AZ6uGd) for help, sharing feedback and talking to the community.
* [Check out our documentation](https://docs.restate.dev) to get quickly started!
* [Follow us on Twitter](https://twitter.com/restatedev) for staying up to date.
* [Create a GitHub issue](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues) for requesting a new feature or reporting a problem.
* [Visit the Restate GitHub org](https://github.com/restatedev) for official SDKs and other repositories.

## Using the SDK

### Prerequisites

* .NET 10.0 SDK or later
* [Restate Server](https://restate.dev/get-restate/) or [Restate CLI](https://docs.restate.dev/develop/local_dev/)

  Alternatively, use the included Docker Compose file:

  ```bash
  docker compose up -d
  ```

### Install

```bash
dotnet add package Restate.Sdk --version 0.1.0-alpha.1
```

Optional packages:

```bash
dotnet add package Restate.Sdk.Testing --version 0.1.0-alpha.1   # Mock contexts for unit testing
dotnet add package Restate.Sdk.Lambda --version 0.1.0-alpha.1    # AWS Lambda adapter
```

> The Roslyn source generator is bundled with `Restate.Sdk` -- typed clients and service
> definitions are generated automatically at compile time. No additional packages needed.

### Quick Start

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

### Service Types

Restate supports three service types, each with different consistency and state guarantees.

#### Stateless Service

No state. Multiple invocations run concurrently.

```csharp
[Service]
public class EmailService
{
    [Handler]
    public async Task<bool> SendEmail(Context ctx, EmailRequest request)
    {
        return await ctx.Run("send-email", async () =>
        {
            await emailClient.SendAsync(request.To, request.Subject, request.Body);
            return true;
        });
    }
}
```

#### Virtual Object

Keyed entities with exclusive state access. Only one `[Handler]` runs at a time per key.
`[SharedHandler]` methods can run concurrently with read-only state access.

```csharp
[VirtualObject]
public class Counter
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var current = await ctx.Get(Count);
        var next = current + delta;
        ctx.Set(Count, next);
        return next;
    }

    [SharedHandler]
    public async Task<int> Get(SharedObjectContext ctx)
        => await ctx.Get(Count);

    [Handler]
    public Task Reset(ObjectContext ctx)
    {
        ctx.ClearAll();
        return Task.CompletedTask;
    }
}
```

#### Workflow

Long-running durable workflows with state and awakeables for external signaling. The `Run` handler
executes exactly once per workflow ID. Workflow promises (`ctx.Promise<T>()`) are also available
for signaling between handlers.

```csharp
[Workflow]
public class SignupWorkflow
{
    private static readonly StateKey<string> Status = new("status");

    [Handler]
    public async Task<bool> Run(WorkflowContext ctx, SignupRequest request)
    {
        ctx.Set(Status, "creating-account");

        var accountId = await ctx.Run("create-account",
            () => AccountService.Create(request.Email, request.Name));

        ctx.Set(Status, "awaiting-verification");

        // Awakeable: a durable promise resolved by an external system.
        // Pass awakeable.Id to the external system; await awakeable.Value to block.
        var awakeable = ctx.Awakeable<string>();

        await ctx.Run("send-verification-email",
            () =>
            {
                EmailService.SendVerification(request.Email, awakeable.Id);
                return Task.CompletedTask;
            });

        // Workflow suspends here until the external system resolves the awakeable
        await awakeable.Value;

        ctx.Set(Status, "completed");
        return true;
    }

    [SharedHandler]
    public async Task<string> GetStatus(SharedWorkflowContext ctx)
        => await ctx.Get(Status) ?? "unknown";
}
```

### Durable Building Blocks

The `Context` object provides durable operations that are automatically journaled and replayed:

```csharp
// Side effects (journaled, replayed on retries)
var result = await ctx.Run("name", async () => await FetchDataAsync());
var value = await ctx.Run("name", () => ComputeSync());

// Service-to-service calls (retried, exactly-once)
var response = await ctx.Call<string>("GreeterService", "Greet", "Alice");
var count = await ctx.Call<int>("CounterObject", "my-key", "Add", 1);

// One-way sends (fire-and-forget, returns InvocationHandle for tracking)
InvocationHandle handle = await ctx.Send("EmailService", "SendEmail", request);
await ctx.Send("ReminderService", "Remind", data, delay: TimeSpan.FromHours(1));

// Durable timers (survive restarts)
await ctx.Sleep(TimeSpan.FromMinutes(5));

// Non-blocking timer (returns a future for use with combinators)
var timer = ctx.Timer(TimeSpan.FromMinutes(5));

// Awakeables (promises resolved by external systems)
var awakeable = ctx.Awakeable<string>();
// pass awakeable.Id to external system, then:
var payload = await awakeable.Value;

// Non-blocking futures and combinators
var f1 = ctx.RunAsync<int>("a", () => Task.FromResult(1));
var f2 = ctx.RunAsync<int>("b", () => Task.FromResult(2));
var results = await ctx.All(f1, f2);     // wait for all
var winner = await ctx.Race(f1, f2);     // first to complete

// Replay-safe random
var id = ctx.Random.NextGuid();
var n = ctx.Random.Next(1, 100);

// Replay-safe console (silent during replay)
ctx.Console.Log("processing...");

// Durable timestamp
var now = await ctx.Now();

// Context properties
var invocationId = ctx.InvocationId;    // unique ID for this invocation
var headers = ctx.Headers;              // request headers
CancellationToken ct = ctx.Aborted;     // fires when invocation is cancelled
```

### Error Handling

Restate automatically retries failed handlers. To signal a non-retryable failure (validation errors,
business rule violations), throw a `TerminalException`:

```csharp
// Non-retryable error -- Restate will NOT retry this invocation
throw new TerminalException("Order not found", 404);

// All other exceptions are retried automatically with exponential backoff
```

### ASP.NET Core Integration

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

### AWS Lambda

Deploy handlers as Lambda functions using the `Restate.Sdk.Lambda` package:

```csharp
using Restate.Sdk.Lambda;

public class Handler : RestateLambdaHandler
{
    public override void Register(Action<Type> bind)
    {
        bind(typeof(GreeterService));
        bind(typeof(CounterObject));
    }
}
```

Configure the Lambda function handler as `YourAssembly::YourNamespace.Handler::FunctionHandler`.

### Testing

The `Restate.Sdk.Testing` package provides mock contexts for unit testing handlers without
a running Restate server:

```csharp
using Restate.Sdk.Testing;

var ctx = new MockContext();
var service = new GreeterService();
var result = await service.Greet(ctx, "Alice");
Assert.Equal("Hello, Alice!", result);
```

Mock contexts are available for every context type:

| Mock Class | For |
|------------|-----|
| `MockContext` | Stateless services |
| `MockObjectContext` | Virtual object exclusive handlers |
| `MockSharedObjectContext` | Virtual object shared handlers |
| `MockWorkflowContext` | Workflow run handlers |
| `MockSharedWorkflowContext` | Workflow shared handlers |

Mock context features:

```csharp
// Deterministic time
var ctx = new MockContext();
ctx.CurrentTime = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
var now = await ctx.Now(); // returns the configured time

// Setup call results
ctx.SetupCall<string>("GreeterService", "Greet", "Hello!");

// Setup call failures
ctx.SetupCallFailure("GreeterService", "Greet", new TerminalException("fail", 500));

// Register typed clients
ctx.RegisterClient<IGreeterServiceClient>(myMockClient);

// Verify recorded calls, sends, and sleeps
Assert.Single(ctx.Calls);
Assert.Equal("GreeterService", ctx.Calls[0].Service);
```

### Interfaces

Context interfaces (`IContext`, `IObjectContext`, etc.) are available for utility methods,
type constraints, and generic programming:

```csharp
// Utility method accepting any context type
public static async Task<string> FormatTimestamp(IContext ctx)
{
    var now = await ctx.Now();
    return now.ToString("O");
}
```

### External Ingress Client

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

## Samples

The [`samples/`](samples/) directory contains complete working examples:

| Sample | Port | Demonstrates |
|--------|------|--------------|
| [Greeter](samples/Greeter) | 9080 | Service basics, `ctx.Run()`, `ctx.Sleep()` |
| [Counter](samples/Counter) | 9081 | Virtual object state, `StateKey<T>`, shared handlers |
| [TicketReservation](samples/TicketReservation) | 9082 | State machines, delayed sends, cross-service calls |
| [SignupWorkflow](samples/SignupWorkflow) | 9084 | Workflows, durable promises, awakeables |

Run any sample:

```bash
cd samples/Greeter
dotnet run
```

## Compatibility

| SDK Version | Restate Server | Protocol | .NET |
|-------------|----------------|----------|------|
| 0.1.0-alpha.1 | 1.6.0+ | v5 - v6 | .NET 10.0 |

## Contributing

Contributions are welcome. Whether feature requests, bug reports, or pull requests, all contributions are appreciated.

### Building from source

```bash
dotnet build
dotnet test
```

### Running specific tests

```bash
dotnet test test/Restate.Sdk.Tests --filter "FullyQualifiedName~ProtobufParser"
dotnet test test/Restate.Sdk.Generators.Tests
```

### Code formatting

The CI pipeline enforces consistent formatting. Check locally before pushing:

```bash
dotnet format --verify-no-changes
```

### CI

Pull requests run two GitHub Actions jobs automatically:
- **Build & Test** -- builds in Release mode and runs all tests
- **Format Check** -- verifies `dotnet format` compliance

## License

MIT
