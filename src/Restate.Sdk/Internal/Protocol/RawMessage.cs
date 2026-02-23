using System.Buffers;

namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     A protocol message with header and optional pooled payload buffer.
///     Dispose to return the payload buffer to the pool.
/// </summary>
internal struct RawMessage : IDisposable
{
    private byte[]? _rentedBuffer;
    private int _payloadLength;

    public MessageHeader Header { get; }
    public bool HasPayload => _payloadLength > 0;
    public ReadOnlySpan<byte> Payload => HasPayload ? _rentedBuffer.AsSpan(0, _payloadLength) : [];

    public ReadOnlyMemory<byte> PayloadMemory =>
        HasPayload ? _rentedBuffer.AsMemory(0, _payloadLength) : ReadOnlyMemory<byte>.Empty;

    private RawMessage(MessageHeader header, byte[]? buffer, int length)
    {
        Header = header;
        _rentedBuffer = buffer;
        _payloadLength = length;
    }

    public static RawMessage Create(MessageHeader header)
    {
        return new RawMessage(header, null, 0);
    }

    public static RawMessage Create(MessageHeader header, byte[] rentedBuffer, int length)
    {
        return new RawMessage(header, rentedBuffer, length);
    }

    /// <summary>
    ///     Transfers ownership of the pooled payload buffer to the caller.
    ///     After this call, Dispose() becomes a no-op — the caller is responsible
    ///     for returning the buffer to ArrayPool when done.
    /// </summary>
    public (byte[] Buffer, ReadOnlyMemory<byte> Memory) DetachPayload()
    {
        var buf = _rentedBuffer!;
        var mem = buf.AsMemory(0, _payloadLength);
        _rentedBuffer = null;
        _payloadLength = 0;
        return (buf, mem);
    }

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
            _payloadLength = 0;
        }
    }
}