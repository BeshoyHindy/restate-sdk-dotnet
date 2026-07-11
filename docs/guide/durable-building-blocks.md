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
