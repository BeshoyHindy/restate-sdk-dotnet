namespace Restate.Sdk.Internal;

/// <summary>
///     Control-flow sentinel mirroring sdk-shared-core's Suspended error (code 599):
///     unwinds a handler parked on uncompleted notifications after the runtime closed the
///     input stream. Never reported as an invocation failure.
/// </summary>
internal sealed class SuspendedException : Exception
{
    public SuspendedException() : base("Invocation suspended") { }
}
