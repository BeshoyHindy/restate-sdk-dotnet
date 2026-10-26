using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests;

/// <summary>
///     When buffered commands reach the runtime. Other invocations observe commands the handler
///     never awaits — a shared handler reads the state it set, an awakeable's holder waits for
///     its completion — so they must go out while the handler parks on a future, and flushes
///     that overlap on a slow transport must not lose any of them.
/// </summary>
public class CommandFlushTests : IDisposable
{
    private readonly Pipe _inbound = new();
    private Pipe _outbound = new();
    private ProtocolReader? _outboundReader;
    private InvocationStateMachine? _sm;

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outboundReader?.Dispose();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
        _sm?.Dispose();
    }

    /// <param name="outboundOptions">
    ///     Options for the outbound pipe. A pipe that pauses its writer keeps every flush in
    ///     flight until the test reads, as Kestrel does while HTTP/2 flow control holds a write.
    /// </param>
    private DefaultObjectContext StartLive(PipeOptions? outboundOptions = null)
    {
        if (outboundOptions is not null)
            _outbound = new Pipe(outboundOptions);

        _sm = new InvocationStateMachine(
            new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer),
            negotiatedVersion: ServiceProtocolVersion.V7);
        var ctx = new DefaultObjectContext(_sm, NullLogger.Instance, CancellationToken.None);
        _sm.Initialize("inv-1", [0xAB], "key", 0, 0);
        _ = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        return ctx;
    }

    /// <summary>Reads frames off the wire until <paramref name="count" /> of the given type arrived.</summary>
    private async Task<List<RawMessage>> ReadAsync(MessageType type, int count)
    {
        _outboundReader ??= new ProtocolReader(_outbound.Reader);
        var messages = new List<RawMessage>();
        while (messages.Count < count)
        {
            var message = await _outboundReader.ReadMessageAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
                          ?? throw new InvalidOperationException("stream ended");
            if (message.Header.Type == type)
                messages.Add(message);
            else
                message.Dispose();
        }

        return messages;
    }

    private async Task<List<string>> ReadSetStateKeysAsync(int count)
    {
        var keys = new List<string>();
        foreach (var message in await ReadAsync(MessageType.SetStateCommand, count))
            using (message)
                keys.Add(Gen.SetStateCommandMessage.Parser.ParseFrom(message.Payload).Key.ToStringUtf8());

        return keys;
    }

    [Fact]
    public async Task AwakeablesCreatedWhileAFlushIsInFlight_SendEveryBufferedCommand()
    {
        var ctx = StartLive(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));

        // The command interpreter's shape: create an awakeable, publish its id to state, repeat...
        var futures = new List<IDurableFuture>();
        foreach (var name in new[] { "awk0", "awk1", "awk2" })
        {
            var awakeable = ctx.Awakeable<string>();
            ctx.Set(new StateKey<string>("awk-" + name), awakeable.Id);
            futures.Add(awakeable.Future);
        }

        // ...then parks on them, as awaitAnySuccessful does.
        var waiting = Task.Run(async () =>
        {
            await foreach (var _ in ctx.WaitAll(futures.ToArray()))
                return;
        });

        Assert.Equal(["awk-awk0", "awk-awk1", "awk-awk2"], await ReadSetStateKeysAsync(3));
        Assert.False(waiting.IsFaulted, waiting.Exception?.ToString());
    }

    [Fact]
    public async Task StateWrittenAfterTheLastFlush_ReachesTheRuntimeWhileTheHandlerParks()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();
        ctx.Set(new StateKey<string>("awk-id"), awakeable.Id);
        var parked = awakeable.Future.GetResult().AsTask();

        // Nothing resolves the awakeable until another invocation has read its id from state.
        Assert.Equal(["awk-id"], await ReadSetStateKeysAsync(1));
        Assert.False(parked.IsCompleted);
    }

    [Fact]
    public async Task UnawaitedCompletionsAndClears_ReachTheRuntimeWithoutAFurtherOperation()
    {
        var ctx = StartLive();

        ctx.ResolveAwakeable("awk-1", "done");
        (await ReadAsync(MessageType.CompleteAwakeableCommand, 1))[0].Dispose();

        ctx.Clear("some-key");
        (await ReadAsync(MessageType.ClearStateCommand, 1))[0].Dispose();

        ctx.ClearAll();
        (await ReadAsync(MessageType.ClearAllStateCommand, 1))[0].Dispose();
    }
}
