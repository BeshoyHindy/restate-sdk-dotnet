using System.Buffers;
using System.IO.Pipelines;
using Restate.Sdk.Internal.Protocol;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Plan 07 §1.2 4b-iii (ProtocolWriterEdgeTests). Covers the <see cref="ProtocolWriter" /> and
///     <see cref="MessageHeader" />/<see cref="MessageFlags" /> surface the §4 suites and the
///     existing ProtocolReaderWriterTests do not: header-only writes, a large payload spanning
///     multiple pipe segments, full flags round-trip, the IBufferWriter and Stream constructors,
///     and idempotent Complete.
/// </summary>
public class ProtocolWriterEdgeTests
{
    [Fact]
    public async Task WriteHeaderOnly_EmitsZeroLengthFrameWithFlags()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);
        writer.WriteHeaderOnly(MessageType.End, MessageFlags.RequiresAck);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var reader = new ProtocolReader(pipe.Reader);
        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal(MessageType.End, message!.Value.Header.Type);
        Assert.Equal(0u, message.Value.Header.Length);
        Assert.True(message.Value.Header.Flags.HasRequiresAck());
        message.Value.Dispose();
    }

    [Fact]
    public async Task LargePayload_SpanningSegments_RoundTrips()
    {
        // A payload larger than a single default pipe segment forces GetSpan to span segments; the
        // reader's multi-segment CopyTo path reassembles it byte-for-byte. The write happens on a
        // background task: a 64 KB+ payload exceeds the default Pipe PauseWriterThreshold, so the
        // synchronous flush would otherwise block waiting for a reader that only starts AFTER the
        // flush returns — a single-thread backpressure deadlock. Writing concurrently lets the main
        // thread's read drain the pipe so the flush completes.
        var pipe = new Pipe();
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var write = Task.Run(async () =>
        {
            var writer = new ProtocolWriter(pipe.Writer);
            writer.WriteMessage(MessageType.OutputCommand, payload);
            await pipe.Writer.FlushAsync();
            await pipe.Writer.CompleteAsync();
        });

        var reader = new ProtocolReader(pipe.Reader);
        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal((uint)payload.Length, message!.Value.Header.Length);
        Assert.Equal(payload, message.Value.Payload.ToArray());
        message.Value.Dispose();
        await AwaitBounded(write);
    }

    [Fact]
    public void IBufferWriterConstructor_WritesFramesWithoutAFlushTarget()
    {
        // The IBufferWriter ctor has no PipeWriter, so FlushAsync is a no-op returning default.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new ProtocolWriter(buffer);
        writer.WriteMessage(MessageType.RunCommand, [9, 8, 7]);

        var flush = writer.FlushAsync();
        Assert.True(flush.IsCompletedSuccessfully);

        // The framed bytes are header(8) + payload(3).
        Assert.Equal(MessageHeader.Size + 3, buffer.WrittenCount);
        var header = MessageHeader.Read(buffer.WrittenSpan);
        Assert.Equal(MessageType.RunCommand, header.Type);
        Assert.Equal(3u, header.Length);

        // Complete() on an IBufferWriter-backed writer takes the `_pipeWriter is null` arm of the
        // null-conditional (ProtocolWriter.cs:90): there is no PipeWriter to complete, so it is a
        // pure flag flip — and a second Complete is idempotent via the _completed guard.
        writer.Complete();
        writer.Complete();
    }

    [Fact]
    public void StreamConstructor_AndIdempotentComplete()
    {
        using var writer = new ProtocolWriter(new MemoryStream());
        writer.WriteHeaderOnly(MessageType.End);
        writer.Complete();
        writer.Complete();   // second call is a no-op (the _completed guard)
    }

    // ---- MessageHeader / MessageFlags coverage (kept here — they frame every ProtocolWriter op) -

    [Fact]
    public void MessageHeader_TryWrite_RespectsBufferLength()
    {
        var header = MessageHeader.Create(MessageType.SleepCommand, MessageFlags.Completed, 42);

        Span<byte> tooSmall = stackalloc byte[MessageHeader.Size - 1];
        Assert.False(header.TryWrite(tooSmall));

        Span<byte> exact = stackalloc byte[MessageHeader.Size];
        Assert.True(header.TryWrite(exact));
        var roundTrip = MessageHeader.Read(exact);
        Assert.Equal(MessageType.SleepCommand, roundTrip.Type);
        Assert.Equal(42u, roundTrip.Length);
        Assert.True(roundTrip.Flags.IsCompleted());
    }

    [Fact]
    public void MessageHeader_WithFlags_And_ToString()
    {
        var header = MessageHeader.Create(MessageType.RunCommand, 7);
        var withFlags = header.WithFlags(MessageFlags.RequiresAck);

        Assert.True(withFlags.Flags.HasRequiresAck());
        Assert.Equal(MessageType.RunCommand, withFlags.Type);
        Assert.Equal(7u, withFlags.Length);
        Assert.Contains("RunCommand", header.ToString());
    }

    [Fact]
    public void MessageFlags_PredicatesCoverBothBranches()
    {
        Assert.True((MessageFlags.Completed | MessageFlags.RequiresAck).IsCompleted());
        Assert.True((MessageFlags.Completed | MessageFlags.RequiresAck).HasRequiresAck());
        Assert.False(MessageFlags.None.IsCompleted());
        Assert.False(MessageFlags.None.HasRequiresAck());
    }

    [Fact]
    public void MessageType_Categories_CoverEachBranch()
    {
        Assert.True(MessageType.RunCommand.IsCommand());
        Assert.False(MessageType.RunCommand.IsNotification());
        Assert.True(MessageType.RunCompletion.IsNotification());
        Assert.False(MessageType.RunCompletion.IsCommand());
        Assert.True(MessageType.Start.IsControlMessage());
        Assert.False(MessageType.RunCommand.IsControlMessage());
    }
}
