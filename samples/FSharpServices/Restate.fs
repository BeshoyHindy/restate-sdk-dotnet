namespace Restate.Sdk.FSharp

open System
open System.Buffers
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Restate.Sdk
open Restate.Sdk.Endpoint

/// <summary>
///   A small, type-safe F# binding over the Restate .NET SDK's <c>ServiceDefinition</c> surface.
///
///   The C# SDK discovers <c>[Service]</c> / <c>[VirtualObject]</c> / <c>[Workflow]</c> classes with a
///   Roslyn source generator that emits a <c>ServiceDefinition</c> into <c>ServiceDefinitionRegistry</c>
///   at module-initialization time. Roslyn source generators do not run for F# projects, so this module
///   reproduces — by hand, with type inference doing the bookkeeping — exactly what the generator emits:
///   a per-handler <c>HandlerInvoker</c> that casts the base <c>Context</c> to the handler's concrete
///   context type, a JSON <c>InputDeserializer</c>, and a DI-resolving <c>Factory</c>.
///
///   The context helpers (<c>run</c>, <c>get</c>, <c>set</c>, ...) are generic over the context type with
///   a subtype constraint. That is deliberate: F# does not implicitly upcast arguments to let-bound
///   functions, so a helper typed on the base <c>Context</c> would reject a <c>WorkflowContext</c>. A
///   constrained generic accepts any context subtype and resolves the member on the constraint.
/// </summary>
[<RequireQualifiedAccess>]
module Durable =

  // --- Durable-context helpers (generic over the concrete context subtype) -------------------------

  // The explicit method type argument 'Run<'T>' pins the async overload: without it 'Func<Task<'T>>'
  // also unifies with the synchronous 'Run<'U>(string, Func<'U>)' (taking 'U = Task<'T>'), and F#
  // reports the call as an ambiguous overload. Fixing 'T rules the synchronous candidate out.

  /// Durable side effect returning a value — journaled once, replayed verbatim afterwards.
  let run<'C, 'T when 'C :> Context> (ctx: 'C) (name: string) (action: unit -> Task<'T>) : Task<'T> =
    ctx.Run<'T>(name, Func<Task<'T>>(action)).AsTask()

  /// Durable side effect with an explicit retry policy.
  let runRetry<'C, 'T when 'C :> Context>
      (ctx: 'C) (name: string) (policy: RetryPolicy) (action: unit -> Task<'T>) : Task<'T> =
    ctx.Run<'T>(name, Func<Task<'T>>(action), policy).AsTask()

  /// Durable side effect with no return value.
  let runUnit<'C when 'C :> Context> (ctx: 'C) (name: string) (action: unit -> Task) : Task =
    let result : ValueTask = ctx.Run(name, Func<Task>(action))
    result.AsTask()

  /// Reads durable per-key state (default value when unset).
  let get<'C, 'T when 'C :> SharedObjectContext> (ctx: 'C) (key: StateKey<'T>) : Task<'T> =
    ctx.Get(key).AsTask()

  /// Writes durable per-key state.
  let set<'C, 'T when 'C :> ObjectContext> (ctx: 'C) (key: StateKey<'T>) (value: 'T) : unit =
    ctx.Set(key, value)

  /// Clears every state key for this object/workflow key.
  let clearAll<'C when 'C :> ObjectContext> (ctx: 'C) : unit = ctx.ClearAll()

  /// Lists every state key currently set for this key.
  let stateKeys<'C when 'C :> SharedObjectContext> (ctx: 'C) : Task<string[]> = ctx.StateKeys().AsTask()

  /// Creates a durable promise (awakeable) that an external caller can resolve or reject by id.
  let awakeable<'C, 'T when 'C :> SharedObjectContext> (ctx: 'C) : Awakeable<'T> = ctx.Awakeable<'T>()

  // --- Handler combinators ------------------------------------------------------------------------

  // Mirrors the generated InputDeserializer: a typed JSON reader keyed off JsonSerde's options so the
  // managed and F# paths share one serialization configuration.
  let private deserializer<'I> () : Func<ReadOnlySequence<byte>, obj> =
    Func<ReadOnlySequence<byte>, obj>(fun data ->
      let mutable reader = Utf8JsonReader(data)
      JsonSerializer.Deserialize(&reader, JsonSerde.SerializerOptions.GetTypeInfo(typeof<'I>)))

  let private noDeserializer : Func<ReadOnlySequence<byte>, obj> =
    Unchecked.defaultof<Func<ReadOnlySequence<byte>, obj>>

  let private make
      (name: string) (isShared: bool) (hasInput: bool) (hasOutput: bool)
      (deser: Func<ReadOnlySequence<byte>, obj>)
      (invoke: obj -> Context -> obj -> Task<obj>) : HandlerDefinition =
    HandlerDefinition(
      Name = name,
      IsShared = isShared,
      HasInput = hasInput,
      HasOutput = hasOutput,
      InputDeserializer = deser,
      Invoker = HandlerInvoker(fun instance ctx input _ct -> invoke instance ctx input))

  /// Exclusive handler with an input payload and an output payload.
  let handler<'S, 'C, 'I, 'O when 'C :> Context>
      (name: string) (body: 'S -> 'C -> 'I -> Task<'O>) : HandlerDefinition =
    make name false true true (deserializer<'I> ())
      (fun instance ctx input ->
        task {
          let! result = body (instance :?> 'S) (ctx :?> 'C) (input :?> 'I)
          return box result
        })

  /// Shared (concurrent / read-only) handler with an input payload and an output payload.
  let sharedHandler<'S, 'C, 'I, 'O when 'C :> Context>
      (name: string) (body: 'S -> 'C -> 'I -> Task<'O>) : HandlerDefinition =
    make name true true true (deserializer<'I> ())
      (fun instance ctx input ->
        task {
          let! result = body (instance :?> 'S) (ctx :?> 'C) (input :?> 'I)
          return box result
        })

  /// Exclusive handler with no input payload, returning a value.
  let func<'S, 'C, 'O when 'C :> Context>
      (name: string) (body: 'S -> 'C -> Task<'O>) : HandlerDefinition =
    make name false false true noDeserializer
      (fun instance ctx _ ->
        task {
          let! result = body (instance :?> 'S) (ctx :?> 'C)
          return box result
        })

  /// Shared (concurrent / read-only) handler with no input payload, returning a value.
  let sharedFunc<'S, 'C, 'O when 'C :> Context>
      (name: string) (body: 'S -> 'C -> Task<'O>) : HandlerDefinition =
    make name true false true noDeserializer
      (fun instance ctx _ ->
        task {
          let! result = body (instance :?> 'S) (ctx :?> 'C)
          return box result
        })

  /// Exclusive handler with no input and no output (a fire-and-forget mutation).
  let action<'S, 'C when 'C :> Context>
      (name: string) (body: 'S -> 'C -> Task<unit>) : HandlerDefinition =
    make name false false false noDeserializer
      (fun instance ctx _ ->
        task {
          do! body (instance :?> 'S) (ctx :?> 'C)
          return Unchecked.defaultof<obj>
        })

  // --- Service-definition builders ----------------------------------------------------------------

  let private definition<'S>
      (name: string) (kind: ServiceType) (handlers: HandlerDefinition list) : ServiceDefinition =
    ServiceDefinition(
      Name = name,
      Type = kind,
      Factory = Func<IServiceProvider, obj>(fun sp ->
        let instance = sp.GetService(typeof<'S>)
        if isNull instance then
          raise (InvalidOperationException($"Service '{typeof<'S>.Name}' is not registered in DI"))
        else instance),
      Handlers = (List.toArray handlers :> IReadOnlyList<HandlerDefinition>))

  /// Builds a stateless <c>Service</c> definition for the marker type <typeparamref name="'S" />.
  let service<'S> (name: string) (handlers: HandlerDefinition list) : ServiceDefinition =
    definition<'S> name ServiceType.Service handlers

  /// Builds a keyed <c>VirtualObject</c> definition for the marker type <typeparamref name="'S" />.
  let virtualObject<'S> (name: string) (handlers: HandlerDefinition list) : ServiceDefinition =
    definition<'S> name ServiceType.VirtualObject handlers

  /// Builds a keyed <c>Workflow</c> definition for the marker type <typeparamref name="'S" />.
  let workflow<'S> (name: string) (handlers: HandlerDefinition list) : ServiceDefinition =
    definition<'S> name ServiceType.Workflow handlers

  /// Registers a definition under its marker type, mirroring the generator's module initializer.
  let register<'S> (def: ServiceDefinition) : unit = ServiceDefinitionRegistry.Register<'S>(def)
