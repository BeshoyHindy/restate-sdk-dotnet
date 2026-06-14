# F# Restate services

Three Restate services authored in **F#** on one HTTP/2 endpoint:

| Kind            | Service              | Shows                                                            |
| --------------- | -------------------- | --------------------------------------------------------------- |
| Service (Saga)  | `TripBookingService` | compensating transactions — roll back completed bookings in reverse on failure |
| Virtual Object  | `CounterObject`      | durable per-key state, exclusive writes + shared reads          |
| Workflow        | `SignupWorkflow`     | run-once lifecycle + an awakeable the workflow suspends on until an external event |

## Why this sample is structured differently from the C# ones

The C# samples rely on the **Restate source generator** (`[Service]` / `[Handler]` attributes →
generated `ServiceDefinition` registered at module-init). Roslyn source generators **do not run for F#
projects**, so this sample reproduces what the generator emits, by hand, in `Restate.fs`:

- a per-handler `HandlerInvoker` that casts the base `Context` to the handler's concrete context type,
- a JSON `InputDeserializer` keyed off `JsonSerde`,
- a DI-resolving `Factory`,
- and `ServiceDefinitionRegistry.Register<'S>` calls (the generator's module initializer).

`Restate.fs` wraps all of that in a small, type-safe DSL (`Durable.handler`, `Durable.run`,
`Durable.get`, ...). The context helpers are generic with a `:> Context` constraint on purpose — F#
does not implicitly upcast arguments to let-bound functions, so a helper typed on the base `Context`
would reject a `WorkflowContext`.

Two project settings differ from the C# samples, both in `FSharpServices.fsproj`:

- **no** `Restate.Sdk.Generators` analyzer reference (it would be a no-op for F#),
- `FSharp.Core` is referenced explicitly (the SDK's in-box `FSharp.Core` is a compiler asset and is
  not copied to the build output), and `<Nullable>disable</Nullable>` opts out of F#'s (noisy) nullness
  checking against the C#-nullable-annotated SDK surface.

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
