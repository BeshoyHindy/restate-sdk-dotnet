using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Writes length-prefixed protocol frames to an IBufferWriter.
/// </summary>
internal sealed class ProtocolWriter : IDisposable
{
    private readonly PipeWriter? _pipeWriter;
    private readonly IBufferWriter<byte> _writer;
    private bool _completed;

    public ProtocolWriter(IBufferWriter<byte> writer)
    {
        _writer = writer;
    }

    public ProtocolWriter(PipeWriter writer)
    {
        _writer = writer;
        _pipeWriter = writer;
    }

    public ProtocolWriter(Stream stream) : this(PipeWriter.Create(stream))
    {
    }

    public void Dispose()
    {
        try { Complete(); } catch { /* stream may already be broken */ }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteMessage(MessageType type, MessageFlags flags, ReadOnlySpan<byte> payload)
    {
        var header = MessageHeader.Create(type, flags, (uint)payload.Length);
        header.WriteTo(_writer);

        if (payload.Length > 0)
        {
            var span = _writer.GetSpan(payload.Length);
            payload.CopyTo(span);
            _writer.Advance(payload.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteMessage(MessageType type, ReadOnlySpan<byte> payload)
    {
        WriteMessage(type, MessageFlags.None, payload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeaderOnly(MessageType type, MessageFlags flags = MessageFlags.None)
    {
        var header = MessageHeader.Create(type, flags, 0);
        header.WriteTo(_writer);
    }

    /// <summary>
    ///     Gets a memory region for the caller to write payload into directly.
    ///     Call AdvancePayload after writing.
    /// </summary>
    public Memory<byte> GetPayloadMemory(MessageType type, MessageFlags flags, int payloadSize)
    {
        var header = MessageHeader.Create(type, flags, (uint)payloadSize);
        header.WriteTo(_writer);
        return _writer.GetMemory(payloadSize);
    }

    public void AdvancePayload(int payloadSize)
    {
        _writer.Advance(payloadSize);
    }

    public ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        if (_pipeWriter is not null)
            return _pipeWriter.FlushAsync(ct);
        return default;
    }

    public void Complete(Exception? exception = null)
    {
        if (_completed) return;
        _completed = true;
        _pipeWriter?.Complete(exception);
    }
}