using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Restate.Sdk.Internal;

namespace Restate.Sdk.Tests.Observability;

/// <summary>
///     Verifies the static <c>Restate.Sdk</c> Meter records the three invocation instruments
///     once per invocation with the expected tags. Uses <see cref="MeterListener" /> (built-in,
///     no extra package) and filters measurements by service tag because the Meter is
///     process-global and other test classes drive invocations in parallel.
/// </summary>
public class MetricsTests
{
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

    private static MeterListener CreateListener(ConcurrentQueue<Measurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RestateMetrics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        return dict;
    }

    private static List<Measurement> ForService(ConcurrentQueue<Measurement> measurements, string service)
    {
        return measurements
            .Where(m => m.Tags.TryGetValue("restate.service", out var s) && Equals(s, service))
            .ToList();
    }

    [Fact]
    public async Task SuccessfulInvocation_RecordsAllThreeInstrumentsOnce()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using (CreateListener(measurements))
        {
            await ObservabilityTestDriver.DriveAsync<ObsMetricsGreeterService>(
                "Greet", "World", new InvocationHandler(), "obs-metrics-1");
        }

        var recorded = ForService(measurements, "ObsMetricsGreeter");

        var invocations = Assert.Single(recorded, m => m.Instrument == "restate.sdk.invocations");
        Assert.Equal(1, invocations.Value);
        Assert.Equal("Greet", invocations.Tags["restate.handler"]);
        Assert.Equal("success", invocations.Tags["outcome"]);

        var duration = Assert.Single(recorded, m => m.Instrument == "restate.sdk.invocation.duration");
        Assert.True(duration.Value >= 0, "Duration must be non-negative");
        Assert.Equal("success", duration.Tags["outcome"]);

        var replayed = Assert.Single(recorded, m => m.Instrument == "restate.sdk.journal.replayed_commands");
        Assert.Equal(1, replayed.Value); // known_entries=1: only the input command
        Assert.False(replayed.Tags.ContainsKey("outcome"), "Replay histogram must not carry the outcome tag");
    }

    [Fact]
    public async Task TerminalException_RecordsTerminalErrorOutcome()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using (CreateListener(measurements))
        {
            await ObservabilityTestDriver.DriveAsync<ObsMetricsFailingService>(
                "Fail", "input", new InvocationHandler(), "obs-metrics-2");
        }

        var recorded = ForService(measurements, "ObsMetricsFailing");

        var invocations = Assert.Single(recorded, m => m.Instrument == "restate.sdk.invocations");
        Assert.Equal("terminal_error", invocations.Tags["outcome"]);
        Assert.Equal("Fail", invocations.Tags["restate.handler"]);

        var duration = Assert.Single(recorded, m => m.Instrument == "restate.sdk.invocation.duration");
        Assert.Equal("terminal_error", duration.Tags["outcome"]);
    }
}
