using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RasaMic.Protocol;

/// <summary>QR pairing payload: pins the server identity key and endpoints.</summary>
public sealed record PairingPayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string ServerId,
    [property: JsonPropertyName("name")] string ServerName,
    [property: JsonPropertyName("ips")] string[] Ips,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pk")] string StaticPubBase64Url)
{
    public static string Encode(PairingPayload payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, PairingJsonContext.Default.PairingPayload);
        return Base64Url.EncodeToString(json);
    }

    public static PairingPayload Decode(string encoded)
    {
        byte[] json = Base64Url.DecodeFromChars(encoded);
        return JsonSerializer.Deserialize(json, PairingJsonContext.Default.PairingPayload)
            ?? throw new InvalidDataException("Invalid pairing payload.");
    }

    public byte[] StaticPublicKey => Base64Url.DecodeFromChars(StaticPubBase64Url);

    public static PairingPayload FromIdentity(ServerIdentity identity, string name, string[] ips, int port) =>
        new(ProtocolConstants.Version, identity.ServerId, name, ips, port,
            Base64Url.EncodeToString(identity.StaticPublicKey));
}

[JsonSerializable(typeof(PairingPayload))]
internal sealed partial class PairingJsonContext : JsonSerializerContext;