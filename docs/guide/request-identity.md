# Request Identity Verification

A Restate Endpoint is an ordinary HTTP server: whatever can reach it can drive your handlers.
Request identity closes that gap. The Restate server signs every request it sends to the
Endpoint with an Ed25519 key, and the SDK verifies that signature before the request reaches
routing.

Verification is opt-in. Configure at least one public key and every request to `/discover` and
`/invoke/{service}/{handler}` must be signed; configure none and requests are not verified at all.

## Configure the keys

`restate-server` prints the public half of its request-signing key on startup, as a
`publickeyv1_<base58>` string. Hand that value to the Endpoint.

Self-hosted:

```csharp
using Restate.Sdk.Hosting;

await RestateHost.CreateBuilder()
    .AddService<GreeterService>()
    .WithIdentityKeys("publickeyv1_kgERWXbLfq6MHcWLd86a5dpmSvM86QhQrQjL2gGPbnD")
    .Build()
    .RunAsync();
```

ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);

var identityKeys = builder.Configuration.GetSection("Restate:IdentityKeys").Get<string[]>() ?? [];

builder.Services.AddRestate(opts =>
{
    opts.AddService<GreeterService>();

    // No keys configured (the local default) means no verification at all.
    if (identityKeys.Length > 0)
        opts.WithIdentityKeys(identityKeys);
});

var app = builder.Build();
app.MapRestate();
await app.RunAsync();
```

AWS Lambda — call `WithIdentityKeys` from `Register()`, not from a constructor body. The identity
verifier is built during handler construction, so keys added later would be silently ignored and
the handler throws `InvalidOperationException` instead:

```csharp
using Restate.Sdk;

public class Handler : RestateLambdaHandler
{
    public override void Register()
    {
        Bind<GreeterService>();

        var key = Environment.GetEnvironmentVariable("RESTATE_IDENTITY_KEY");
        if (!string.IsNullOrEmpty(key))
            WithIdentityKeys(key);
    }
}
```

Keys are parsed and validated at the configuring call site, so a typo fails fast with
`ArgumentException` rather than at the first invocation. A key must carry the `publickeyv1_`
prefix, decode as base58, and decode to exactly 32 bytes. Calling `WithIdentityKeys()` with no
keys also throws — that would otherwise read as "identity configured" while silently leaving the
Endpoint open.

`WithIdentityKeys` accepts several keys and is additive across calls, which is what makes rotation
possible: add the new key, let Restate switch over, then drop the old one.

## What the SDK verifies

Restate sends two headers:

| Header | Value |
|--------|-------|
| `x-restate-signature-scheme` | `v1` for signed requests, `unsigned` otherwise |
| `x-restate-jwt-v1` | A compact JWT signed with the Ed25519 private key |

With keys configured, only the `v1` scheme is accepted: `unsigned`, missing, and unknown schemes
are all rejected. The JWT then has to satisfy all of the following.

- The JOSE header carries `alg` of `EdDSA`. `none`, `HS256`, and anything else are rejected.
- The header's `kid` matches one of the configured keys. The `kid` is the *full* serialized key,
  `publickeyv1_` prefix included.
- The Ed25519 signature verifies against that key.
- The payload carries both `exp` and `nbf`, and they bracket the current time. There is no
  clock-skew allowance, so the Endpoint's clock has to track the Restate server's.
- The payload's `aud` equals the request path (see below). A single-element array is accepted;
  a multi-element `aud` is not.

Verification never throws: any malformed token is simply a failed verification.

## Rejection is early and silent

The identity check runs before anything else touches the request — before routing, before
discovery, before the service registry is consulted. A request that fails it gets `401
Unauthorized` with an empty body, so an unsigned probe cannot tell an existing handler from a
missing one: `POST /invoke/Unknown/Handler` is a `401` when unsigned and a `404` once signed.

Repeated identity headers are rejected too. Two `x-restate-signature-scheme` values are ambiguous,
and the SDK refuses rather than picking one.

## The audience is the full request path

The `aud` claim is the path Restate dialled, and that is what the SDK compares against — including
the ASP.NET Core `PathBase`. Mounting the Endpoint under a prefix does not change this; the token
still has to be signed for the prefixed path.

```csharp
var app = builder.Build();
app.UsePathBase("/restate");
app.UseRouting();
app.MapRestate();
await app.RunAsync();
```

With that host, a signed discovery request has `aud` of `/restate/discover`, not `/discover`. If
you register the deployment with Restate under the same prefix — which you must, for the requests
to arrive at all — this lines up on its own. It only bites when a proxy in front of the Endpoint
rewrites the path after Restate has signed it.

On AWS Lambda the audience is the request path reported by API Gateway. Identity headers there are
matched case-insensitively, because API Gateway's header casing varies.

## Developing locally without identity

Configure no keys. A Restate server that is not signing its requests leaves them unsigned, and an
Endpoint with no keys accepts them — exactly the behaviour the
[Getting Started](getting-started.md) quick start relies on.

Drive that from configuration rather than from an `#if DEBUG`: bind the key list as in the
ASP.NET Core sample above, leave `Restate:IdentityKeys` out of `appsettings.Development.json`, and
supply it in production. The same binary is then verified in production and open locally, with no
compile-time switch to get wrong.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Every request is `401`, discovery included | Restate is not signing, or the Endpoint holds the wrong public key |
| `401` only after adding a path prefix or a reverse proxy | The path Restate signed is not the path the Endpoint sees, so `aud` no longer matches |
| Works for a while, then `401` | Endpoint and server clocks have drifted past the token's `exp` |
| `ArgumentException` at startup | The key is malformed — check the `publickeyv1_` prefix and that the value was copied whole |
