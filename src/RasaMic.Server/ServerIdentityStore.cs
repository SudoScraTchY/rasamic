using RasaMic.Protocol;

namespace RasaMic.Server;

/// <summary>Loads the persisted server identity, creating it on first run (shared by CLI and GUI).</summary>
public static class ServerIdentityStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RasaMic", "server.pkcs8");

    public static ServerIdentity LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            return ServerIdentity.FromPkcs8(File.ReadAllBytes(path));
        ServerIdentity identity = ServerIdentity.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, identity.ExportPkcs8());
        return identity;
    }
}