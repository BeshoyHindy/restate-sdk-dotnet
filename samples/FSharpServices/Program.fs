module Restate.Sdk.FSharp.Samples.Program

open System.Text.Json
open System.Text.Json.Serialization.Metadata
open Restate.Sdk
open Restate.Sdk.Hosting
open Restate.Sdk.FSharp.Samples

// Three Restate services authored in F# — a Saga (stateless Service), a durable Counter
// (Virtual Object), and a signup Workflow — hosted on one HTTP/2 endpoint. Handler discovery and
// registration are generated into Services.Generated.fs by the Restate.Sdk.FSharp.Myriad generator;
// the runtime glue lives in the Restate.Sdk.FSharp C# helper library.
[<EntryPoint>]
let main argv =
  // User-payload JSON: camelCase + case-insensitive, matching the Restate ingress convention. An
  // explicit resolver is required because the input deserializers call GetTypeInfo on these options.
  let json = JsonSerializerOptions(JsonSerializerDefaults.Web)
  json.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
  JsonSerde.Configure(json)

  // Register the generated service definitions, then bind them onto the host.
  Registrations.registerAll ()

  let port =
    match argv with
    | [| value |] -> int value
    | _ -> 9080

  let builder = Registrations.bind (RestateHost.CreateBuilder())
  builder.WithPort(port).Build().RunAsync().GetAwaiter().GetResult()

  0
