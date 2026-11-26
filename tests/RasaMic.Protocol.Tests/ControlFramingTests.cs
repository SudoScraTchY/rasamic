using RasaMic.Protocol;
using Xunit;

namespace RasaMic.Protocol.Tests;

public class ControlFramingTests
{
    [Fact]
    public async Task Roundtrip_AllMessageTypes()
    {
        ControlMessage[] messages =
        [
            new Hello(1, "client-1", "Pixel 9", new byte[] { 1, 2, 3 }, 0),
            new HelloAck("srv", "Rasa Server", new byte[] { 4 }, new byte[] { 5 }, 42, 1),
            new PairConfirm("123456"),
            new Resume(42, new byte[] { 9 }),
            new SetMute(true),
            new SetGain(-6.5f),
            new SetBitrate(32000),
            new SetFrameSize(20),
            new SetNoiseSuppression(true),
            new SetAgc(false),
            new SetPolarPattern(2),
            new Stats(12.5f, 0.1f, 3.2f, 20f, 1000, 60000),
            new Ping(123456789),
            new Pong(123456789),
            new ClientInfo("iPhone 17", 85, "wifi"),
            new Bye("user"),
            new Error("unsupported version", "upgrade to 1.x"),
        ];

        using var stream = new MemoryStream();
        foreach (var m in messages)
            await ControlFraming.WriteAsync(stream, m);
        stream.Position = 0;
        foreach (var expected in messages)
        {
            var actual = await ControlFraming.ReadAsync(stream);
            Assert.NotNull(actual);
            string expectedJson = System.Text.Json.JsonSerializer.Serialize(expected, ProtocolJsonContext.Default.ControlMessage);
            string actualJson = System.Text.Json.JsonSerializer.Serialize(actual, ProtocolJsonContext.Default.ControlMessage);
            Assert.Equal(expectedJson, actualJson);
        }
        Assert.Null(await ControlFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedLength()
    {
        using var stream = new MemoryStream(new byte[] { 0xFF, 0xFF });
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_ThrowsOnTruncatedPayload()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x10, 1, 2, 3 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => ControlFraming.ReadAsync(stream));
    }
}

public class PairingPayloadTests
{
    [Fact]
    public void EncodeDecode_Roundtrip()
    {
        using var identity = ServerIdentity.Create();
        var payload = PairingPayload.FromIdentity(identity, "Rasa Server", ["192.168.1.10", "10.0.0.5"], 47300);
        string encoded = PairingPayload.Encode(payload);
        var decoded = PairingPayload.Decode(encoded);
        Assert.Equal(encoded, PairingPayload.Encode(decoded));
        Assert.Equal(identity.StaticPublicKey, decoded.StaticPublicKey);
        Assert.Equal(identity.ServerId, decoded.ServerId);
    }

    [Fact]
    public void Encoded_IsUrlSafe()
    {
        using var identity = ServerIdentity.Create();
        var payload = PairingPayload.FromIdentity(identity, "Rasa Server", ["192.168.1.10"], 47300);
        string encoded = PairingPayload.Encode(payload);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }
}