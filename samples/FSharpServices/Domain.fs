namespace Restate.Sdk.FSharp.Samples

open System
open System.Threading.Tasks
open Restate.Sdk

// ---------------------------------------------------------------------------------------------------
// Saga domain — a trip booking composed of three independent reservations.
// ---------------------------------------------------------------------------------------------------

type FlightRequest = { From: string; To: string; Date: DateOnly }
type HotelRequest = { City: string; CheckIn: DateOnly; CheckOut: DateOnly }
type CarRentalRequest = { City: string; PickUp: DateOnly; DropOff: DateOnly }

type TripBookingRequest =
  { TripId: string
    UserId: string
    Flight: FlightRequest
    Hotel: HotelRequest
    CarRental: CarRentalRequest }

type TripBookingResult =
  { TripId: string
    FlightConfirmation: string
    HotelConfirmation: string
    CarRentalConfirmation: string }

/// <summary>
///   Simulated external booking APIs. In a real application these would be REST/gRPC calls; here they
///   just return synthetic confirmation ids. The car-rental step can fail to exercise compensation.
/// </summary>
[<RequireQualifiedAccess>]
module BookingApi =

  let private confirmation (prefix: string) =
    $"{prefix}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpperInvariant()}"

  let bookFlight (request: FlightRequest) : Task<string> =
    Console.WriteLine($"  [API] Booking flight {request.From} -> {request.To} on {request.Date}")
    Task.FromResult(confirmation "FL")

  let cancelFlight (confirmationId: string) : Task =
    Console.WriteLine($"  [API] Cancelling flight {confirmationId}")
    Task.CompletedTask

  let bookHotel (request: HotelRequest) : Task<string> =
    Console.WriteLine($"  [API] Booking hotel in {request.City} from {request.CheckIn} to {request.CheckOut}")
    Task.FromResult(confirmation "HT")

  let cancelHotel (confirmationId: string) : Task =
    Console.WriteLine($"  [API] Cancelling hotel {confirmationId}")
    Task.CompletedTask

  // Deterministic, replay-safe failure trigger: a car-rental city of "FAILVILLE" always fails
  // terminally so the saga's compensation path can be asserted end-to-end. (Throwing a
  // TerminalException makes Restate stop retrying and surface the failure to the orchestrator.)
  let bookCarRental (request: CarRentalRequest) : Task<string> =
    Console.WriteLine($"  [API] Booking car in {request.City} from {request.PickUp} to {request.DropOff}")
    if String.Equals(request.City, "FAILVILLE", StringComparison.OrdinalIgnoreCase) then
      raise (TerminalException("Car rental service rejected the booking for this city.", 409us))
    Task.FromResult(confirmation "CR")

  let cancelCarRental (confirmationId: string) : Task =
    Console.WriteLine($"  [API] Cancelling car rental {confirmationId}")
    Task.CompletedTask

// ---------------------------------------------------------------------------------------------------
// Workflow domain — user signup with email verification.
// ---------------------------------------------------------------------------------------------------

type SignupRequest = { Email: string; Name: string }
type SignupResult = { AccountId: string; Verified: bool }

/// <summary>Stub for an account management API.</summary>
[<RequireQualifiedAccess>]
module AccountService =

  let create (email: string) (name: string) : Task<string> =
    Console.WriteLine($"  [API] Creating account for {name} <{email}>")
    Task.FromResult($"acct-{Guid.NewGuid():N}")

  let activate (accountId: string) : Task =
    Console.WriteLine($"  [API] Activating account {accountId}")
    Task.CompletedTask

/// <summary>
///   Stub for an email delivery service. The verification email embeds the awakeable id so an external
///   "click" can resolve the durable promise the workflow is suspended on.
/// </summary>
[<RequireQualifiedAccess>]
module EmailService =

  let sendVerification (email: string) (callbackId: string) : Task =
    Console.WriteLine($"  [API] Sending verification email to {email} (awakeable {callbackId})")
    Task.CompletedTask
