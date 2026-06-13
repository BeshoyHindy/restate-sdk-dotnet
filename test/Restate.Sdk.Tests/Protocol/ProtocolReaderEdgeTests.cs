using System.Buffers;
using System.IO.Pipelines;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Tests.Testing;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Plan 07 §1.2 4b-iii (ProtocolReaderEdgeTests). Drives <see cref="ProtocolReader" /> across the
///     partial-frame and boundary paths the §4 suites do not: H9 byte-by-byte drip feed (the
///     post-B3 single-reader resume + examined-position handling), zero-length payload frames,
///     header split across reads, EOF mid-payload → <see cref="ProtocolException" />, and a
///     cancellation token honored mid-read. All waits flow through the 5 s harness watchdog.
/// </summary>
public class ProtocolReaderEdgeTests
{
    private static byte[] FrameBytes(MessageType type, byte[] payload)
    {
        var stream = new MemoryStream();
        WriteFramedMessage(stream, type, payload);
        return stream.ToArray();
    }

    [Fact]
    public async Task DripFeed_OneBytePerFlush_DecodesExactlyOneMessage()
    {
        // H9: a frame dribbled one byte per flush must resume the partial-frame decode and yield
        // EXACTLY one message. The CountingPipeReader proves only one reader ever touches the wire,
        // and the loop is bounded because each flush makes the single ReadAsync progress by a byte.
        var pipe = new Pipe();
        var payload = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        var frame = FrameBytes(MessageType.RunCommand, payload);

        var counting = new CountingPipeReader(pipe.Reader);
        var reader = new ProtocolReader(counting);

        var feed = Task.Run(async () =>
        {
            foreach (var b in frame)
            {
                var mem = pipe.Writer.GetMemory(1);
                mem.Span[0] = b;
                pipe.Writer.Advance(1);
                await pipe.Writer.FlushAsync();
            }

            await pipe.Writer.CompleteAsync();
        });

        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal(MessageType.RunCommand, message!.Value.Header.Type);
        Assert.Equal(payload, message.Value.Payload.ToArray());
        message.Value.Dispose();

        Assert.Null(await AwaitBounded(reader.ReadMessageAsync()));   // clean EOF after the one frame
        await feed;
        Assert.True(counting.PeakPendingReads <= 1, "more than one concurrent wire read observed");
    }

    [Fact]
    public async Task ZeroLengthPayloadFrame_DecodesAsHeaderOnly()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(FrameBytes(MessageType.End, []));
        await pipe.Writer.CompleteAsync();

        var reader = new ProtocolReader(pipe.Reader);
        var message = await AwaitBounded(reader.ReadMessageAsync());

