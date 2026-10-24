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

A `Run` result is the object the action returned on the attempt that ran it, and a copy
deserialized from the journal on every later attempt, so the type must round-trip through the
configured serializer and handler code must not rely on reference identity.

## Calls and Sends

```csharp
// Service-to-service calls (retried, exactly-once)
var response = await ctx.Call<string>("GreeterService", "Greet", "Alice");
var count = await ctx.Call<int>("CounterObject", "my-key", "Add", 1);

// Calls with idempotency key (exactly-once deduplication)
var txnId = await ctx.Call<string>("PaymentService", "Charge", request,
    CallOptions.WithIdempotencyKey("order-123"));

// Calls in a server-side concurrency scope, optionally narrowed by a limit key
var quote = await ctx.Call<string>("PricingService", "Quote", request,
    CallOptions.WithScope("pricing", "tenant-a"));

// One-way sends (fire-and-forget, returns InvocationHandle for tracking)
InvocationHandle handle = await ctx.Send("EmailService", "SendEmail", request);
await ctx.Send("ReminderService", "Remind", data, delay: TimeSpan.FromHours(1));
await ctx.Send("EmailService", "SendEmail", request, SendOptions.WithScope("notifications"));

// Cancel a running invocation
await ctx.CancelInvocation("inv-id-to-cancel");
```

The scope and the limit key cap how many invocations run at once, and they need a server that
negotiates protocol V7. See [Scope and Limit Key](scope-and-limit-key.md).

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
awaits it; another handler holding the **invocation handle** resolves or rejects it. The ingress
addresses signals by awakeable id only, so a signal awaited by name has no ingress route — use an
awakeable when an external system must complete it. Unlike an awakeable, a named signal needs no id
to be passed around: the name is the rendezvous point.

```csharp
// Named signal: the resolver addresses this invocation and the name "approval"
var approval = ctx.Signal<string>("approval");
var decision = await approval.GetResult();

// Signals are durable futures, so they compose like any other
var first = await ctx.Race(ctx.Signal<string>("approved"), ctx.Signal<string>("rejected"));

// Unnamed signal: the returned SignalHandle<T> carries the index this call allocated, and that
// index — not a fixed number — is what the resolver addresses
SignalHandle<string> handle = ctx.Signal<string>();
var unnamed = await handle.GetResult();
```

A `SignalHandle<T>` is itself an `IDurableFuture<T>`, so it goes straight into `All`, `Race`, `Any`
and `AllSettled` without unwrapping `handle.Future`. Its index comes from the same allocator
awakeables draw from, so publish `handle.Index` — in state, or by calling the resolver — rather
than assuming which index an invocation allocated.

> Signal an invocation only once it has started. On restate-server 1.8.1 and later a signal sent to
> an invocation that has not started yet — one waiting out a delayed send, or queued behind another
> call to the same virtual-object key — is dropped, and the handler then waits for a signal that
> never arrives. Awakeable completions are affected the same way. The operator can change that by
> setting `experimental-enable-preflight-signal-recording = true` on the server, which records
> signals for an invocation that has not started. It requires every node to run 1.8.1 or later, and
> it is one-way: once a partition has it enabled, the cluster can no longer be rolled back below
> 1.8.1.

## Resolving and rejecting signals

The other half of a signal is the side that completes it. From a handler, address the target
invocation through its **invocation handle**:

```csharp
InvocationHandle target = await ctx.Send("ReviewService", "Review", request);

// Resolve the signal the target handler awaits by name
await target.ResolveSignal(ctx, "approval", "granted");

// Or reject it: the awaiting handler fails with a TerminalException carrying the reason
await target.RejectSignal(ctx, "approval", "not approved");

// Unnamed signals are addressed by index instead of name. The awaiting handler reads the index
// from the handle ctx.Signal<T>() returned (handle.Index) and publishes it — in state, or by
// calling the resolver — so nobody has to assume which index it allocated.
await target.ResolveSignal(ctx, signalIndex, "granted");
await target.RejectSignal(ctx, signalIndex, "not approved");
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

## Calling from outside the runtime

`RestateClient` invokes the same handlers from outside Restate — from an API gateway, a job, or a
test. Calls and sends take the same `CallOptions` and `SendOptions` a handler uses, and a scope can
be bound once to a derived client instead of passed on each invocation:

```csharp
using var client = new RestateClient("http://localhost:8080");

var greeting = await client.Service("GreeterService").Call<string>("Greet", "World");
var count = await client.VirtualObject("CounterObject", "my-key").Call<int>("Add", 1);
var invocationId = await client.Service("EmailService")
    .Send("SendEmail", request, delay: TimeSpan.FromHours(1));

// Bind the scope once; a per-call scope still wins over the bound one
var tenant = client.WithScope("pricing");
var quote = await tenant.Service("PricingService").Call<string>("Quote", request);
```

A caller that did not keep the invocation id can look the target up — by workflow name and key, or
by the idempotency key an invocation was submitted with — and then attach to it or read its output:

```csharp
var signup = await client.LookupWorkflow("SignupWorkflow", "user-1");
var result = await client.Attach<bool>(signup.InvocationId);

var email = await client.LookupIdempotentInvocation("EmailService", "SendEmail", "order-1");
var sent = await client.GetOutput<bool>(email.InvocationId);
```

Both lookups take an optional scope, which defaults to the scope bound by `WithScope`. The ingress
derives the invocation id from the target instead of reading storage, so a lookup succeeds for a
target that was never invoked; that absence shows up as a `RestateIngressException` with status 404
on the following `Attach`. `WithScope` and the lookups need restate-server 1.7 or later.

See [Scope and Limit Key](scope-and-limit-key.md) for the flow-control options and
[Ingress Client Errors](ingress-errors.md) for what a failed ingress request reports.

## Futures and Combinators

```csharp
// Non-blocking futures and combinators
var f1 = ctx.RunAsync<int>("a", () => Task.FromResult(1));
var f2 = ctx.RunAsync<int>("b", () => Task.FromResult(2));
var results = await ctx.All(f1, f2);     // wait for all
var winner = await ctx.Race(f1, f2);     // first to complete
```

`All` throws the first failure in delivery order as soon as that failure is delivered, without
waiting for the futures still pending: the operations behind them stay journaled and complete
there. A future still pending when the invocation suspends is not a failure — the suspension
propagates instead, unless another future was delivered a failure, which is thrown in its place
because the journal replays it on the next attempt.

A context and the futures it hands out must not be used from more than one thread at a time:
concurrent context operations reorder the journal and break replay. `Task.WhenAny` and
`Task.WhenAll` over durable futures bypass the delivery ordering the combinators provide, so
compose futures with `All`, `Race`, `Any`, `AllSettled` or `WaitAll`.

## State

Virtual objects and workflows have durable key-value state accessed via `StateKey<T>`:

```csharp
private static readonly StateKey<int> Count = new("count");

var current = await ctx.Get(Count);
ctx.Set(Count, current + 1);
ctx.Clear("count");
ctx.ClearAll();
```

Every state operation also takes an optional entry name. The name labels the journal entry, so
the Restate UI shows `load-count` instead of an anonymous state read. It is observability only:
it does not change what is read or written, and omitting it records exactly the entry it always
did.

```csharp
var current = await ctx.Get(Count, "load-count");
ctx.Set(Count, current + 1, "bump-count");
ctx.Clear("count", "reset-count");
ctx.ClearAll("wipe-state");
var keys = await ctx.StateKeys("list-keys");
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
