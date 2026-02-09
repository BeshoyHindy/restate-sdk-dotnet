using Microsoft.Extensions.Logging;

namespace Restate.Sdk;

/// <summary>
///     Restricted context available inside
///     <see cref="Context.Run{T}(string, Func{IRunContext, Task{T}})" /> blocks.
///     Deliberately excludes Restate operations (no Run, Sleep, calls, state, etc.)
///     to prevent nested side effects which would violate the durable execution model.
/// </summary>
public interface IRunContext
{
    /// <summary>Cancellation token for the current Run operation.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Logger scoped to the current invocation.</summary>
    ILogger Logger { get; }
}