        Assert.NotNull(message);
        Assert.Equal(MessageType.End, message!.Value.Header.Type);
        Assert.False(message.Value.HasPayload);
        Assert.Equal(0u, message.Value.Header.Length);
        message.Value.Dispose();
    }

    [Fact]
    public async Task HeaderSplitAcrossReads_Reassembles()
    {
        // The header (8 bytes) arrives in two flushes — exercises WaitingHeader's "need more data"
        // resume (buffer.Length < MessageHeader.Size returns false, then re-reads).
        var pipe = new Pipe();
        var frame = FrameBytes(MessageType.SleepCommand, [1, 2, 3]);

        var feed = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(frame.AsMemory(0, 3));   // partial header
            await pipe.Writer.FlushAsync();
            await pipe.Writer.WriteAsync(frame.AsMemory(3));      // rest of header + payload
            await pipe.Writer.CompleteAsync();
        });

        var reader = new ProtocolReader(pipe.Reader);
        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal(MessageType.SleepCommand, message!.Value.Header.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Value.Payload.ToArray());
        message.Value.Dispose();
        await feed;
    }

    [Fact]
    public async Task EofMidPayload_ThrowsProtocolException()
    {
        // Header claims 16 payload bytes; only 4 are written before EOF → WaitingPayload + completed.
        var pipe = new Pipe();
        var header = MessageHeader.Create(MessageType.CallCommand, 16);
        header.WriteTo(pipe.Writer);
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3, 4 });
        await pipe.Writer.CompleteAsync();

        var reader = new ProtocolReader(pipe.Reader);
        await Assert.ThrowsAsync<ProtocolException>(() => AwaitBounded(reader.ReadMessageAsync()));
    }

    [Fact]
    public async Task CancellationToken_HonoredMidRead()
    {
        // A read with no data pending parks; cancelling the token must surface the cancellation
        // rather than hang (the watchdog would otherwise have to catch a leak).
        var pipe = new Pipe();
        var reader = new ProtocolReader(pipe.Reader);
        using var cts = new CancellationTokenSource();

        var read = reader.ReadMessageAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitBounded(read));
    }

    [Fact]
    public void StreamConstructor_BuildsAReader()
    {
        // The Stream-accepting constructor is a distinct ctor overload; build over an empty stream
        // and dispose to cover it plus the idempotent Complete().
        using var reader = new ProtocolReader(new MemoryStream());
        reader.Complete();
        reader.Complete();   // second call is a no-op (the _completed guard)
    }

    [Fact]
    public async Task MultiSegmentBuffer_HeaderStraddlesSegments_UsesSlowCopyPath()
    {
        // The single-byte drip-feed keeps everything in one Pipe segment, so the header fast path
        // (buffer.FirstSpan.Length >= MessageHeader.Size) always wins. To exercise the SLOW header
        // path (stackalloc + ReadOnlySequence.CopyTo), the 8-byte header must straddle two buffer
        // segments. A SegmentedPipeReader hands the decoder a genuinely multi-segment ReadOnlySequence
        // split at byte 4 — inside the header — so FirstSpan is shorter than the header.
        var frame = FrameBytes(MessageType.SleepCommand, [9, 8, 7]);
        var reader = new ProtocolReader(new SegmentedPipeReader(frame, splitAt: 4));

        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal(MessageType.SleepCommand, message!.Value.Header.Type);
        Assert.Equal(new byte[] { 9, 8, 7 }, message.Value.Payload.ToArray());
        message.Value.Dispose();
    }

    [Fact]
    public async Task MultiSegmentBuffer_PayloadStraddlesSegments_UsesSlowCopyPath()
    {
        // Split AFTER the header but inside the payload so the payload slice is multi-segment,
        // exercising the non-single-segment payload CopyTo branch.
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        var frame = FrameBytes(MessageType.CallCommand, payload);
        // Header is 8 bytes; split at 10 leaves the header whole but the 6-byte payload straddles.
        var reader = new ProtocolReader(new SegmentedPipeReader(frame, splitAt: 10));

        var message = await AwaitBounded(reader.ReadMessageAsync());
        Assert.NotNull(message);
        Assert.Equal(MessageType.CallCommand, message!.Value.Header.Type);
        Assert.Equal(payload, message.Value.Payload.ToArray());
        message.Value.Dispose();
    }

    /// <summary>
    ///     A <see cref="PipeReader" /> that returns one fixed buffer as a genuinely multi-segment
    ///     <see cref="ReadOnlySequence{T}" /> split at <c>splitAt</c>, then signals completion. This
    ///     forces <see cref="ProtocolReader" />'s slow (multi-segment) header/payload copy paths,
    ///     which an in-memory <see cref="Pipe" /> never produces for small frames.
    /// </summary>
    private sealed class SegmentedPipeReader : PipeReader
    {
        private readonly ReadOnlySequence<byte> _sequence;

        public SegmentedPipeReader(byte[] data, int splitAt)
        {
            var first = new Segment(data.AsMemory(0, splitAt));
            var second = first.Append(data.AsMemory(splitAt));
            _sequence = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            new(new ReadResult(_sequence, isCanceled: false, isCompleted: true));

        public override bool TryRead(out ReadResult result)
        {
            result = new ReadResult(_sequence, isCanceled: false, isCompleted: true);
            return true;
        }

        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

            public Segment Append(ReadOnlyMemory<byte> memory)
            {
                var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
                Next = next;
                return next;
            }
        }
    }
}
