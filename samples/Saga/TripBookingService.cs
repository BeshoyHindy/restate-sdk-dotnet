using Restate.Sdk;

namespace Saga;

/// <summary>
///     Demonstrates the Saga pattern (compensating transactions) using Restate.
///     Books a flight, hotel, and car rental. If any step fails, previously
///     completed bookings are undone in reverse order.
///
///     This pattern is common in distributed systems where you cannot use a
///     single database transaction across multiple services. Restate's durable
///     execution guarantees that compensations always run — even after crashes.
///
///     Pattern:
///     1. Book flight  → on failure: done (nothing to undo)
///     2. Book hotel   → on failure: cancel flight
///     3. Book car     → on failure: cancel hotel, cancel flight
/// </summary>
[Service]
public sealed class TripBookingService
{
    /// <summary>
    ///     Orchestrates a trip booking with saga-style compensation.
    ///     Each step is wrapped in <c>ctx.Run()</c> for durable execution.
    ///     Compensating actions are collected and executed on failure.
    /// </summary>
    [Handler]
    public async Task<TripBookingResult> Book(Context ctx, TripBookingRequest request)
    {
        // Compensations are stacked (LIFO) — last booking is cancelled first.
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
            // A step failed terminally — run compensations in reverse order.
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
