using System.Text.Json.Serialization;

namespace NativeAotGreeter;

/// <summary>
///     Source-generated JSON serializer context for NativeAOT support.
///     Lists all types that need JSON serialization/deserialization at runtime.
/// </summary>
[JsonSerializable(typeof(GreetRequest))]
[JsonSerializable(typeof(GreetResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext;
