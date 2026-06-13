using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Restate.Sdk.Internal.Context;

internal sealed class RunContext : IRunContext
{
    internal RunContext(CancellationToken ct, ILogger? logger = null,
        EntryRetryInfo retryInfo = default)
    {
        CancellationToken = ct;
        Logger = logger ?? NullLogger.Instance;
        RetryInfo = retryInfo;
    }

    public CancellationToken CancellationToken { get; }
    public ILogger Logger { get; }
    public EntryRetryInfo RetryInfo { get; }
}