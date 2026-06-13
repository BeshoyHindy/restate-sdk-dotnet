using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Plan 07 §1.2 protocol-serde lane. Closes the residual hand-written gaps the §4 suites and the
///     existing Protocol*EdgeTests do not reach:
///       * <see cref="MessageHeader.TryRead" /> SUCCESS path (lines 58-59) — the existing
///         MessageHeaderTests only assert the too-small failure branch, never a buffer long enough
///         to fall through to <c>header = Read(source); return true;</c>;
///       * the <see cref="ProtocolException" /> (message, inner) constructor (lines 9-11), which the
///         hand-written codec never builds (every decode throw uses the single-arg ctor) yet is the
///         wrapping ctor a caller would use to chain a malformed-frame cause.
/// </summary>
public class ProtocolEdgeTests
{
    [Fact]
    public void MessageHeader_TryRead_ExactBuffer_DecodesAndReturnsTrue()
    {
        // A buffer of exactly Size bytes must take the success fall-through (not the too-small early
        // return), decoding Type/Flags/Length back out — the inverse of MessageHeaderTests' failure case.
        var original = MessageHeader.Create(MessageType.CallCommand, MessageFlags.RequiresAck, 1234);
        Span<byte> buffer = stackalloc byte[MessageHeader.Size];
        original.Write(buffer);

        Assert.True(MessageHeader.TryRead(buffer, out var header));
        Assert.Equal(MessageType.CallCommand, header.Type);
        Assert.True(header.Flags.HasRequiresAck());
        Assert.Equal(1234u, header.Length);
    }

    [Fact]
    public void MessageHeader_TryRead_OversizedBuffer_ReadsLeadingFrame()
    {
        // A buffer LONGER than Size still succeeds, reading only the leading 8 bytes — proves the
        // length check is `< Size`, not `!= Size`, so trailing payload bytes never block the read.
        var original = MessageHeader.Create(MessageType.SleepCommand, 7);
        var buffer = new byte[MessageHeader.Size + 16];
        original.Write(buffer);

        Assert.True(MessageHeader.TryRead(buffer, out var header));
        Assert.Equal(MessageType.SleepCommand, header.Type);
        Assert.Equal(7u, header.Length);
    }

    [Fact]
    public void ProtocolException_InnerConstructor_PreservesMessageAndCause()
    {
        // The (string, Exception) ctor is the chaining overload: it must set both Message and
        // InnerException so a malformed-frame decode failure can be surfaced with its root cause.
        var cause = new InvalidOperationException("bad protobuf");
        var ex = new ProtocolException("frame decode failed", cause);

        Assert.Equal("frame decode failed", ex.Message);
        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public void ProtocolException_MessageConstructor_HasNoInner()
    {
        // The single-arg ctor (already exercised by the decode-throw tests) leaves InnerException null.
        var ex = new ProtocolException("standalone");
        Assert.Equal("standalone", ex.Message);
        Assert.Null(ex.InnerException);
    }
}
