namespace NativeAotSaga;

/// <summary>
///     Simulates external booking APIs (flight, hotel, car rental).
///     Car rental has a 20% random failure rate to demonstrate saga compensation.
/// </summary>
public static class BookingApi
{
    public static Task<string> BookFlight(FlightRequest request)
    {
        Console.WriteLine(
            $"  [API] Booking flight {request.From} -> {request.To} on {request.Date}"
        );
        return Task.FromResult($"FL-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}");
    }

    public static Task CancelFlight(string confirmationId)
    {
        Console.WriteLine($"  [API] Cancelling flight {confirmationId}");
        return Task.CompletedTask;
    }

    public static Task<string> BookHotel(HotelRequest request)
    {
        Console.WriteLine(
            $"  [API] Booking hotel in {request.City} from {request.CheckIn} to {request.CheckOut}"
        );
        return Task.FromResult($"HT-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}");
    }

    public static Task CancelHotel(string confirmationId)
    {
        Console.WriteLine($"  [API] Cancelling hotel {confirmationId}");
        return Task.CompletedTask;
    }

    public static Task<string> BookCarRental(CarRentalRequest request)
    {
        Console.WriteLine(
            $"  [API] Booking car in {request.City} from {request.PickUp} to {request.DropOff}"
        );

        // Simulate occasional failures to demonstrate compensation
        if (Random.Shared.Next(100) < 20)
            throw new InvalidOperationException("Car rental service temporarily unavailable");

        return Task.FromResult($"CR-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}");
    }

    public static Task CancelCarRental(string confirmationId)
    {
        Console.WriteLine($"  [API] Cancelling car rental {confirmationId}");
        return Task.CompletedTask;
    }
}
