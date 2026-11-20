using System.Buffers.Binary;

namespace RasaMic.Protocol;

[Flags]
public enum AudioFlags : byte
{
    None = 0,
    Fec = 1,
    Dtx = 2,
    Stereo = 4,
}

public struct AudioPacketHeader
{
    public byte Magic;
    public byte Version;
    public byte MsgType;
    public AudioFlags Flags;
    public uint SessionId;
    public uint Seq;
    public ulong CaptureTsUs;
    public uint Reserved;

    public const byte TypeAudio = 1;

    public static void Write(Span<byte> destination, in AudioPacketHeader header)
    {
        if (destination.Length < ProtocolConstants.AudioHeaderSize)
            throw new ArgumentException("Destination too small for audio header.", nameof(destination));
        destination[0] = header.Magic;
        destination[1] = header.Version;
        destination[2] = header.MsgType;
        destination[3] = (byte)header.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], header.SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], header.CaptureTsUs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], header.Reserved);
    }

    public static AudioPacketHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.AudioHeaderSize)
            throw new ArgumentException("Source too small for audio header.", nameof(source));
        return new AudioPacketHeader
        {
            Magic = source[0],
            Version = source[1],
            MsgType = source[2],
            Flags = (AudioFlags)source[3],
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            Seq = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            CaptureTsUs = BinaryPrimitives.ReadUInt64LittleEndian(source[12..]),
            Reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
        };
    }

    public static void WriteNonce(Span<byte> nonce, uint sessionId, uint seq, uint direction)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[0..], sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[4..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], direction);
    }
}