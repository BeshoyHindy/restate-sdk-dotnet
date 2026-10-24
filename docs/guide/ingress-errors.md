# Ingress Client Errors

Every operation on `RestateClient` throws `RestateIngressException` when the response carries a
non-success status — calls, sends, `Attach`, `GetOutput`, the lookups, the signal completions, and
`Cancel`, which addresses the admin API rather than the ingress but reports a failure the same way.

```csharp
using Restate.Sdk.Client;

try
{
    var greeting = await client.Service("GreeterService").Call<string>("Greet", "World");
}
catch (RestateIngressException ex)
{
    logger.LogError(ex, "Ingress call failed with {Status} from {Source}",
        ex.StatusCode, ex.ErrorSource ?? "an unreported source");
}
```

## What the exception carries

| Member | Value |
|--------|-------|
| `StatusCode` | The HTTP status of the response, as a `HttpStatusCode?` inherited from `HttpRequestException` |
| `ErrorSource` | Where the failure came from: `ingress` for one the ingress produced itself, `invocation` for one that came back from the handler. Null when the server did not report one |
| `ErrorCode` | The Restate error code, such as `RT0003`. Null when the server did not report one |
| `Message` | The server's error message, the raw response body when it is not JSON, or a description of the status code when the response had no body |

`ErrorSource` is the property that answers "whose fault is this?". An unknown service, a rejected
payload or an overloaded ingress are reported as `ingress`; a handler that failed durably — a
`TerminalException`, for instance — is reported as `invocation`. Whatever the server reports is
passed through verbatim. The property is named `ErrorSource` because `Exception.Source` already
means the application or object that raised the exception.

`ErrorSource` is read from the `x-restate-error-source` response header, or from the `source` field
of the JSON error body; the header wins when both are present. `ErrorCode` and `Message` come from
that body's `code` and `message` fields. A `code` the server sends as a JSON number is carried as
its text — `"500"` — instead of failing the parse and losing the rest of the body.

## It derives from HttpRequestException

`RestateIngressException` extends `HttpRequestException`, so code written against what
`EnsureSuccessStatusCode` used to throw here keeps working, and `StatusCode` is the inherited
property:

```csharp
try
{
    await client.Service("GreeterService").Call<string>("Greet", "World");
}
catch (HttpRequestException ex)
{
    // Still catches every ingress failure, typed or not.
}
```

Catch `RestateIngressException` first when both handlers are present — a transport failure that
never reached the ingress (a refused connection, a DNS failure, a timeout) surfaces as a plain
`HttpRequestException`, with no Restate error source to report.

## Older servers report less

`ErrorSource` and `ErrorCode` need **restate-server 1.7.4 or newer**, which is where the error
source header and the structured error body arrived. Against an older server the exception is still
thrown, still carries the status and the message, and reports both as null. Treat them as
diagnostics, not as control flow:

```csharp
try
{
    await client.Service("GreeterService").Call<string>("Greet", "World");
}
catch (RestateIngressException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
{
    // Branch on the status, which every server version reports.
}
```

Nothing on the failure path throws on its own: an unreadable, non-JSON or truncated body never
masks the failure it describes. The body becomes the message when it cannot be parsed, and the
status alone is reported when it cannot be read at all.

## What a failure looks like

A failure the ingress produced itself, from restate-server 1.7.4 or newer:

| Member | Value |
|--------|-------|
| `StatusCode` | `InternalServerError` |
| `ErrorSource` | `ingress` |
| `ErrorCode` | `RT0003` |
| `Message` | `service not found` |

The same call against a server that reports neither the header nor the structured body:

| Member | Value |
|--------|-------|
| `StatusCode` | `InternalServerError` |
| `ErrorSource` | `null` |
| `ErrorCode` | `null` |
| `Message` | `boom` — whatever the body said |

A response with no body at all, where the message describes the status instead:

| Member | Value |
|--------|-------|
| `StatusCode` | `ServiceUnavailable` |
| `Message` | `The Restate ingress request failed with status 503 (ServiceUnavailable).` |

## Failures that are not ingress failures

Not every failure the client raises is an ingress failure:

- `ArgumentException` / `ArgumentNullException`, before any request is made, for an empty invocation
  id, service name, handler name or signal id, and for a
  [limit key without a scope](scope-and-limit-key.md).
- `InvalidOperationException` from `Cancel` when the client has no `AdminAddress` configured — the
  admin API is the only place cancellation lives, so the client refuses rather than sending a
  request no server version answers.
- `JsonException` when a send or a lookup answered with a success status but no `invocationId` in
  the body: the request was accepted, so there is no failure to report, but there is no handle to
  hand back either.
- A plain `HttpRequestException` for a transport failure that never reached the ingress.

A lookup is worth calling out separately. The ingress derives the invocation id from the target
instead of reading storage, so looking up a workflow key or an idempotency key that has never been
invoked still succeeds; the absence surfaces as a `RestateIngressException` with status `NotFound`
on the following `Attach` or `GetOutput`.

## See also

- [Scope and Limit Key](scope-and-limit-key.md) — the options an ingress call can carry
- [Integration Testing with Testcontainers](testcontainers.md) — the harness exposes a
  `RestateClient` against a real server
- The [ScopedClient sample](https://github.com/BeshoyHindy/restate-sdk-dotnet/tree/main/samples/ScopedClient)
  — prints what each property reports for a failure from the ingress and one from a handler
