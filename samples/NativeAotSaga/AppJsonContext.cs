using System.Text.Json.Serialization;

namespace NativeAotSaga;

[JsonSerializable(typeof(TripBookingRequest))]
[JsonSerializable(typeof(TripBookingResult))]
[JsonSerializable(typeof(FlightRequest))]
[JsonSerializable(typeof(HotelRequest))]
[JsonSerializable(typeof(CarRentalRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext;
