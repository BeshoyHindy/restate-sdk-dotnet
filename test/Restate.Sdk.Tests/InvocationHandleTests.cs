using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests;

/// <summary>
///     The signalling half of <see cref="InvocationHandle" />: the <c>SendSignalCommand</c> it
///     writes for a signal addressed by name and by index, driven through a <see cref="Context" />
///     and through an <see cref="IContext" />-typed variable, as a helper shared between handler
///     kinds would.
/// </summary>
public class InvocationHandleTests : IDisposable
{
    /// <summary>The code a rejected signal carries; <c>DefaultContext</c> sends it for every rejection.</summary>
    private const ushort RejectionCode = 500;

    private const string Target = "inv-target";
    private readonly Context _ctx;
    private readonly InvocationHandle _handle = new(Target);
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly InvocationStateMachine _sm;

    public InvocationHandleTests()
    {
        _sm = new InvocationStateMachine(
            new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer),
            negotiatedVersion: ServiceProtocolVersion.V7);
        _sm.Initialize("inv-sender", [0xAB], "", 0, 0);
        _ctx = new DefaultContext(_sm, NullLogger.Instance, CancellationToken.None);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
        _sm.Dispose();
    }

    [Fact]
    public async Task ResolveSignal_ByName_ThroughAContext()
    {
        await _handle.ResolveSignal(_ctx, "approval", "granted");

        var command = await SingleSendSignalAsync();
        Assert.Equal(Target, command.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Name, command.SignalIdCase);
        Assert.Equal("approval", command.Name);
        Assert.Equal("\"granted\"", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task ResolveSignal_ByName_ThroughAnIContext()
    {
        await ResolveByNameAsync(_ctx, _handle);

        var command = await SingleSendSignalAsync();
        Assert.Equal(Target, command.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Name, command.SignalIdCase);
        Assert.Equal("approval", command.Name);
        Assert.Equal("\"granted\"", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task ResolveSignal_ByIndex_ThroughAContext()
    {
        await _handle.ResolveSignal(_ctx, InvocationStateMachine.FirstUserSignalIndex, 42);

        var command = await SingleSendSignalAsync();
        Assert.Equal(Target, command.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx, command.SignalIdCase);
        Assert.Equal((uint)InvocationStateMachine.FirstUserSignalIndex, command.Idx);
        Assert.Equal("42", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task ResolveSignal_ByIndex_ThroughAnIContext()
    {
        await ResolveByIndexAsync(_ctx, _handle, InvocationStateMachine.FirstUserSignalIndex);

        var command = await SingleSendSignalAsync();
        Assert.Equal(Target, command.TargetInvocationId);
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx, command.SignalIdCase);
        Assert.Equal((uint)InvocationStateMachine.FirstUserSignalIndex, command.Idx);
        Assert.Equal("42", command.Value.Content.ToStringUtf8());
    }

    [Fact]
    public async Task RejectSignal_ByName_ThroughAContext()
    {
        await _handle.RejectSignal(_ctx, "approval", "not approved");

        var command = await SingleSendSignalAsync();
        Assert.Equal("approval", command.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Failure, command.ResultCase);
        Assert.Equal(RejectionCode, command.Failure.Code);
        Assert.Equal("not approved", command.Failure.Message);
    }

    [Fact]
    public async Task RejectSignal_ByName_ThroughAnIContext()
    {
        await RejectByNameAsync(_ctx, _handle);

        var command = await SingleSendSignalAsync();
        Assert.Equal("approval", command.Name);
        Assert.Equal(RejectionCode, command.Failure.Code);
        Assert.Equal("not approved", command.Failure.Message);
    }

    [Fact]
    public async Task RejectSignal_ByIndex_ThroughAContext()
    {
        await _handle.RejectSignal(_ctx, InvocationStateMachine.FirstUserSignalIndex, "not approved");

        var command = await SingleSendSignalAsync();
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx, command.SignalIdCase);
        Assert.Equal((uint)InvocationStateMachine.FirstUserSignalIndex, command.Idx);
        Assert.Equal("not approved", command.Failure.Message);
    }

    [Fact]
    public async Task RejectSignal_ByIndex_ThroughAnIContext()
    {
        await RejectByIndexAsync(_ctx, _handle, InvocationStateMachine.FirstUserSignalIndex);

        var command = await SingleSendSignalAsync();
        Assert.Equal(Gen.SendSignalCommandMessage.SignalIdOneofCase.Idx, command.SignalIdCase);
        Assert.Equal((uint)InvocationStateMachine.FirstUserSignalIndex, command.Idx);
        Assert.Equal("not approved", command.Failure.Message);
    }

    // The four helpers below take IContext, so they compile only while the handle's overloads
    // accept the interface — the point of the overloads.
    private static ValueTask ResolveByNameAsync(IContext context, InvocationHandle handle)
    {
        return handle.ResolveSignal(context, "approval", "granted");
    }

    private static ValueTask ResolveByIndexAsync(IContext context, InvocationHandle handle, int signalIndex)
    {
        return handle.ResolveSignal(context, signalIndex, 42);
    }

    private static ValueTask RejectByNameAsync(IContext context, InvocationHandle handle)
    {
        return handle.RejectSignal(context, "approval", "not approved");
    }

    private static ValueTask RejectByIndexAsync(IContext context, InvocationHandle handle, int signalIndex)
    {
        return handle.RejectSignal(context, signalIndex, "not approved");
    }

    private async Task<Gen.SendSignalCommandMessage> SingleSendSignalAsync()
    {
        _outbound.Writer.Complete();
        using var reader = new ProtocolReader(_outbound.Reader);
        using var message = await reader.ReadMessageAsync()
                            ?? throw new InvalidOperationException("No outbound message was written.");

        Assert.Equal(MessageType.SendSignalCommand, message.Header.Type);
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(message.Payload);
        Assert.Null(await reader.ReadMessageAsync());
        return command;
    }
}
