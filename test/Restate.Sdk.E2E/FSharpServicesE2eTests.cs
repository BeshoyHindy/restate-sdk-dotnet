using System.Net;
using Xunit;

namespace Restate.Sdk.E2E;

/// <summary>
///     End-to-end coverage for the F# sample (<c>samples/FSharpServices</c>) against a real
///     restate-server. Each scenario can only pass if the F# authoring stack — attributed handlers, the
///     C# <c>Restate.Sdk.FSharp</c> runtime helpers, and the Myriad-generated registration — is wired
///     correctly all the way through the server.
/// </summary>
[Collection(FSharpServicesCollection.Name)]
public sealed class FSharpServicesE2eTests(FSharpServicesFixture fixture)
{
    // Result shapes for the F# records (camelCase on the wire via the SDK's Web serializer policy).
    private sealed record TripResult(
        string TripId, string FlightConfirmation, string HotelConfirmation, string CarRentalConfirmation);

    private sealed record SignupResult(string AccountId, bool Verified);

    private static object Trip(string tripId, string carRentalCity) => new
    {
        tripId,
        userId = "alice",
        flight = new { from = "SFO", to = "JFK", date = "2026-03-15" },
        hotel = new { city = "New York", checkIn = "2026-03-15", checkOut = "2026-03-18" },
        carRental = new { city = carRentalCity, pickUp = "2026-03-15", dropOff = "2026-03-18" },
    };

    [DockerFact]
    public async Task VirtualObject_durable_state_accumulates_and_clears()
    {
        var key = $"counter-{Guid.NewGuid():N}";

        Assert.Equal(5, (await fixture.Ingress.InvokeAsync<int>("CounterObject", "Add", body: 5, key: key)).Value);
        Assert.Equal(8, (await fixture.Ingress.InvokeAsync<int>("CounterObject", "Add", body: 3, key: key)).Value);
        Assert.Equal(8, (await fixture.Ingress.InvokeAsync<int>("CounterObject", "Get", key: key)).Value);

        await fixture.Ingress.InvokeAsync<string>("CounterObject", "Reset", key: key);
        Assert.Equal(0, (await fixture.Ingress.InvokeAsync<int>("CounterObject", "Get", key: key)).Value);
    }

    [DockerFact]
    public async Task Saga_happy_path_returns_all_confirmations()
    {
        var result = (await fixture.Ingress.InvokeAsync<TripResult>(
            "TripBookingService", "Book", body: Trip("trip-ok", "New York"),
            idempotencyKey: $"ok-{Guid.NewGuid():N}")).Value;

        Assert.StartsWith("FL-", result.FlightConfirmation);
        Assert.StartsWith("HT-", result.HotelConfirmation);
        Assert.StartsWith("CR-", result.CarRentalConfirmation);
    }

    [DockerFact]
    public async Task Saga_compensation_path_surfaces_terminal_failure()
    {
        // The deterministic FAILVILLE trigger fails the car booking terminally; the saga rolls back the
        // flight + hotel and re-surfaces the TerminalException, which the ingress returns as 409.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Ingress.InvokeAsync<TripResult>(
                "TripBookingService", "Book", body: Trip("trip-fail", "FAILVILLE"),
                idempotencyKey: $"fail-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Contains("Car rental", ex.Message);
    }

    [DockerFact]
    public async Task Workflow_suspends_on_awakeable_and_resumes_on_external_resolve()
    {
        var key = $"signup-{Guid.NewGuid():N}";

        // Start the run fire-and-forget; it creates the account then suspends on the awakeable.
        await fixture.Ingress.SendAsync(
            "SignupWorkflow", "Run", body: new { email = "alice@example.com", name = "Alice" }, key: key);

        Assert.Equal("awaiting-verification", await WaitForStatus(key, "awaiting-verification"));

        var awakeableId = (await fixture.Ingress.InvokeAsync<string>(
            "SignupWorkflow", "GetPendingVerificationId", key: key)).Value;
        Assert.False(string.IsNullOrEmpty(awakeableId));

        // The external "click": resolving the awakeable wakes the suspended run.
        await fixture.Ingress.ResolveAwakeableAsync(awakeableId, "verified");

        Assert.Equal("completed", await WaitForStatus(key, "completed"));

        var output = await fixture.Ingress.AttachWorkflowAsync<SignupResult>("SignupWorkflow", key);
        Assert.True(output.Verified);
        Assert.StartsWith("acct-", output.AccountId);
    }

    private async Task<string> WaitForStatus(string key, string expected, int maxAttempts = 40)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var status = (await fixture.Ingress.InvokeAsync<string>("SignupWorkflow", "GetStatus", key: key)).Value;
            if (status == expected)
                return status;
            await Task.Delay(500);
        }

        throw new InvalidOperationException($"Workflow '{key}' never reached status '{expected}'.");
    }
}
