using RasaMic.Protocol;
using Xunit;

namespace RasaMic.Protocol.Tests;

public class AudioPacketHeaderTests
{
    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var header = new AudioPacketHeader
        {
            Magic = ProtocolConstants.AudioMagic,
            Version = ProtocolConstants.Version,
            MsgType = AudioPacketHeader.TypeAudio,
            Flags = AudioFlags.Fec | AudioFlags.Dtx,
            SessionId = 0xDEADBEEF,
            Seq = 123456,
            CaptureTsUs = 1_700_000_000_123_456UL,
            Reserved = 0,
        };
        Span<byte> buffer = stackalloc byte[ProtocolConstants.AudioHeaderSize];
        AudioPacketHeader.Write(buffer, header);
        var read = AudioPacketHeader.Read(buffer);
        Assert.Equal(header.Magic, read.Magic);
        Assert.Equal(header.Version, read.Version);
        Assert.Equal(header.MsgType, read.MsgType);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.SessionId, read.SessionId);
        Assert.Equal(header.Seq, read.Seq);
        Assert.Equal(header.CaptureTsUs, read.CaptureTsUs);
    }

    [Fact]
    public void Header_IsExactly24Bytes()
    {
        Assert.Equal(24, ProtocolConstants.AudioHeaderSize);
    }

    [Fact]
    public void Nonce_IsDeterministicAndUniquePerSeq()
    {
        Span<byte> n1 = stackalloc byte[12];
        Span<byte> n2 = stackalloc byte[12];
        AudioPacketHeader.WriteNonce(n1, 42, 1, ProtocolConstants.DirectionClientToServer);
        AudioPacketHeader.WriteNonce(n2, 42, 2, ProtocolConstants.DirectionClientToServer);
        Assert.False(n1.SequenceEqual(n2));
        AudioPacketHeader.WriteNonce(n2, 42, 1, ProtocolConstants.DirectionClientToServer);
        Assert.True(n1.SequenceEqual(n2));
    }

    [Fact]
    public void Read_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => AudioPacketHeader.Read(new byte[23]));
    }
}