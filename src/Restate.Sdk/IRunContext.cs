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

    /// <summary>
    ///     Retry accounting for this side effect's current attempt — the .NET surface of shared-core's
    ///     <c>EntryRetryInfo</c> passed to the run (vm/context.rs:461-479). On the first attempt
    ///     <see cref="EntryRetryInfo.RetryCount" /> is <c>0</c>; after the runtime re-drives the invocation the
    ///     first committed run resumes the cumulative count/duration from the StartMessage seeds. Lets a
    ///     backoff-aware closure observe how many times it has already been retried.
    /// </summary>
    EntryRetryInfo RetryInfo { get; }
}