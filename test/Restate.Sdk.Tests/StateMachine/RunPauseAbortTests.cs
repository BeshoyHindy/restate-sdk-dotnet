using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     A service whose handler parks inside a <c>ctx.Run</c> closure until the test releases it,
///     so the test can take the transport away while the closure is still executing.
/// </summary>
[Service]
public class PausedRunService
{
    /// <summary>Signalled once the Run closure is executing, before it blocks on <see cref="Release" />.</summary>
    public static TaskCompletionSource ClosureEntered { get; private set; } = new();

    /// <summary>Completed by the test once the transport has been taken away.</summary>
    public static TaskCompletionSource Release { get; private set; } = new();

    public static void Reset()
    {
        ClosureEntered = new TaskCompletionSource();
        Release = new TaskCompletionSource();
    }

    [Handler]
    public async Task<string> Record(Context ctx, string name)
    {
        var recorded = await ctx.Run("record", async () =>
        {
            ClosureEntered.TrySetResult();
            await Release.Task;
            return $"recorded {name}";
        });

        return recorded;
    }
}

/// <summary>
///     restate-server 1.8 aborts the running attempt the moment an invocation is paused, so a
///     <c>ctx.Run</c> closure that is already executing loses its transport before it can propose
///     its result. The attempt has to end as a disconnect: the invocation keeps its journal and
///     resumes when it is unpaused, which an OutputCommand would forbid.
///     <para>
///         The closed-stream write surfaces as an <see cref="InvalidOperationException" /> from the
///         response pipe, which <c>InvocationHandler.HandleAsync</c> absorbs in its catch-all
///         branch: the attempt is logged as failed and the retryable ErrorMessage it tries to write
///         fails on the same closed stream. Nothing terminal reaches the wire and nothing escapes
///         to the host, so these tests pin the behaviour rather than drive a change.
///     </para>
/// </summary>
public class RunPauseAbortTests
{
    private static byte[] Frame(MessageType type, byte[] payload)
    {
        var frame = new byte[MessageHeader.Size + payload.Length];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(frame);
        payload.CopyTo(frame, MessageHeader.Size);
        return frame;
    }

    private static async Task WriteStartAndInputAsync(PipeWriter writer, string invocationId, string input)
    {
        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8(invocationId),
            DebugId = invocationId,
            KnownEntries = 1,
            Key = "",
            RandomSeed = 7
        };
        await writer.WriteAsync(Frame(MessageType.Start, start.ToByteArray()));

        var inputCommand = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(input)) }
        };
        await writer.WriteAsync(Frame(MessageType.InputCommand, inputCommand.ToByteArray()));
    }

    /// <summary>Reads every frame type the attempt managed to put on the wire.</summary>
    private static async Task<List<MessageType>> ReadFrameTypesAsync(PipeReader reader)
    {
        var result = await reader.ReadAsync();
        var data = result.Buffer.ToArray();
        reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);

        var types = new List<MessageType>();
        var offset = 0;
        while (offset + MessageHeader.Size <= data.Length)
        {
            var header = MessageHeader.Read(data.AsSpan(offset, MessageHeader.Size));
            if (offset + MessageHeader.Size + (int)header.Length > data.Length)
                break;

            types.Add(header.Type);
            offset += MessageHeader.Size + (int)header.Length;
        }

        return types;
    }

    [Fact]
    public async Task PausedAttempt_RunProposalOnClosedStream_EndsWithoutOutputCommand()
    {
        PausedRunService.Reset();

        var inbound = new Pipe();
        var outbound = new Pipe();

        await WriteStartAndInputAsync(inbound.Writer, "inv-paused-run", "World");

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(PausedRunService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Record");

        var handlerTask = new InvocationHandler().HandleAsync(
            inbound.Reader,
            outbound.Writer,
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new PausedRunService()),
            ServiceProtocolVersion.V7,
            CancellationToken.None);

        await PausedRunService.ClosureEntered.Task;

        // The pause aborts the attempt: the request body ends and the response body goes away
        // while the closure is still running. Both halves are gone before it proposes its result.
        await inbound.Writer.CompleteAsync();
        await outbound.Writer.CompleteAsync();

        PausedRunService.Release.TrySetResult();

        // The attempt ends; nothing escapes to the host (on Kestrel that would abort the
        // connection, on Lambda it would turn into a 500 with no protocol frame) and the task is
        // not left carrying a fault nobody observes.
        await handlerTask;
        Assert.True(handlerTask.IsCompletedSuccessfully);

        var frames = await ReadFrameTypesAsync(outbound.Reader);
        Assert.DoesNotContain(MessageType.OutputCommand, frames);
        Assert.DoesNotContain(MessageType.End, frames);

        await outbound.Reader.CompleteAsync();
        await inbound.Reader.CompleteAsync();
    }

    [Fact]
    public async Task RunProposalOnClosedStream_RentsNoBufferForTheLostProposal()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();

        using var reader = new ProtocolReader(inbound.Reader);
        using var writer = new ProtocolWriter(outbound.Writer);
        using var sm = new InvocationStateMachine(reader, writer, negotiatedVersion: ServiceProtocolVersion.V7);

        await WriteStartAndInputAsync(inbound.Writer, "inv-paused-run-buffers", "World");
        await sm.StartAsync(CancellationToken.None);
        Assert.Equal(InvocationState.Processing, sm.State);

        // The transport is gone, exactly as it is after a pause aborts the attempt.
        await inbound.Writer.CompleteAsync();
        await outbound.Writer.CompleteAsync();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            sm.RunAsync("record", () => Task.FromResult("recorded"), CancellationToken.None).AsTask());

        // The closed stream ends the attempt. It must not become a TerminalException, which the
        // handler would journal as the invocation's final outcome.
        Assert.IsNotType<TerminalException>(failure);

        // A proposal that never reached the runtime must not leave its pooled copy rented: the
        // run is not journaled, so nothing will ever read that buffer back.
        Assert.Empty(RentedBuffers(sm));

        await outbound.Reader.CompleteAsync();
        await inbound.Reader.CompleteAsync();
    }

    /// <summary>
    ///     The ArrayPool rentals the state machine is holding for journaled run results. Held in a
    ///     private field with no accessor, so the test reads it directly.
    /// </summary>
    private static IReadOnlyList<byte[]> RentedBuffers(InvocationStateMachine sm)
    {
        var field = typeof(InvocationStateMachine)
            .GetField("_rentedBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (List<byte[]>?)field.GetValue(sm) ?? [];
    }

    private sealed class FuncServiceProvider(Func<Type, object> factory) : IServiceProvider
    {
        public object GetService(Type serviceType) => factory(serviceType);
    }
}
