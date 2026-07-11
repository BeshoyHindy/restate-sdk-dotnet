using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal;

namespace Restate.Sdk.Tests.Observability;

/// <summary>
///     Verifies the replay-aware logger backing <c>ctx.Logger</c>: output suppressed during
///     replay, enabled after, scope delegation, and the invocation-id scope spanning the
///     handler execution when driven through <see cref="InvocationHandler" />.
/// </summary>
public class ReplayAwareLoggerTests
{
    [Fact]
    public void IsEnabled_ReturnsFalse_WhileReplaying()
    {
        var inner = new CapturingLogger();
        var replaying = true;
        var logger = new ReplayAwareLogger(inner, () => replaying);

        Assert.False(logger.IsEnabled(LogLevel.Critical));

        LogInfo(logger, "suppressed");
        Assert.Empty(inner.Entries);
    }

    [Fact]
    public void Log_FlowsToInner_AfterReplayCompletes()
    {
        var inner = new CapturingLogger();
        var replaying = true;
        var logger = new ReplayAwareLogger(inner, () => replaying);

        LogInfo(logger, "suppressed");
        replaying = false;

        Assert.True(logger.IsEnabled(LogLevel.Information));
        LogInfo(logger, "visible");

        var entry = Assert.Single(inner.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("visible", entry.Message);
    }

    [Fact]
    public void IsEnabled_RespectsInnerLoggerLevel()
    {
        var inner = new CapturingLogger { MinimumLevel = LogLevel.Warning };
        var logger = new ReplayAwareLogger(inner, () => false);

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void BeginScope_DelegatesToInner_EvenDuringReplay()
    {
        var inner = new CapturingLogger();
        var logger = new ReplayAwareLogger(inner, () => true);

        using (logger.BeginScope("my-scope"))
        {
            Assert.Equal("my-scope", Assert.Single(inner.ActiveScopes));
        }

        Assert.Empty(inner.ActiveScopes);
    }

    [Fact]
    public void InvocationLogScope_CarriesInvocationId()
    {
        var scope = new InvocationLogScope("inv-42");

        var pair = Assert.Single(scope);
        Assert.Equal("InvocationId", pair.Key);
        Assert.Equal("inv-42", pair.Value);
        Assert.Equal("InvocationId:inv-42", scope.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => scope[1]);
    }

    [Fact]
    public async Task CtxLogger_EndToEnd_LogsWithInvocationIdScope()
    {
        var factory = new CapturingLoggerFactory();
        var handler = new InvocationHandler(factory);

        await ObservabilityTestDriver.DriveAsync<ObsLoggingService>(
            "LogSomething", "input", handler, "obs-log-1");

        var entry = Assert.Single(factory.Logger.Entries, e => e.Message == "hello from handler");
        var scope = Assert.Single(entry.Scopes.OfType<InvocationLogScope>());
        var pair = Assert.Single(scope);
        Assert.Equal("InvocationId", pair.Key);
        Assert.Equal("obs-log-1", pair.Value);
    }

    /// <summary>Logs via the ILogger interface directly — CA1848 forbids extension methods here.</summary>
    private static void LogInfo(ILogger logger, string message)
    {
        logger.Log(LogLevel.Information, default, message, null, static (state, _) => state);
    }

    /// <summary>
    ///     Minimal capturing logger: records entries with the scopes active at log time.
    ///     Single-threaded by design — each invocation handler executes sequentially.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, List<object?> Scopes)> Entries { get; } = [];
        public List<object?> ActiveScopes { get; } = [];
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            ActiveScopes.Add(state);
            return new Scope(this, state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= MinimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), [.. ActiveScopes]));
        }

        private sealed class Scope(CapturingLogger owner, object state) : IDisposable
        {
            public void Dispose()
            {
                owner.ActiveScopes.Remove(state);
            }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            return Logger;
        }

        public void Dispose()
        {
        }
    }
}
