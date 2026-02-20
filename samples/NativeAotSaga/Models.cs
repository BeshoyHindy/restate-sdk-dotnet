namespace NativeAotSaga;

/// <summary>Request to book a complete trip.</summary>
public record TripBookingRequest(
    string TripId,
    string UserId,
    FlightRequest Flight,
    HotelRequest Hotel,
    CarRentalRequest CarRental
);

/// <summary>Result of a completed trip booking.</summary>
public record TripBookingResult(
    string TripId,
    string? FlightConfirmation,
    string? HotelConfirmation,
    string? CarRentalConfirmation
);

public record FlightRequest(string From, string To, DateOnly Date);

public record HotelRequest(string City, DateOnly CheckIn, DateOnly CheckOut);

public record CarRentalRequest(string City, DateOnly PickUp, DateOnly DropOff);
