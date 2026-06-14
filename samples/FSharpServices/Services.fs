module Restate.Sdk.FSharp.Samples.Services

open System
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Restate.Sdk
open Restate.Sdk.Endpoint
open Restate.Sdk.FSharp

// Durable state keys. Shared by the handlers below; one StateKey<'T> per logical column.
let private count = StateKey<int>("count")
let private wfStatus = StateKey<string>("status")
let private wfAccountId = StateKey<string>("accountId")
let private wfPendingId = StateKey<string>("pendingVerificationId")

// ---------------------------------------------------------------------------------------------------
// Saga (stateless Service): book a trip, compensating completed steps in reverse order on failure.
// ---------------------------------------------------------------------------------------------------

/// <summary>
///   The Saga pattern (compensating transactions). Books a flight, hotel, and car rental, each as a
///   durable <c>ctx.Run</c> step. If a later step fails terminally, the already-completed bookings are
///   undone in reverse order. Restate guarantees the compensations run even across crashes/replays.
/// </summary>
type TripBookingService() =

  member _.Book (ctx: Context) (request: TripBookingRequest) : Task<TripBookingResult> =
    task {
      // A malformed request is a deterministic client error: fail terminally rather than letting a
      // null dereference be retried forever.
      if isNull (box request)
         || isNull (box request.Flight)
         || isNull (box request.Hotel)
         || isNull (box request.CarRental) then
        raise (TerminalException("TripBookingRequest must include tripId, flight, hotel, and carRental.", 400us))

      // Compensations are stacked LIFO — the most recent booking is cancelled first.
      let compensations = ResizeArray<unit -> Task>()

      try
        ctx.Console.Log($"Booking flight for trip {request.TripId}...")
        let! flight = Durable.run ctx "book-flight" (fun () -> BookingApi.bookFlight request.Flight)
        compensations.Add(fun () -> Durable.runUnit ctx "cancel-flight" (fun () -> BookingApi.cancelFlight flight))

        ctx.Console.Log($"Booking hotel for trip {request.TripId}...")
        let! hotel = Durable.run ctx "book-hotel" (fun () -> BookingApi.bookHotel request.Hotel)
        compensations.Add(fun () -> Durable.runUnit ctx "cancel-hotel" (fun () -> BookingApi.cancelHotel hotel))

        ctx.Console.Log($"Booking car rental for trip {request.TripId}...")
        let! car =
          Durable.runRetry ctx "book-car-rental" (RetryPolicy.FixedAttempts(3))
            (fun () -> BookingApi.bookCarRental request.CarRental)

        ctx.Console.Log($"Trip {request.TripId} booked successfully!")
        return
          { TripId = request.TripId
            FlightConfirmation = flight
            HotelConfirmation = hotel
            CarRentalConfirmation = car }
      with
      | :? TerminalException as ex ->
        ctx.Console.Log($"Trip {request.TripId} failed. Running {compensations.Count} compensation(s)...")
        for i in (compensations.Count - 1) .. -1 .. 0 do
          do! compensations.[i] ()
        ctx.Console.Log($"Trip {request.TripId} fully compensated.")
        // Re-surface the original terminal failure (preserving its stack) so the caller sees it.
        ExceptionDispatchInfo.Throw(ex)
        return Unchecked.defaultof<TripBookingResult>
    }

// ---------------------------------------------------------------------------------------------------
// Virtual Object: a durable per-key counter with exclusive writes and shared reads.
// ---------------------------------------------------------------------------------------------------

/// <summary>
///   A Virtual Object that keeps a durable counter per key. Exclusive handlers (Add/Reset) run one at a
///   time per key; shared handlers (Get/GetKeys) run concurrently without blocking writers.
/// </summary>
type CounterObject() =

  member _.Add (ctx: ObjectContext) (delta: int) : Task<int> =
    task {
      let! current = Durable.get ctx count
      let next = current + delta
      Durable.set ctx count next
      return next
    }

  member _.AddThenFail (ctx: ObjectContext) : Task<unit> =
    // A non-retryable error: TerminalException stops Restate from retrying the invocation.
    ignore ctx
    raise (TerminalException("This operation intentionally fails and will not be retried", 400us))

  member _.Reset (ctx: ObjectContext) : Task<unit> = task { Durable.clearAll ctx }

  member _.Get (ctx: SharedObjectContext) : Task<int> = task { return! Durable.get ctx count }

  member _.GetKeys (ctx: SharedObjectContext) : Task<string[]> = task { return! Durable.stateKeys ctx }

