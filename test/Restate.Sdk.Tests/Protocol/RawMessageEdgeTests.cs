using System.Buffers;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Plan 07 §1.2 round-1 residual-gap closure for <see cref="RawMessage" />. The
///     <see cref="RawMessage.PayloadMemory" /> accessor and <see cref="RawMessage.DetachPayload" />
///     buffer-ownership transfer have no in-SDK caller today (the read path consumes
///     <see cref="RawMessage.Payload" /> spans), so they are covered directly here — the §1.3
///     position is "coverable, not excluded". Both the payload-present and payload-absent arms of
///     <c>PayloadMemory</c> are exercised, plus the post-detach Dispose no-op.
/// </summary>
public sealed class RawMessageEdgeTests
{
    private static MessageHeader Header(uint length) =>
        MessageHeader.Create(MessageType.RunCommand, length);

    [Fact]
    public void PayloadMemory_WithPayload_ReturnsExactlyTheRentedSlice()
    {
        var rented = ArrayPool<byte>.Shared.Rent(4);
        rented[0] = 0xDE;
        rented[1] = 0xAD;
        rented[2] = 0xBE;
        rented[3] = 0xEF;
        var message = RawMessage.Create(Header(4), rented, 4);

        Assert.True(message.HasPayload);
        // PayloadMemory must view exactly the declared length, not the (over-allocated) rented buffer.
        Assert.Equal(4, message.PayloadMemory.Length);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, message.PayloadMemory.ToArray());

        message.Dispose();
    }

    [Fact]
    public void PayloadMemory_HeaderOnly_IsEmpty()
    {
        var message = RawMessage.Create(Header(0));

        Assert.False(message.HasPayload);
        Assert.True(message.PayloadMemory.IsEmpty);

        message.Dispose();
    }

    [Fact]
    public void DetachPayload_TransfersOwnership_AndMakesDisposeANoOp()
    {
        var rented = ArrayPool<byte>.Shared.Rent(3);
        rented[0] = 1;
        rented[1] = 2;
        rented[2] = 3;
        var message = RawMessage.Create(Header(3), rented, 3);

        var (buffer, memory) = message.DetachPayload();
        Assert.Same(rented, buffer);
        Assert.Equal(new byte[] { 1, 2, 3 }, memory.ToArray());

        // After detaching, the message no longer owns the buffer: HasPayload is false and Dispose is a
        // no-op (it must NOT return the now-caller-owned buffer to the pool). The caller returns it.
        Assert.False(message.HasPayload);
        message.Dispose();
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
