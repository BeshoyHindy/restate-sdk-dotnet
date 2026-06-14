using System.Buffers;
using System.Text.Json;
using Restate.Sdk.Endpoint;

namespace Restate.Sdk.FSharp;

/// <summary>
///     Strongly-typed, reflection-free builders for the four handler shapes. Each returns the same
///     <see cref="HandlerDefinition" /> the C# source generator would emit — a JSON input deserializer plus
///     a <see cref="HandlerInvoker" /> that casts the boxed instance/context/input and boxes the result —
///     but the casts and generics live in C# where they are painless. F# supplies the handler body as a
///     lambda whose argument types it infers from the target method, so the generated call site stays terse.
/// </summary>
public static class FsHandler
{
    private static Func<ReadOnlySequence<byte>, object?> Deserializer<TIn>()
        => data =>
        {
            var reader = new Utf8JsonReader(data);
            return JsonSerializer.Deserialize(ref reader, JsonSerde.SerializerOptions.GetTypeInfo(typeof(TIn)))
                   ?? throw new InvalidOperationException("Deserialization returned null");
        };

    /// <summary>Handler with an input payload and an output payload.</summary>
    public static HandlerDefinition InOut<TSvc, TCtx, TIn, TOut>(
        string name, bool shared, Func<TSvc, TCtx, TIn, Task<TOut>> body) where TCtx : Context
        => new()
        {
            Name = name,
            IsShared = shared,
            HasInput = true,
            HasOutput = true,
            InputDeserializer = Deserializer<TIn>(),
            Invoker = async (instance, context, input, _) =>
                (object?)await body((TSvc)instance, (TCtx)context, (TIn)input!).ConfigureAwait(false),
        };

    /// <summary>Handler with an input payload and no output.</summary>
    public static HandlerDefinition InUnit<TSvc, TCtx, TIn>(
        string name, bool shared, Func<TSvc, TCtx, TIn, Task> body) where TCtx : Context
        => new()
        {
            Name = name,
            IsShared = shared,
            HasInput = true,
            HasOutput = false,
            InputDeserializer = Deserializer<TIn>(),
            Invoker = async (instance, context, input, _) =>
            {
                await body((TSvc)instance, (TCtx)context, (TIn)input!).ConfigureAwait(false);
                return null;
            },
        };

    /// <summary>Handler with no input payload and an output payload.</summary>
    public static HandlerDefinition Out<TSvc, TCtx, TOut>(
        string name, bool shared, Func<TSvc, TCtx, Task<TOut>> body) where TCtx : Context
        => new()
        {
            Name = name,
            IsShared = shared,
            HasInput = false,
            HasOutput = true,
            Invoker = async (instance, context, _, _) =>
                (object?)await body((TSvc)instance, (TCtx)context).ConfigureAwait(false),
        };

    /// <summary>Handler with no input and no output.</summary>
    public static HandlerDefinition Unit<TSvc, TCtx>(
        string name, bool shared, Func<TSvc, TCtx, Task> body) where TCtx : Context
        => new()
        {
            Name = name,
            IsShared = shared,
            HasInput = false,
            HasOutput = false,
            Invoker = async (instance, context, _, _) =>
            {
                await body((TSvc)instance, (TCtx)context).ConfigureAwait(false);
                return null;
            },
        };
}

/// <summary>
///     Builds and registers a <see cref="ServiceDefinition" /> for a service type, mirroring the source
///     generator's module initializer. Call these once at startup (by hand, or from the code emitted by
///     the Restate.Sdk.FSharp.Myriad generator) before hosting.
/// </summary>
public static class FsService
{
    /// <summary>Registers a stateless <c>Service</c>.</summary>
    public static void Service<TSvc>(string name, params HandlerDefinition[] handlers) where TSvc : class
        => Register<TSvc>(name, ServiceType.Service, handlers);

    /// <summary>Registers a keyed <c>VirtualObject</c>.</summary>
    public static void VirtualObject<TSvc>(string name, params HandlerDefinition[] handlers) where TSvc : class
        => Register<TSvc>(name, ServiceType.VirtualObject, handlers);

    /// <summary>Registers a keyed <c>Workflow</c>.</summary>
    public static void Workflow<TSvc>(string name, params HandlerDefinition[] handlers) where TSvc : class
        => Register<TSvc>(name, ServiceType.Workflow, handlers);

    private static void Register<TSvc>(string name, ServiceType type, HandlerDefinition[] handlers) where TSvc : class
        => ServiceDefinitionRegistry.Register<TSvc>(new ServiceDefinition
        {
            Name = name,
            Type = type,
            Factory = sp => sp.GetService(typeof(TSvc))
                            ?? throw new InvalidOperationException($"Service '{typeof(TSvc).Name}' is not registered in DI"),
            Handlers = handlers,
        });
}
