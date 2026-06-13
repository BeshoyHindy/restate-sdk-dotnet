using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk;

/// <summary>
///     A target for <see cref="IContext.Attach{T}(AttachTarget)" /> and
///     <see cref="IContext.GetOutput{T}(AttachTarget)" />. Mirrors the shared-core
///     <c>AttachInvocationTarget</c> sum type (lib.rs:205-218): attach to a running invocation by its
///     invocation id, by a workflow id (service name + key), or by an idempotency id
///     (service + handler + idempotency key, optional service key).
/// </summary>
public abstract class AttachTarget
{
    // Sealed hierarchy: construction is funnelled through the factory methods so callers can only
    // produce the three shapes the runtime understands, and the codec dispatches via the polymorphic
    // ApplyTo overloads below (no switch/default arm to leave uncovered).
    private AttachTarget()
    {
    }

    /// <summary>Attaches by the target's invocation id (the existing string overload's payload).</summary>
    public static AttachTarget InvocationId(string invocationId) =>
        new ByInvocationId(invocationId ?? throw new ArgumentNullException(nameof(invocationId)));

    /// <summary>Attaches by a workflow id: the workflow service <paramref name="name" /> and its <paramref name="key" />.</summary>
    public static AttachTarget WorkflowId(string name, string key) =>
        new ByWorkflowId(
            name ?? throw new ArgumentNullException(nameof(name)),
            key ?? throw new ArgumentNullException(nameof(key)));

    /// <summary>
    ///     Attaches by an idempotency id: the target <paramref name="serviceName" />,
    ///     <paramref name="handlerName" />, and <paramref name="idempotencyKey" />, with an optional
    ///     <paramref name="serviceKey" /> for keyed services (virtual objects / workflows).
    /// </summary>
    public static AttachTarget IdempotencyId(
        string serviceName, string handlerName, string idempotencyKey, string? serviceKey = null) =>
        new ByIdempotencyId(
            serviceName ?? throw new ArgumentNullException(nameof(serviceName)),
            handlerName ?? throw new ArgumentNullException(nameof(handlerName)),
            idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey)),
            serviceKey);

    // Set the target oneof on each command message kind. Two methods (not one generic) because the
    // generated AttachInvocation / GetInvocationOutput messages are distinct types with no shared
    // base for the oneof setters.
    internal abstract void ApplyTo(Gen.AttachInvocationCommandMessage message);

    internal abstract void ApplyTo(Gen.GetInvocationOutputCommandMessage message);

    private static Gen.WorkflowTarget BuildWorkflowTarget(ByWorkflowId target) =>
        new() { WorkflowName = target.Name, WorkflowKey = target.Key };

    private static Gen.IdempotentRequestTarget BuildIdempotentRequestTarget(ByIdempotencyId target)
    {
        var msg = new Gen.IdempotentRequestTarget
        {
            ServiceName = target.ServiceName,
            HandlerName = target.HandlerName,
            IdempotencyKey = target.IdempotencyKey
        };
        // ServiceKey is a proto optional (HasServiceKey gate); only set it when present so a stateless
        // service target leaves the field unset rather than emitting an empty string.
        if (target.ServiceKey is not null) msg.ServiceKey = target.ServiceKey;
        return msg;
    }

    private sealed class ByInvocationId(string invocationId) : AttachTarget
    {
        private string Id { get; } = invocationId;

        internal override void ApplyTo(Gen.AttachInvocationCommandMessage message) => message.InvocationId = Id;

        internal override void ApplyTo(Gen.GetInvocationOutputCommandMessage message) => message.InvocationId = Id;
    }

    private sealed class ByWorkflowId(string name, string key) : AttachTarget
    {
        public string Name { get; } = name;
        public string Key { get; } = key;

        internal override void ApplyTo(Gen.AttachInvocationCommandMessage message) =>
            message.WorkflowTarget = BuildWorkflowTarget(this);

        internal override void ApplyTo(Gen.GetInvocationOutputCommandMessage message) =>
            message.WorkflowTarget = BuildWorkflowTarget(this);
    }

    private sealed class ByIdempotencyId(
        string serviceName, string handlerName, string idempotencyKey, string? serviceKey) : AttachTarget
    {
        public string ServiceName { get; } = serviceName;
        public string HandlerName { get; } = handlerName;
        public string IdempotencyKey { get; } = idempotencyKey;
        public string? ServiceKey { get; } = serviceKey;

        internal override void ApplyTo(Gen.AttachInvocationCommandMessage message) =>
            message.IdempotentRequestTarget = BuildIdempotentRequestTarget(this);

        internal override void ApplyTo(Gen.GetInvocationOutputCommandMessage message) =>
            message.IdempotentRequestTarget = BuildIdempotentRequestTarget(this);
    }
}
