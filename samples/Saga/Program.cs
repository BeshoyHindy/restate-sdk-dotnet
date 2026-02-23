using Restate.Sdk.Hosting;
using Saga;

// Saga pattern: compensating transactions for distributed bookings.
// If any step fails, previously completed bookings are rolled back in reverse order.
//
// Test:
//   restate invocations invoke TripBookingService Book --body '{
//     "tripId": "trip-1",
//     "userId": "alice",
//     "flight": { "from": "SFO", "to": "JFK", "date": "2026-03-15" },
//     "hotel": { "city": "New York", "checkIn": "2026-03-15", "checkOut": "2026-03-18" },
//     "carRental": { "city": "New York", "pickUp": "2026-03-15", "dropOff": "2026-03-18" }
//   }'
await RestateHost
    .CreateBuilder()
    .WithPort(9086)
    .AddService<TripBookingService>()
    .Build()
    .RunAsync();
