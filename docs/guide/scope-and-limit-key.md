# Scope and Limit Key

A **scope** is a named server-side concurrency limit. An invocation sent into a scope runs under
the limit configured for that scope on the Restate server, so a slow dependency behind one scope
cannot drain the capacity every other handler shares.

A **limit key** narrows that limit further, to the invocations inside the scope that share the same
key — one tenant, one customer, one device. The scope caps the total; the limit key caps each
tenant's share of it.

Both are optional, both are per-invocation, and both are set through `CallOptions` and
`SendOptions`.

## Setting them on a call or a send

`CallOptions.WithScope` and `SendOptions.WithScope` each have a one-argument form for a scope and a
two-argument form for a scope plus a limit key:

```csharp
// A call in a scope
var quote = await ctx.Call<Quote>("PricingService", "Quote", request,
    CallOptions.WithScope("pricing"));

// A call in a scope, narrowed to one tenant
var tenantQuote = await ctx.Call<Quote>("PricingService", "Quote", request,
    CallOptions.WithScope("pricing", "tenant-a"));

// A non-blocking call future takes the same options
var pending = ctx.CallFuture<Quote>("PricingService", "Quote", request,
    CallOptions.WithScope("pricing"));

// A one-way send
await ctx.Send("EmailService", "SendEmail", request, SendOptions.WithScope("notifications"));
```

The factories are a shorthand for the one case they name. Both types are `readonly record struct`s,
so an object initializer combines the scope with the other options — the idempotency key, and a
send's delay:

```csharp
var charge = await ctx.Call<Receipt>("PaymentService", "Charge", request,
    new CallOptions { Scope = "payments", LimitKey = "customer-7", IdempotencyKey = "order-123" });

await ctx.Send("ReminderService", "Remind", request,
    new SendOptions { Scope = "reminders", Delay = TimeSpan.FromHours(1) });
```

## Typed clients

The generated typed clients take the same options. A call client takes `CallOptions` per method:

```csharp
var pricing = ctx.ServiceClient<IPricingServiceClient>();
var quote = await pricing.QuoteAsync(request, CallOptions.WithScope("pricing", "tenant-a"));
var pending = pricing.QuoteFuture(request, CallOptions.WithScope("pricing"));
```

A send client takes `SendOptions` once, when the client is created, and every send it makes carries
them:

```csharp
var email = ctx.ServiceSendClient<IEmailServiceSendClient>(SendOptions.WithScope("notifications"));
await email.SendEmailSend(request);
```

## From outside the runtime

`RestateClient`, the ingress client, accepts the same `CallOptions` and `SendOptions`:

```csharp
using var client = new RestateClient("http://localhost:8080");

var quote = await client.Service("PricingService")
    .Call<Quote>("Quote", request, CallOptions.WithScope("pricing", "tenant-a"));

await client.Service("EmailService")
    .Send("SendEmail", request, SendOptions.WithScope("notifications"));
```

`WithScope` binds a scope to a derived client instead, so it does not have to be repeated on every
invocation. Every call, send and lookup the derived client makes runs in that scope:

```csharp
var pricing = client.WithScope("pricing");

var first = await pricing.Service("PricingService").Call<Quote>("Quote", request);
var second = await pricing.Service("PricingService").Call<Quote>("Quote", otherRequest);
```

A per-call scope still wins over the bound one, and a per-call limit key narrows the bound scope —
on a bound client the limit key no longer needs a scope beside it:

```csharp
// Runs in "promotions", not in the bound "pricing"
await pricing.Service("PricingService").Call<Quote>("Quote", request,
    CallOptions.WithScope("promotions"));

// Runs in the bound "pricing", narrowed to one tenant
await pricing.Service("PricingService").Call<Quote>("Quote", request,
    new CallOptions { LimitKey = "tenant-a" });
```

The derived client shares the `HttpClient` of the client it came from without owning it: disposing
the derived client leaves the original — and its pooled connections — usable, and the original
stays unscoped. It carries the rest of the configuration too, including the admin address, so
`Cancel` still works through it.

The lookups take a scope as well, because the invocation id the ingress derives depends on the
scope the target was invoked in. Pass it explicitly, or let the bound scope supply it:

```csharp
var signup = await client.LookupWorkflow("SignupWorkflow", "user-1", "signups");
var alsoSignup = await client.WithScope("signups").LookupWorkflow("SignupWorkflow", "user-1");
```

## A limit key needs a scope

The server honours a limit key only inside a scope, so a limit key on its own would be silently
ignored and the caller would lose the flow control it asked for. The SDK fails instead, with an
`ArgumentException` whose `ParamName` is `options`:

```
A limit key requires a scope; set Scope alongside LimitKey.
```

The check runs before anything is journaled or sent: on the ingress client the throw is
synchronous, and no HTTP request leaves the process. On a scope-bound client the bound scope
satisfies the rule.

## Server and protocol requirements

On the handler side, the scope and the limit key are fields on the call and one-way-call commands
that **service protocol V7** added. A V5 or V6 server parses the command without them, which again
means losing the flow control silently, so the SDK fails the operation with an `ArgumentException`
when the negotiated version is below V7:

```
A scope or limit key requires service protocol V7, but the negotiated version is V6. The server
must negotiate protocol V7 for this call (restate-server 1.8 does by default).
```

It is an `ArgumentException` and not a `TerminalException`: asking for an option the negotiated
stream cannot carry is a usage error in the handler, not an outcome of the invocation.

The protocol version is negotiated per invocation, from the request content type, so what matters
is the server in front of the handler. restate-server 1.8 negotiates V7 by default; 1.7 does so
only with V7 enabled; 1.6 does not offer it at all.

The ingress client negotiates nothing — a scope moves the request onto the versioned ingress route
that carries it in the path (`/restate/scope/{scope}/call/...`), and the limit key travels in the
`x-restate-limit-key` request header. Those routes need restate-server 1.7 or later, and so do the
lookups.

> restate-server 1.8 drops every client-supplied `x-restate-*` request header except
> `x-restate-limit-key`, which it reads before the drop. `RestateClient` sets that header itself;
> this only matters for callers that add `x-restate-*` headers of their own, through
> `HttpClient.DefaultRequestHeaders` or a `DelegatingHandler`.

## Replay

The scope and the limit key are part of the journaled call: they are compared on replay along with
the target service, handler and key. Changing the scope of a call between attempts is therefore a
journal mismatch, reported as a retryable error — pick the scope from data the handler already has
(the request, state, a `ctx.Run` result), not from configuration that can change under a running
invocation.

## Under test

`MockContext` applies the same limit-key rule, so a handler that asks for a limit key without a
scope fails in a unit test exactly as it does against a server. The recorded calls and sends carry
what the handler asked for:

```csharp
var ctx = new MockContext();
ctx.SetupCall("PricingService", "Quote", new Quote("widget", 2, 500));

await ctx.Call<Quote>("PricingService", "Quote", request,
    CallOptions.WithScope("pricing", "tenant-a"));

Assert.Equal("pricing", ctx.Calls[0].Scope);
Assert.Equal("tenant-a", ctx.Calls[0].LimitKey);
```

The mock has no negotiated protocol version, so it does not reproduce the V7 check. See the
[testing guide](testing.md) for the rest of the mock surface.

## See also

- [Durable Building Blocks](durable-building-blocks.md) — calls, sends and the rest of the context
- [Ingress Client Errors](ingress-errors.md) — what a rejected scoped invocation reports
- The [ScopedClient sample](https://github.com/BeshoyHindy/restate-sdk-dotnet/tree/main/samples/ScopedClient)
  — a scoped call from a handler, and an ingress client with the scope bound
