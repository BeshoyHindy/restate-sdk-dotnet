# Telemetry

The SDK ships OpenTelemetry-compatible instrumentation out of the box. Both the
`System.Diagnostics.ActivitySource` and the `System.Diagnostics.Metrics.Meter` it publishes are
named `Restate.Sdk`, and both are static — nothing has to be resolved from dependency injection,
so the same instruments work under AWS Lambda and Native AOT.

Nothing is emitted until something listens. With no metric listener the instruments short-circuit,
and with no activity listener the SDK skips span creation entirely rather than allocating an
`Activity` per invocation.

## Wiring it up

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Restate.Sdk.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRestate(opts =>
{
    opts.AddService<GreeterService>();
    opts.Telemetry.EnableOperationActivities = true;
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Restate.Sdk")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("Restate.Sdk")
        .AddOtlpExporter());

var app = builder.Build();
app.MapRestate();
await app.RunAsync();
```

`RestateTelemetryOptions` is reachable from every hosting surface. On the self-hosted builder:

```csharp
using Restate.Sdk.Hosting;

await RestateHost.CreateBuilder()
    .AddService<GreeterService>()
    .ConfigureTelemetry(o => o.EnableOperationActivities = true)
    .Build()
    .RunAsync();
```

On AWS Lambda, from `Register()` — the invocation pipeline is built during handler construction,
so a later call throws `InvalidOperationException` rather than being silently ignored:

```csharp
using Restate.Sdk;

public class Handler : RestateLambdaHandler
{
    public override void Register()
    {
        Bind<GreeterService>();
        ConfigureTelemetry(o => o.EnableOperationActivities = true);
    }
}
```

## Metrics

Three instruments are recorded once per invocation attempt — never per journal operation.

| Instrument | Kind | Unit | Meaning |
|------------|------|------|---------|
| `restate.sdk.invocations` | `Counter<long>` | `{invocation}` | Completed invocation attempts |
| `restate.sdk.invocation.duration` | `Histogram<double>` | `s` | Duration of the attempt in seconds |
| `restate.sdk.journal.replayed_commands` | `Histogram<long>` | `{command}` | Journal commands replayed for the attempt, including the input command |

`restate.sdk.invocations` and `restate.sdk.invocation.duration` carry three tags:

| Tag | Value |
|-----|-------|
| `restate.service` | The registered Service, Virtual Object, or Workflow name |
| `restate.handler` | The handler name |
| `outcome` | How the attempt ended |

`restate.sdk.journal.replayed_commands` carries `restate.service` and `restate.handler` only. It
deliberately has no `outcome` tag: replay depth describes where an attempt started, not how it
finished, and tagging it by outcome would make the histogram hard to aggregate.

`outcome` is one of:

| Value | Meaning |
|-------|---------|
| `success` | The handler completed and its output was journaled |
| `terminal_error` | The handler threw a `TerminalException` — Restate will not retry |
| `error` | The attempt failed with a retryable error |
| `cancelled` | The attempt was cancelled by the caller |
| `suspended` | The attempt suspended waiting on pending completions or Signals; the runtime resumes it later |

A suspended attempt is normal, not a failure: a Workflow that sleeps for a day suspends, and the
counter records one `suspended` attempt now and another attempt when it resumes. Baseline alerting
on `outcome` of `error` rather than "anything that is not `success`".

## Invocation spans

Every invocation attempt opens one span on the `Restate.Sdk` source, named `{Service}/{Handler}`
with `ActivityKind.Server`. If the invocation carries a `traceparent` header — with `tracestate`
alongside it when present — the span is parented to that remote context, so it joins the caller's
trace rather than starting a new one.

| Tag | Set |
|-----|-----|
| `restate.invocation.id` | At start |
| `restate.service` | At start |
| `restate.handler` | At start |
| `rpc.service` | At start — mirrors `restate.service` for OpenTelemetry RPC conventions |
| `rpc.method` | At start — mirrors `restate.handler` |
| `rpc.system` | At start — always `restate` |
| `restate.journal.commands` | At completion — journal commands the invocation holds by the end of the attempt |
| `restate.replayed` | At completion — `true` when the attempt resumed an existing journal |

The span status is set to `Error` when the attempt ends in a `TerminalException`, a protocol
error, or an unhandled exception. Suspensions and cancellations leave the status unset — neither
is a failure of the handler.

## Per-operation activities

Durable operations can each open a child span beneath the invocation span. This is off by default:
every traced operation allocates an `Activity` on the invocation hot path. Turn it on with
`EnableOperationActivities`, as in the samples above.

| Activity | Started by | Tags |
|----------|-----------|------|
| `restate.run` | `ctx.Run(...)` | `restate.run.name` — the name given to the Run |
| `restate.call` | `ctx.Call(...)`, including the generated typed clients that wrap it | `rpc.service`, `rpc.method` — the invocation target |
| `restate.sleep` | `ctx.Sleep(...)` | `restate.sleep.duration_ms` |

Two things stay true even when the option is on. Activities are still only created while a
listener is attached to the `Restate.Sdk` source, and replayed operations never start one — no
user code runs during replay, so a span there would time the journal read rather than the work.
A `restate.call` span covers the call's asynchronous completion, not just the moment it was sent.

## Logging

`ctx.Logger` is a replay-aware `ILogger`. It suppresses output while the invocation is replaying
its journal, so a handler that is re-executed after a restart does not duplicate every line it
already logged:

```csharp
using Microsoft.Extensions.Logging;
using Restate.Sdk;

[Service]
public class OrderService
{
    [Handler]
    public async Task<string> Submit(Context ctx, string orderId)
    {
        // Written once per invocation: suppressed while the journal replays.
        ctx.Logger.LogInformation("Submitting order {OrderId}", orderId);

        return await ctx.Run("charge", () => $"charged:{orderId}");
    }
}
```

The logger's category is the registered Service name, so per-service log levels work the way they
do for any other category. Its entries also sit inside a scope carrying `InvocationId`, which
structured logging providers surface as a field — that is the value to filter on when following a
single invocation. The SDK's own lifecycle logging (started, completed, suspending, protocol
errors) is written under the `Restate.Invocation` category instead, which lets you turn SDK
chatter down without silencing your handlers:

```json
{
  "Logging": {
    "LogLevel": {
      "Restate.Invocation": "Warning"
    }
  }
}
```

`ctx.Logger` needs an `ILoggerFactory`. Under ASP.NET Core one is always in the container; on
Lambda, a handler that never calls `UseLoggerFactory` gets a no-op logger and the replay-aware
wrapper is skipped altogether.
