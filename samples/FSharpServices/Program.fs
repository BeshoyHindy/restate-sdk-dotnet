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

/// <summary>
///   Builds the hosted endpoint on <paramref name="port" /> (0 = an OS-assigned ephemeral port). Shared
///   by the executable entry point and the E2E suite, which hosts the same services in-process behind a
///   real restate-server container (see test/Restate.Sdk.E2E/FSharpServicesFixture).
/// </summary>
let buildHost (port: int) =
  // User-payload JSON: camelCase + case-insensitive, matching the Restate ingress convention. An
  // explicit resolver is required because the input deserializers call GetTypeInfo on these options.
  let json = JsonSerializerOptions(JsonSerializerDefaults.Web)
  json.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
  JsonSerde.Configure(json)

  // Register the generated service definitions, then bind them onto the host.
  Registrations.registerAll ()
  let builder = Registrations.bind (RestateHost.CreateBuilder())
  builder.WithPort(port).Build()

[<EntryPoint>]
let main argv =
  let port =
    match argv with
    | [| value |] -> int value
    | _ -> 9080

  (buildHost port).RunAsync().GetAwaiter().GetResult()
  0
