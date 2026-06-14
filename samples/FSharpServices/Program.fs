module Restate.Sdk.FSharp.Samples.Program

open System.Text.Json
open System.Text.Json.Serialization.Metadata
open Restate.Sdk
open Restate.Sdk.Hosting
open Restate.Sdk.FSharp.Samples

// Three Restate services authored in F# — a Saga (stateless Service), a durable Counter
// (Virtual Object), and a signup Workflow — hosted on one HTTP/2 endpoint.
//
//   restate deployments register http://localhost:9080
//   restate invocations invoke CounterObject/c1 Add --body '5'
//   restate invocations invoke TripBookingService Book --body '{ "tripId": "t1", ... }'
[<EntryPoint>]
let main argv =
  // User-payload JSON: camelCase + case-insensitive, matching the Restate ingress convention. An
  // explicit resolver is required because the input deserializers call GetTypeInfo on these options.
  let json = JsonSerializerOptions(JsonSerializerDefaults.Web)
  json.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
  JsonSerde.Configure(json)

  // Mirror the source generator's module initializer: register the hand-built definitions.
  Services.registerAll ()

  let port =
    match argv with
    | [| value |] -> int value
    | _ -> 9080

  RestateHost
    .CreateBuilder()
    .AddService<Services.TripBookingService>()
    .AddVirtualObject<Services.CounterObject>()
    .AddWorkflow<Services.SignupWorkflow>()
    .WithPort(port)
    .Build()
    .RunAsync()
    .GetAwaiter()
    .GetResult()

  0
