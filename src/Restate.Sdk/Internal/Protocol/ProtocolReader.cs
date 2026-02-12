using System.Buffers;
using System.IO.Pipelines;

namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Reads length-prefixed protocol frames from a PipeReader.
///     Handles multi-segment buffers and partial reads.
/// </summary>
internal sealed class ProtocolReader : IDisposable
{
    private readonly PipeReader _reader;
    private MessageHeader _pendingHeader;
    private DecoderState _state = DecoderState.WaitingHeader;
    private bool _completed;

    public ProtocolReader(PipeReader reader)
    {
        _reader = reader;
    }

    public ProtocolReader(Stream stream) : this(PipeReader.Create(stream))
    {
    }

    public void Dispose()
    {
        try { Complete(); } catch { /* stream may already be broken */ }
    }

    /// <summary>
    ///     Reads the next message. Returns null on clean stream end.
    ///     Throws ProtocolException on truncated stream.
    /// </summary>
    public async ValueTask<RawMessage?> ReadMessageAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryParseMessage(ref buffer, out var message))
            {
                // Only mark consumed bytes. Do NOT pass buffer.End as examined —
                // that would tell the PipeReader we've seen all remaining data,
                // causing the next ReadAsync to block until NEW network data arrives
                // even when there are unconsumed bytes in the buffer.
                _reader.AdvanceTo(buffer.Start);
                return message;
            }

            // Parse failed — we need more data. Mark everything as examined so
            // ReadAsync waits for new data beyond what we've already seen.
            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                if (_state == DecoderState.WaitingPayload)
                    throw new ProtocolException("Stream ended with incomplete message");
                return null;
            }
        }
    }

    public void Complete(Exception? exception = null)
    {
        if (_completed) return;
        _completed = true;
        _reader.Complete(exception);
    }

    private bool TryParseMessage(ref ReadOnlySequence<byte> buffer, out RawMessage message)
    {
        message = default;

        if (_state == DecoderState.WaitingHeader)
        {
            if (buffer.Length < MessageHeader.Size)
                return false;

            Span<byte> headerBytes = stackalloc byte[MessageHeader.Size];
            buffer.Slice(0, MessageHeader.Size).CopyTo(headerBytes);
            _pendingHeader = MessageHeader.Read(headerBytes);
            buffer = buffer.Slice(MessageHeader.Size);
            _state = DecoderState.WaitingPayload;
        }

        if (_state == DecoderState.WaitingPayload)
        {
            if (_pendingHeader.Length == 0)
            {
                message = RawMessage.Create(_pendingHeader);
                _state = DecoderState.WaitingHeader;
                return true;
            }

            if (buffer.Length < _pendingHeader.Length)
                return false;

            var payloadSlice = buffer.Slice(0, _pendingHeader.Length);
            var rented = ArrayPool<byte>.Shared.Rent((int)_pendingHeader.Length);
            payloadSlice.CopyTo(rented);

            message = RawMessage.Create(_pendingHeader, rented, (int)_pendingHeader.Length);
            buffer = buffer.Slice(_pendingHeader.Length);
            _state = DecoderState.WaitingHeader;
            return true;
        }

        return false;
    }

    private enum DecoderState
    {
        WaitingHeader,
        WaitingPayload
    }
}