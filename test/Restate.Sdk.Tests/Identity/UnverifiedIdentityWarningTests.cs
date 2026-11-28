using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Restate.Sdk.Hosting;
using Restate.Sdk.Tests.Endpoint;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     An endpoint without identity keys accepts requests from any client that can reach it, so
///     both hosts log one warning at startup saying so. Endpoints with keys stay quiet.
/// </summary>
public class UnverifiedIdentityWarningTests
{
    private const int IdentityVerificationDisabledEventId = 15;

    [Fact]
    public async Task MapRestate_NoKeysConfigured_LogsWarning()
    {
        var provider = new CapturingLoggerProvider();
        await using var app = BuildApp(provider, identityKey: null);

        var warning = Assert.Single(provider.Entries, e => e.EventId == IdentityVerificationDisabledEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("WithIdentityKeys", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapRestate_KeysConfigured_DoesNotLogWarning()
    {
        var provider = new CapturingLoggerProvider();
        await using var app = BuildApp(provider, IdentityTestHelpers.CreateKeyPair().SerializedKey);

        Assert.DoesNotContain(provider.Entries, e => e.EventId == IdentityVerificationDisabledEventId);
    }

    [Fact]
    public void LambdaHandler_NoKeysConfigured_LogsWarning()
    {
        var provider = new CapturingLoggerProvider();
        LoggingHandler.Create(provider, () => new OpenHandler());

        var warning = Assert.Single(provider.Entries, e => e.EventId == IdentityVerificationDisabledEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    [Fact]
    public void LambdaHandler_KeysConfigured_DoesNotLogWarning()
    {
        var provider = new CapturingLoggerProvider();
        LoggingHandler.Create(provider, () => new SecuredHandler());

        Assert.DoesNotContain(provider.Entries, e => e.EventId == IdentityVerificationDisabledEventId);
    }

    private static WebApplication BuildApp(CapturingLoggerProvider provider, string? identityKey)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(provider);
        builder.Services.AddRestate(opts =>
        {
            opts.AddService<GreeterService>();
            if (identityKey is not null)
                opts.WithIdentityKeys(identityKey);
        });

        var app = builder.Build();
        app.MapRestate();
        return app;
    }

    private sealed class OpenHandler : LoggingHandler
    {
        protected override void RegisterServices() => Bind<GreeterService>();
    }

    private sealed class SecuredHandler : LoggingHandler
    {
        protected override void RegisterServices()
        {
            Bind<GreeterService>();
            WithIdentityKeys(IdentityTestHelpers.CreateKeyPair().SerializedKey);
        }
    }

    /// <summary>
    ///     <see cref="RestateLambdaHandler.Register" /> runs from the base constructor, before a
    ///     derived constructor can store anything, so the provider travels through a thread-static.
    /// </summary>
    private abstract class LoggingHandler : RestateLambdaHandler
    {
        [ThreadStatic] private static ILoggerProvider? s_pendingProvider;

        public static void Create(ILoggerProvider provider, Func<LoggingHandler> construct)
        {
            s_pendingProvider = provider;
            try
            {
                _ = construct();
            }
            finally
            {
                s_pendingProvider = null;
            }
        }

        public sealed override void Register()
        {
            UseLoggerFactory(new LoggerFactory([s_pendingProvider!]));
            RegisterServices();
        }

        protected abstract void RegisterServices();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _sync = new();
        private readonly List<(int EventId, LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(int EventId, LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_sync) return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(int eventId, LogLevel level, string message)
        {
            lock (_sync) _entries.Add((eventId, level, message));
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Add(eventId.Id, logLevel, formatter(state, exception));
            }
        }
    }
}
