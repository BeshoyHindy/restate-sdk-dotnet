using System.Text.Json.Serialization;

namespace NativeAotCounter;

[JsonSerializable(typeof(int))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext;
