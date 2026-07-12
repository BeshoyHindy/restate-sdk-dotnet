using System.Text.Json;

namespace Restate.Sdk.Client;

/// <summary>
///     Configuration options for <see cref="RestateClient" />.
/// </summary>
public sealed class RestateClientOptions
{
    private JsonSerializerOptions _jsonSerializerOptions = CreateDefaultJsonSerializerOptions();

    /// <summary>
    ///     Gets or sets the options used by reflection-based client overloads to serialize requests
    ///     and deserialize responses. Defaults to camelCase property names for compatibility.
    ///     Overloads that accept <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}" />
    ///     use the supplied type metadata instead.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null" />.</exception>
    public JsonSerializerOptions JsonSerializerOptions
    {
        get => _jsonSerializerOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _jsonSerializerOptions = value;
        }
    }

    private static JsonSerializerOptions CreateDefaultJsonSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
