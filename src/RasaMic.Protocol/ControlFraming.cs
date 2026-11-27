using System.Buffers.Binary;
using System.Text.Json;

namespace RasaMic.Protocol;

public static class ControlFraming
{
    public static async Task WriteAsync(Stream stream, ControlMessage message, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ControlMessage);
        if (payload.Length > ProtocolConstants.MaxControlFrameSize)
            throw new InvalidOperationException("Control frame exceeds maximum size.");
        byte[] lengthPrefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)payload.Length);
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<ControlMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[]? payload = await ReadRawAsync(stream, ct).ConfigureAwait(false);
        if (payload is null)
            return null;
        return JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.ControlMessage)
            ?? throw new InvalidDataException("Null control message.");
    }

    /// <summary>Reads one length-prefixed frame without interpreting it (control JSON or raw audio).</summary>
    public static async ValueTask<byte[]?> ReadRawAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] lengthPrefix = new byte[2];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
            return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
        if (length == 0 || length > ProtocolConstants.MaxControlFrameSize)
            throw new InvalidDataException($"Invalid control frame length {length}.");
        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated control frame.");
        return payload;
    }

    /// <summary>Synchronous framed write for the dedicated audio sender thread.</summary>
    public static void WriteRaw(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaxControlFrameSize)
            throw new InvalidOperationException("Frame exceeds maximum size.");
        Span<byte> lengthPrefix = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)payload.Length);
        stream.Write(lengthPrefix);
        stream.Write(payload);
        stream.Flush();
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
                return offset == 0 && buffer.Length == 2 ? false : throw new EndOfStreamException("Truncated stream.");
            offset += read;
        }
        return true;
    }
}