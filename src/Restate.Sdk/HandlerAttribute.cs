namespace Restate.Sdk;

/// <summary>
///     Marks a method as a Restate handler. On virtual objects this is an exclusive handler
///     (one-at-a-time per key). On services this is a regular concurrent handler.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HandlerAttribute : HandlerAttributeBase;
