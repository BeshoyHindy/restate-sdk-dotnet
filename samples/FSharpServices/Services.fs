namespace Restate.Sdk.FSharp.Samples

open System
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Restate.Sdk
open Restate.Sdk.FSharp

// Durable state keys. Shared by the handlers below; one StateKey<'T> per logical column.
[<AutoOpen>]
module private State =
  let count = StateKey<int>("count")
  let wfStatus = StateKey<string>("status")
  let wfAccountId = StateKey<string>("accountId")
  let wfPendingId = StateKey<string>("pendingVerificationId")

// ---------------------------------------------------------------------------------------------------
// Saga (stateless Service): book a trip, compensating completed steps in reverse order on failure.
// Handler bodies call the C# Context extension methods (ctx.RunStep/...), so there is no F# binding
// boilerplate; the [<Service>]/[<Handler>] attributes are what the Myriad generator scans.
// ---------------------------------------------------------------------------------------------------

/// <summary>The Saga pattern (compensating transactions) — undo completed bookings in reverse on failure.</summary>
[<Service>]
type TripBookingService() =

  [<Handler>]
  member _.Book (ctx: Context) (request: TripBookingRequest) : Task<TripBookingResult> =
    task {
      if isNull (box request)
         || isNull (box request.Flight)
         || isNull (box request.Hotel)
         || isNull (box request.CarRental) then
        raise (TerminalException("TripBookingRequest must include tripId, flight, hotel, and carRental.", 400us))

      // Compensations are stacked LIFO — the most recent booking is cancelled first.
      let compensations = ResizeArray<unit -> Task>()

      try
        ctx.Console.Log($"Booking flight for trip {request.TripId}...")
        let! flight = ctx.RunStep("book-flight", fun () -> BookingApi.bookFlight request.Flight)
        compensations.Add(fun () -> ctx.RunStepUnit("cancel-flight", fun () -> BookingApi.cancelFlight flight))

        ctx.Console.Log($"Booking hotel for trip {request.TripId}...")
        let! hotel = ctx.RunStep("book-hotel", fun () -> BookingApi.bookHotel request.Hotel)
        compensations.Add(fun () -> ctx.RunStepUnit("cancel-hotel", fun () -> BookingApi.cancelHotel hotel))

        ctx.Console.Log($"Booking car rental for trip {request.TripId}...")
        let! car =
          ctx.RunStep("book-car-rental", RetryPolicy.FixedAttempts(3), fun () -> BookingApi.bookCarRental request.CarRental)

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

/// <summary>A Virtual Object keeping a durable counter per key (exclusive Add/Reset, shared Get/GetKeys).</summary>
[<VirtualObject>]
type CounterObject() =

  [<Handler>]
  member _.Add (ctx: ObjectContext) (delta: int) : Task<int> =
    task {
      let! current = ctx.GetAsync(count)
      let next = current + delta
      ctx.SetState(count, next)
      return next
    }

  [<Handler>]
  member _.AddThenFail (ctx: ObjectContext) : Task<unit> =
    ignore ctx
    raise (TerminalException("This operation intentionally fails and will not be retried", 400us))

  [<Handler>]
  member _.Reset (ctx: ObjectContext) : Task<unit> = task { ctx.ClearAllState() }

  [<SharedHandler>]
  member _.Get (ctx: SharedObjectContext) : Task<int> = task { return! ctx.GetAsync(count) }

  [<SharedHandler>]
  member _.GetKeys (ctx: SharedObjectContext) : Task<string[]> = task { return! ctx.StateKeysAsync() }

// ---------------------------------------------------------------------------------------------------
// Workflow: user signup with email verification gated on a durable awakeable.
// ---------------------------------------------------------------------------------------------------

/// <summary>A Workflow that suspends on an awakeable until an external caller resolves the verification.</summary>
[<Workflow>]
type SignupWorkflow() =

  [<Handler>]
  member _.Run (ctx: WorkflowContext) (request: SignupRequest) : Task<SignupResult> =
    task {
      ctx.SetState(wfStatus, "creating-account")
      let! accountId = ctx.RunStep("create-account", fun () -> AccountService.create request.Email request.Name)
      ctx.SetState(wfAccountId, accountId)

      ctx.SetState(wfStatus, "awaiting-verification")
      let awk = ctx.NewAwakeable<string>()
      ctx.SetState(wfPendingId, awk.Id)

      do! ctx.RunStepUnit("send-verification-email", fun () -> EmailService.sendVerification request.Email awk.Id)

      // Suspends here until the awakeable is resolved by an external event.
      let! _verified = awk.Value.AsTask()

      ctx.SetState(wfStatus, "activating")
      do! ctx.RunStepUnit("activate-account", fun () -> AccountService.activate accountId)

      ctx.SetState(wfStatus, "completed")
      return { AccountId = accountId; Verified = true }
    }

  [<SharedHandler>]
  member _.GetStatus (ctx: SharedWorkflowContext) : Task<string> =
    task {
      let! status = ctx.GetAsync(wfStatus)
      return (if isNull status then "unknown" else status)
    }

  [<SharedHandler>]
  member _.GetPendingVerificationId (ctx: SharedWorkflowContext) : Task<string> =
    task {
      let! pending = ctx.GetAsync(wfPendingId)
      return (if isNull pending then "" else pending)
    }