// ---------------------------------------------------------------------------------------------------
// Workflow: user signup with email verification gated on a durable awakeable.
// ---------------------------------------------------------------------------------------------------

/// <summary>
///   A Workflow whose <c>Run</c> handler executes exactly once per workflow id. It creates an account,
///   sends a verification email carrying an awakeable id, then suspends (zero compute) until an external
///   caller resolves that awakeable — at which point it activates the account. Shared handlers expose
///   the live status and the pending awakeable id so an operator (or an E2E test) can complete it.
/// </summary>
type SignupWorkflow() =

  member _.Run (ctx: WorkflowContext) (request: SignupRequest) : Task<SignupResult> =
    task {
      Durable.set ctx wfStatus "creating-account"
      let! accountId =
        Durable.run ctx "create-account" (fun () -> AccountService.create request.Email request.Name)
      Durable.set ctx wfAccountId accountId

      Durable.set ctx wfStatus "awaiting-verification"
      let awk = Durable.awakeable<WorkflowContext, string> ctx
      Durable.set ctx wfPendingId awk.Id

      do!
        Durable.runUnit ctx "send-verification-email"
          (fun () -> EmailService.sendVerification request.Email awk.Id)

      // Suspends here until the awakeable is resolved by an external event.
      let! _verified = awk.Value.AsTask()

      Durable.set ctx wfStatus "activating"
      do! Durable.runUnit ctx "activate-account" (fun () -> AccountService.activate accountId)

      Durable.set ctx wfStatus "completed"
      return { AccountId = accountId; Verified = true }
    }

  member _.GetStatus (ctx: SharedWorkflowContext) : Task<string> =
    task {
      let! status = Durable.get ctx wfStatus
      return (if isNull status then "unknown" else status)
    }

  member _.GetPendingVerificationId (ctx: SharedWorkflowContext) : Task<string> =
    task {
      let! pending = Durable.get ctx wfPendingId
      return (if isNull pending then "" else pending)
    }

// ---------------------------------------------------------------------------------------------------
// Service definitions — the hand-built equivalent of what the C# source generator emits.
// ---------------------------------------------------------------------------------------------------

let tripBookingDefinition : ServiceDefinition =
  Durable.service<TripBookingService> "TripBookingService" [
    Durable.handler "Book" (fun (s: TripBookingService) (ctx: Context) (req: TripBookingRequest) -> s.Book ctx req)
  ]

let counterDefinition : ServiceDefinition =
  Durable.virtualObject<CounterObject> "CounterObject" [
    Durable.handler "Add" (fun (s: CounterObject) (ctx: ObjectContext) (delta: int) -> s.Add ctx delta)
    Durable.action "AddThenFail" (fun (s: CounterObject) (ctx: ObjectContext) -> s.AddThenFail ctx)
    Durable.action "Reset" (fun (s: CounterObject) (ctx: ObjectContext) -> s.Reset ctx)
    Durable.sharedFunc "Get" (fun (s: CounterObject) (ctx: SharedObjectContext) -> s.Get ctx)
    Durable.sharedFunc "GetKeys" (fun (s: CounterObject) (ctx: SharedObjectContext) -> s.GetKeys ctx)
  ]

let signupDefinition : ServiceDefinition =
  Durable.workflow<SignupWorkflow> "SignupWorkflow" [
    Durable.handler "Run" (fun (s: SignupWorkflow) (ctx: WorkflowContext) (req: SignupRequest) -> s.Run ctx req)
    Durable.sharedFunc "GetStatus" (fun (s: SignupWorkflow) (ctx: SharedWorkflowContext) -> s.GetStatus ctx)
    Durable.sharedFunc "GetPendingVerificationId"
      (fun (s: SignupWorkflow) (ctx: SharedWorkflowContext) -> s.GetPendingVerificationId ctx)
  ]

/// Registers all three definitions under their marker types (mirrors the generated module initializer).
let registerAll () : unit =
  Durable.register<TripBookingService> tripBookingDefinition
  Durable.register<CounterObject> counterDefinition
  Durable.register<SignupWorkflow> signupDefinition
