using System.ComponentModel;

namespace Restate.Sdk.Endpoint;

/// <summary>
///     Registry for generated service definitions.
///     Source generators register service definitions at module initialization time.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ServiceDefinitionRegistry
{
    private static readonly Lock SyncRoot = new();
    private static Dictionary<Type, ServiceDefinition>? _definitions;

    /// <summary>
    ///     Registers a pre-built service definition for a given implementation type.
    ///     Called by generated code.
    /// </summary>
    public static void Register<TService>(ServiceDefinition definition)
    {
        lock (SyncRoot)
        {
            _definitions ??= [];
            _definitions[typeof(TService)] = definition;
        }
    }

    /// <summary>
    ///     Gets the service definition for <typeparamref name="TService" />.
    ///     Called by source-generated AOT registration code.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when no definition is found for <typeparamref name="TService" />.
    /// </exception>
    public static ServiceDefinition Get<TService>()
    {
        lock (SyncRoot)
        {
            if (_definitions is not null && _definitions.TryGetValue(typeof(TService), out var def))
                return def;
        }

        throw new InvalidOperationException(
            $"No generated service definition for '{typeof(TService).Name}'. Ensure the Restate source generator is enabled."
        );
    }

    internal static ServiceDefinition? TryGet(Type implementationType)
    {
        lock (SyncRoot)
        {
            if (_definitions is null)
                return null;
            _definitions.TryGetValue(implementationType, out var def);
            return def;
        }
    }
}
