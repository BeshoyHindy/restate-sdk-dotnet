# Durable Building Blocks

The `Context` object provides durable operations that are automatically journaled and replayed.

## Side Effects

```csharp
// Side effects (journaled, replayed on retries)
var result = await ctx.Run("name", async () => await FetchDataAsync());
var value = await ctx.Run("name", () => ComputeSync());

// Side effects with retry policy (custom backoff per operation)
var data = await ctx.Run("fetch", async () => await FetchDataAsync(),
    RetryPolicy.FixedAttempts(5));
var computed = await ctx.Run("compute", () => ComputeSync(),
    RetryPolicy.Default);
await ctx.Run("fire-and-forget", async () => await NotifyAsync(),
    new RetryPolicy
    {
        InitialDelay = TimeSpan.FromSeconds(1),
        ExponentiationFactor = 3.0,
        MaxDelay = TimeSpan.FromSeconds(30),
        MaxAttempts = 10,
        MaxDuration = TimeSpan.FromMinutes(5)
    });
```

## Calls and Sends

```csharp
// Service-to-service calls (retried, exactly-once)
var response = await ctx.Call<string>("GreeterService", "Greet", "Alice");
var count = await ctx.Call<int>("CounterObject", "my-key", "Add", 1);

// Calls with idempotency key (exactly-once deduplication)
var txnId = await ctx.Call<string>("PaymentService", "Charge", request,
    CallOptions.WithIdempotencyKey("order-123"));

// One-way sends (fire-and-forget, returns InvocationHandle for tracking)
InvocationHandle handle = await ctx.Send("EmailService", "SendEmail", request);
await ctx.Send("ReminderService", "Remind", data, delay: TimeSpan.FromHours(1));

// Cancel a running invocation
await ctx.CancelInvocation("inv-id-to-cancel");
```

## Timers

```csharp
// Durable timers (survive restarts)
await ctx.Sleep(TimeSpan.FromMinutes(5));

// Non-blocking timer (returns a future for use with combinators)
var timer = ctx.Timer(TimeSpan.FromMinutes(5));
```

## Awakeables

```csharp
// Awakeables (promises resolved by external systems)
var awakeable = ctx.Awakeable<string>();
// pass awakeable.Id to external system, then:
var payload = await awakeable.Value;
```

## Signals

A **signal** is a durable value delivered to a running invocation from outside it. The handler
awaits it; anything holding the **invocation handle** — another handler or an ingress caller —
resolves or rejects it. Unlike an awakeable, a named signal needs no id to be passed around: the
name is the rendezvous point.

```csharp
// Named signal: the resolver addresses this invocation and the name "approval"
var approval = ctx.Signal<string>("approval");
var decision = await approval.GetResult();

// Signals are durable futures, so they compose like any other
var first = await ctx.Race(ctx.Signal<string>("approved"), ctx.Signal<string>("rejected"));

// Unnamed signal: addressed by the index this call allocates (the first is 17)
var unnamed = await ctx.Signal<string>().GetResult();
```

## Resolving and rejecting signals

The other half of a signal is the side that completes it. From a handler, address the target
invocation through its **invocation handle**:

```csharp
InvocationHandle target = await ctx.Send("ReviewService", "Review", request);

// Resolve the signal the target handler awaits by name
await target.ResolveSignal(ctx, "approval", "granted");

// Or reject it: the awaiting handler fails with a TerminalException carrying the reason
await target.RejectSignal(ctx, "approval", "not approved");

// Unnamed signals are addressed by index instead of name
await ctx.ResolveSignal(target.InvocationId, 17, "granted");
await ctx.RejectSignal(target.InvocationId, 17, "not approved");
```

From outside the runtime, `RestateClient` completes a signal by **id** — the id an invocation hands
out with `ctx.Awakeable<T>().Id`:

```csharp
await client.ResolveSignal(signalId, "granted");
await client.RejectSignal(signalId, "not approved");
```

The ingress addresses signals by id only, so a signal awaited by name is resolvable from a handler
but not over ingress. Sending a signal is journaled, so a replayed attempt re-traverses it without
signalling twice.

A signal resolved before the current attempt started resolves from the journal on replay, without
waiting again. Signals and awakeables share one index allocator and one notification path: an
awakeable is an unnamed signal plus the opaque id that addresses it, so use `ctx.Awakeable<T>()`
when an external system needs an id to carry around, and `ctx.Signal<T>(name)` when the resolver
already knows the invocation and a name. Awakeables are unchanged and remain supported.

## Futures and Combinators

```csharp
// Non-blocking futures and combinators
var f1 = ctx.RunAsync<int>("a", () => Task.FromResult(1));
var f2 = ctx.RunAsync<int>("b", () => Task.FromResult(2));
var results = await ctx.All(f1, f2);     // wait for all
var winner = await ctx.Race(f1, f2);     // first to complete
```

## State

Virtual objects and workflows have durable key-value state accessed via `StateKey<T>`:

```csharp
private static readonly StateKey<int> Count = new("count");

var current = await ctx.Get(Count);
ctx.Set(Count, current + 1);
ctx.Clear(Count);
ctx.ClearAll();
```

See [Service Types](service-types.md) for complete virtual object and workflow examples,
including workflow promises (`ctx.Promise<T>()`).

## Deterministic Utilities

```csharp
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
