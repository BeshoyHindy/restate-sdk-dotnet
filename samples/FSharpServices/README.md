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
members and emits `Services.Generated.fs` (the `FsService.*` registration).

`Services.Generated.fs` is **regenerated on every build** by Myriad.Sdk's MSBuild integration — the
`<Compile Include="Services.Generated.fs"><MyriadFile>Services.fs</MyriadFile></Compile>` item in the
`.fsproj` runs the generator before compile. A `PackageDownload` pulls the `myriad` CLI into the NuGet
cache during `dotnet restore`, so a plain `dotnet build` works with no extra steps. The file is also
committed, so a build that skips the generator still compiles. Edit `Services.fs`, build, done.

> Myriad 0.85.0 is the latest published release (the mis-tagged 0.8.5); its `PreferSharedTypes` plugin
> loader shares the runner's `FSharp.Core`/`Fantomas` with the plugin, so no version pinning is needed,
> and its CLI rolls forward (net9 → net10) automatically.

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

## End-to-end test

The F# sample is a first-class member of the repo's E2E suite, alongside the C# samples
(`test/Restate.Sdk.E2E/FSharpServicesE2eTests`). It hosts the sample in-process (via `Program.buildHost`)
behind a **real `restate-server` Testcontainer** and drives it through the ingress, so every invocation
travels the full real path (`ingress → restate-server → F# endpoint → back`):

```sh
dotnet test test/Restate.Sdk.E2E -c Release --filter "FullyQualifiedName~FSharpServicesE2eTests"
```

It asserts:

1. Virtual Object durable per-key state (`Add` accumulates, `Reset` clears),
2. Saga happy path (all three bookings confirmed),
3. Saga compensation (terminal failure → `409` + bookings rolled back in reverse),
4. Workflow **suspend → external awakeable resolve → resume → completed**.

These are `[DockerFact]` tests (skipped when Docker is absent), self-contained and CI-runnable — no
cluster required.

## Deploy as a pod (parity with the C# samples)

`k8s/restate-fsharp.yaml` is a `RestateDeployment` (knative) matching the other samples. It needs the
container image published first:

```sh
dotnet publish samples/FSharpServices -c Release -t:PublishContainer \
  -p:ContainerRegistry=ghcr.io -p:ContainerRepository=<owner>/restate-sample-fsharpservices
kubectl apply -f samples/FSharpServices/k8s/restate-fsharp.yaml
```
