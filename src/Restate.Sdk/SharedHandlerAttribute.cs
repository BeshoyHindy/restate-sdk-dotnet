namespace Restate.Sdk;

/// <summary>
///     Marks a method as a shared handler on a virtual object or workflow.
///     Shared handlers can run concurrently and have read-only access to state.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SharedHandlerAttribute : HandlerAttributeBase;
