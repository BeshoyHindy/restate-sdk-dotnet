# F# Restate services

Three Restate services authored in **F#** on one HTTP/2 endpoint:

| Kind            | Service              | Shows                                                            |
| --------------- | -------------------- | --------------------------------------------------------------- |
| Service (Saga)  | `TripBookingService` | compensating transactions — roll back completed bookings in reverse on failure |
| Virtual Object  | `CounterObject`      | durable per-key state, exclusive writes + shared reads          |
| Workflow        | `SignupWorkflow`     | run-once lifecycle + an awakeable the workflow suspends on until an external event |

## How F# gets what the C# source generator gives C#

The C# samples rely on the **Restate Roslyn source generator** (`[Service]`/`[Handler]` attributes →
a `ServiceDefinition` registered at module-init). Roslyn source generators **do not run for F#
projects**, so F# is served by two companion projects that mirror that split:

| Concern | C# | F# |
| --- | --- | --- |
| runtime glue | (inline) | **`src/Restate.Sdk.FSharp`** — a C# helper library |
| compile-time codegen | `src/Restate.Sdk.Generators` (Roslyn) | **`src/Restate.Sdk.FSharp.Myriad`** (Myriad) |

**`Restate.Sdk.FSharp`** moves the awkward runtime glue into C#, where generics and System.Text.Json
are painless:

- `DurableContextExtensions` — `ctx.RunStep`/`RunStepUnit`/`GetAsync`/`SetState`/`NewAwakeable`/… return
  `Task` (not `ValueTask`, which F#'s `task { }` binds awkwardly) and have distinct names so F# never has
  to disambiguate `Func<Task<T>>` from `Func<T>`. The receiver is the *base* context type, so a
  `WorkflowContext` flows in by ordinary inheritance — side-stepping F#'s lack of implicit argument
  upcasting. (`RunStep`, not `RunAsync`, because the SDK already has a `Context.RunAsync` returning a
  detached future.)
- `FsHandler.InOut/InUnit/Out/Unit` + `FsService.Service/VirtualObject/Workflow` — strongly-typed,
  reflection-free builders that produce the exact `HandlerDefinition`/`ServiceDefinition` the C#
  generator emits.

So `Services.fs` is just attributed handler classes calling `ctx.RunStep(...)` — no binding boilerplate.

**`Restate.Sdk.FSharp.Myriad`** is a [Myriad](https://github.com/MoiraeSoftware/myriad) generator (the
idiomatic F# analog of a Roslyn source generator: AST in → F# source out). It scans the
`[<Service>]`/`[<VirtualObject>]`/`[<Workflow>]` types and their `[<Handler>]`/`[<SharedHandler>]`
members and emits `Services.Generated.fs` (the `FsService.*` registration). The generated file is
checked in; regenerate it with:

```sh
deno run --allow-run --allow-env --allow-read samples/FSharpServices/regenerate.ts
```

> The Myriad 0.8.3 CLI is a net6.0 tool, so `regenerate.ts` launches it with
> `DOTNET_ROLL_FORWARD=LatestMajor`, and the plugin pins `FSharp.Core` 6.0.x so its Fantomas 6.1.1 calls
> line up with the tool's own copies (0.8.5+ makes this automatic via `PreferSharedTypes`).

Two project settings still differ from the C# samples (`FSharpServices.fsproj`): `FSharp.Core` is
referenced explicitly (the SDK's in-box copy is a compiler asset, not deployed), and
`<Nullable>disable</Nullable>` opts out of F#'s noisy nullness checking against the C#-nullable SDK
surface.

## Run it locally

```sh
dotnet run --project samples/FSharpServices            # listens on :9080 (h2c)
restate deployments register http://localhost:9080

restate invocations invoke CounterObject/c1 Add --body '5'      # -> 5
restate invocations invoke CounterObject/c1 Add --body '3'      # -> 8
restate invocations invoke CounterObject/c1 Get                 # -> 8

restate invocations invoke TripBookingService Book --body '{
  "tripId":"t1","userId":"alice",
  "flight":{"from":"SFO","to":"JFK","date":"2026-03-15"},
  "hotel":{"city":"NYC","checkIn":"2026-03-15","checkOut":"2026-03-18"},
  "carRental":{"city":"NYC","pickUp":"2026-03-15","dropOff":"2026-03-18"}}'
```

A car-rental `city` of `"FAILVILLE"` makes the saga fail terminally and roll back the flight + hotel
(a deterministic trigger so the compensation path is testable).

## End-to-end test on local k0s

`e2e/k0s-e2e.ts` runs the sample against a **real local k0s** cluster that already runs the
restate-operator + a `RestateCluster` named `restate` (the same cluster the other samples deploy to):

```sh
deno run --allow-run --allow-net --allow-env samples/FSharpServices/e2e/k0s-e2e.ts
```

Because importing a locally-built image into k0s's root-owned containerd needs privileges the test does
not assume, it runs the SDK as a host process and registers it with the in-cluster restate-server using
the node's reachable IP — so the **server dials back into the host** and drives the handlers. Every
invocation travels the full real path (`ingress → restate-server → F# SDK → back`). It asserts:

1. Virtual Object durable per-key state (`Add` accumulates, `Reset` clears),
2. Saga happy path (all three bookings confirmed),
3. Saga compensation (terminal failure → `409` + bookings rolled back in reverse),
4. Workflow **suspend → external awakeable resolve → resume → completed**.

## Deploy as a pod (parity with the C# samples)

`k8s/restate-fsharp.yaml` is a `RestateDeployment` (knative) matching the other samples. It needs the
container image published first:

```sh
dotnet publish samples/FSharpServices -c Release -t:PublishContainer \
  -p:ContainerRegistry=ghcr.io -p:ContainerRepository=<owner>/restate-sample-fsharpservices
kubectl apply -f samples/FSharpServices/k8s/restate-fsharp.yaml
```
