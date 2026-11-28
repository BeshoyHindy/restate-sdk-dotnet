using System.IO.Pipelines;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Protocol;

public class ProtocolReaderWriterTests
{
    [Fact]
    public async Task Roundtrip_SingleMessage()
    {
        var pipe = new Pipe();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var writer = new ProtocolWriter(pipe.Writer);
        writer.WriteMessage(MessageType.RunCommand, MessageFlags.RequiresAck, payload);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        var msg = await reader.ReadMessageAsync();

        Assert.NotNull(msg);
        Assert.Equal(MessageType.RunCommand, msg.Value.Header.Type);
        Assert.True(msg.Value.Header.Flags.HasRequiresAck());
        Assert.Equal(payload, msg.Value.Payload.ToArray());

        msg.Value.Dispose();

        var end = await reader.ReadMessageAsync();
        Assert.Null(end);
    }

    [Fact]
    public async Task Roundtrip_MultipleMessages()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);

        writer.WriteMessage(MessageType.Start, new byte[] { 10, 20 });
        writer.WriteMessage(MessageType.InputCommand, new byte[] { 30 });
        writer.WriteHeaderOnly(MessageType.End);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);

        var m1 = await reader.ReadMessageAsync();
        Assert.Equal(MessageType.Start, m1!.Value.Header.Type);
        Assert.Equal(2, m1.Value.Payload.Length);
        m1.Value.Dispose();

        var m2 = await reader.ReadMessageAsync();
        Assert.Equal(MessageType.InputCommand, m2!.Value.Header.Type);
        Assert.Equal(1, m2.Value.Payload.Length);
        m2.Value.Dispose();

        var m3 = await reader.ReadMessageAsync();
        Assert.Equal(MessageType.End, m3!.Value.Header.Type);
        Assert.False(m3.Value.HasPayload);
        m3.Value.Dispose();

        Assert.Null(await reader.ReadMessageAsync());
    }

    [Fact]
    public async Task EmptyPayload()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);
        writer.WriteHeaderOnly(MessageType.End);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        var msg = await reader.ReadMessageAsync();

        Assert.NotNull(msg);
        Assert.Equal(0u, msg.Value.Header.Length);
        Assert.False(msg.Value.HasPayload);
        Assert.Equal(0, msg.Value.Payload.Length);
    }

    [Fact]
    public async Task EmptyStream_ReturnsNull()
    {
        var pipe = new Pipe();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        Assert.Null(await reader.ReadMessageAsync());
    }

    [Fact]
    public async Task TruncatedStream_Throws()
    {
        var pipe = new Pipe();

        // Write a header that claims 100 bytes of payload, but don't write the payload
        var header = MessageHeader.Create(MessageType.RunCommand, 100);
        header.WriteTo(pipe.Writer);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadMessageAsync().AsTask());
    }

    [Theory]
    [InlineData(ProtocolReader.DefaultMaxMessageLength + 1u)]
    [InlineData(0x80000000u)] // would overflow the int cast used to rent the payload buffer
    [InlineData(uint.MaxValue)]
    public async Task OversizedLengthPrefix_ThrowsBeforeBufferingPayload(uint declaredLength)
    {
        var pipe = new Pipe();

        // Only the header is written: the reader must reject it without waiting for the payload.
        MessageHeader.Create(MessageType.RunCommand, declaredLength).WriteTo(pipe.Writer);
        await pipe.Writer.FlushAsync();

        var reader = new ProtocolReader(pipe.Reader);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadMessageAsync().AsTask());
        Assert.Contains("exceeding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LengthPrefixAtCustomLimit_IsAccepted_AndOneAboveIsRejected()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);
        writer.WriteMessage(MessageType.RunCommand, new byte[16]);
        writer.WriteMessage(MessageType.RunCommand, new byte[17]);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader, maxMessageLength: 16);

        var accepted = await reader.ReadMessageAsync();
        Assert.Equal(16, accepted!.Value.Payload.Length);
        accepted.Value.Dispose();

        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadMessageAsync().AsTask());
    }

    [Fact]
    public async Task ChunkedData_ReassemblesCorrectly()
    {
        var pipe = new Pipe();
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        // Write header + payload as raw bytes in small chunks
        var ms = new MemoryStream();
        var header = MessageHeader.Create(MessageType.CallCommand, (uint)payload.Length);
        Span<byte> headerBytes = stackalloc byte[MessageHeader.Size];
        header.Write(headerBytes);

        var allBytes = new byte[MessageHeader.Size + payload.Length];
        headerBytes.CopyTo(allBytes);
        payload.CopyTo(allBytes.AsSpan(MessageHeader.Size));

        // Feed in 3-byte chunks
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < allBytes.Length; i += 3)
            {
                var chunk = allBytes.AsSpan(i, Math.Min(3, allBytes.Length - i));
                var mem = pipe.Writer.GetMemory(chunk.Length);
                chunk.CopyTo(mem.Span);
                pipe.Writer.Advance(chunk.Length);
                await pipe.Writer.FlushAsync();
            }

            pipe.Writer.Complete();
        });

        var reader = new ProtocolReader(pipe.Reader);
        var msg = await reader.ReadMessageAsync();

        Assert.NotNull(msg);
        Assert.Equal(MessageType.CallCommand, msg.Value.Header.Type);
        Assert.Equal(payload, msg.Value.Payload.ToArray());
        msg.Value.Dispose();
    }

    [Fact]
    public async Task GetPayloadMemory_DirectWrite()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);

        var mem = writer.GetPayloadMemory(MessageType.OutputCommand, MessageFlags.None, 4);
        mem.Span[0] = 0xDE;
        mem.Span[1] = 0xAD;
        mem.Span[2] = 0xBE;
        mem.Span[3] = 0xEF;
        writer.AdvancePayload(4);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        var msg = await reader.ReadMessageAsync();

        Assert.NotNull(msg);
        Assert.Equal(MessageType.OutputCommand, msg.Value.Header.Type);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, msg.Value.Payload.ToArray());
        msg.Value.Dispose();
    }

    [Fact]
    public async Task FlagsPreserved()
    {
        var pipe = new Pipe();
        var writer = new ProtocolWriter(pipe.Writer);
        writer.WriteMessage(MessageType.SleepCommand, MessageFlags.Completed | MessageFlags.RequiresAck, []);
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();

        var reader = new ProtocolReader(pipe.Reader);
        var msg = await reader.ReadMessageAsync();

        Assert.True(msg!.Value.Header.Flags.IsCompleted());
        Assert.True(msg.Value.Header.Flags.HasRequiresAck());
    }
}