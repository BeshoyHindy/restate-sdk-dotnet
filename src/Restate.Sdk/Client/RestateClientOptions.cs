using System.Text.Json;

namespace Restate.Sdk.Client;

/// <summary>
///     Configuration options for <see cref="RestateClient" />.
/// </summary>
public sealed class RestateClientOptions
{
    private Uri? _adminAddress;
    private JsonSerializerOptions _jsonSerializerOptions = CreateDefaultJsonSerializerOptions();

    /// <summary>
    ///     Gets or sets the base address of the Restate admin API (port 9070 by default), which
    ///     serves the operations the ingress does not — currently
    ///     <see cref="RestateClient.Cancel" />. Defaults to <see langword="null" />, which leaves
    ///     those operations unavailable: they throw <see cref="InvalidOperationException" />.
    ///     <para>
    ///         A path on the address is kept as a prefix, with or without a trailing slash, so an
    ///         admin API behind a reverse proxy at <c>http://gateway/restate-admin</c> is addressed
    ///         correctly.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an absolute URI.</exception>
    public Uri? AdminAddress
    {
        get => _adminAddress;
        set
        {
            if (value is not null && !value.IsAbsoluteUri)
                throw new ArgumentException("The Restate admin API address must be an absolute URI.", nameof(value));

            _adminAddress = value;
        }
    }

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
