using Restate.Sdk;

namespace NativeAotSaga;

/// <summary>
///     Demonstrates the Saga pattern (compensating transactions) using Restate,
///     compiled with NativeAOT.
/// </summary>
[Service]
public sealed class TripBookingService
{
    [Handler]
    public async Task<TripBookingResult> Book(Context ctx, TripBookingRequest request)
    {
        var compensations = new List<Func<Context, Task>>();

        try
        {
            // Step 1: Book flight
            ctx.Console.Log($"Booking flight for trip {request.TripId}...");
            var flightConfirmation = await ctx.Run(
                "book-flight",
                () => BookingApi.BookFlight(request.Flight)
            );

            compensations.Add(
                async (c) =>
                {
                    c.Console.Log($"Compensating: cancelling flight {flightConfirmation}");
                    await c.Run("cancel-flight", () => BookingApi.CancelFlight(flightConfirmation));
                }
            );

            // Step 2: Book hotel
            ctx.Console.Log($"Booking hotel for trip {request.TripId}...");
            var hotelConfirmation = await ctx.Run(
                "book-hotel",
                () => BookingApi.BookHotel(request.Hotel)
            );

            compensations.Add(
                async (c) =>
                {
                    c.Console.Log($"Compensating: cancelling hotel {hotelConfirmation}");
                    await c.Run("cancel-hotel", () => BookingApi.CancelHotel(hotelConfirmation));
                }
            );

            // Step 3: Book car rental (may fail — demonstrates compensation)
            ctx.Console.Log($"Booking car rental for trip {request.TripId}...");
            var carConfirmation = await ctx.Run(
                "book-car-rental",
                () => BookingApi.BookCarRental(request.CarRental),
                RetryPolicy.FixedAttempts(3)
            );

            ctx.Console.Log($"Trip {request.TripId} booked successfully!");
            return new TripBookingResult(
                request.TripId,
                flightConfirmation,
                hotelConfirmation,
                carConfirmation
            );
        }
        catch (TerminalException)
        {
            ctx.Console.Log(
                $"Trip {request.TripId} failed. Running {compensations.Count} compensation(s)..."
            );

            for (var i = compensations.Count - 1; i >= 0; i--)
                await compensations[i](ctx);

            ctx.Console.Log($"Trip {request.TripId} fully compensated.");
            throw;
        }
    }
}
