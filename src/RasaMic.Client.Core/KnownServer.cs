using System.Text.Json;
using System.Text.Json.Serialization;

namespace RasaMic.Client.Core;

/// <summary>A remembered server: identity key pin + last-known endpoints for silent reconnect.</summary>
public sealed record KnownServer(
    [property: JsonPropertyName("id")] string ServerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pk")] string StaticPubBase64Url,
    [property: JsonPropertyName("ips")] string[] LastIps,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("tcpAudio")] bool TcpAudio = false);

public sealed class KnownServerStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public KnownServerStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RasaMic", "known-servers.json");
    }

    public List<KnownServer> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new List<KnownServer>();
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(_path), KnownServersJsonContext.Default.ListKnownServer)
                    ?? new List<KnownServer>();
            }
            catch (JsonException)
            {
                return new List<KnownServer>();
            }
        }
    }

    public void Remember(KnownServer server)
    {
        lock (_gate)
        {
            List<KnownServer> all = Load();
            all.RemoveAll(s => s.ServerId == server.ServerId);
            all.Add(server);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(all, KnownServersJsonContext.Default.ListKnownServer));
        }
    }
}

[JsonSerializable(typeof(List<KnownServer>))]
internal sealed partial class KnownServersJsonContext : JsonSerializerContext;