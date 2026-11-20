using System.Security.Cryptography;

namespace RasaMic.Protocol;

/// <summary>
/// Persistent server identity: static ECDH P-256 key. ServerId is derived from the public key.
/// QR pairing pins this key; the handshake mixes ephemeral-ephemeral and ephemeral-static DH
/// so a LAN MITM without the static private key cannot derive session keys.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    private readonly ECDiffieHellman _static;

    public byte[] StaticPublicKey { get; }
    public string ServerId { get; }

    private ServerIdentity(ECDiffieHellman staticKey)
    {
        _static = staticKey;
        StaticPublicKey = staticKey.PublicKey.ExportSubjectPublicKeyInfo();
        ServerId = Convert.ToHexString(SHA256.HashData(StaticPublicKey).AsSpan(0, 8)).ToLowerInvariant();
    }

    public static ServerIdentity Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static ServerIdentity FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new ServerIdentity(key);
    }

    public byte[] ExportPkcs8() => _static.ExportPkcs8PrivateKey();

    internal byte[] DeriveWithStatic(byte[] peerPublicKeySpki) => SessionCrypto.Dh(_static, peerPublicKeySpki);

    public void Dispose() => _static.Dispose();
}

public sealed record SessionKeys(byte[] ControlKey, byte[] AudioKey)
{
    public const int KeySize = 32;
}

public static class SessionCrypto
{
    private static readonly byte[] HkdfInfo = "rasamic-v1-keys"u8.ToArray();

    public static ECDiffieHellman CreateEphemeral() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] ExportPublic(ECDiffieHellman key) => key.PublicKey.ExportSubjectPublicKeyInfo();

    /// <summary>Client-side derivation. serverStaticPub comes from the QR payload when pinned.</summary>
    public static SessionKeys DeriveClient(ECDiffieHellman clientEph, byte[] serverEphPub, byte[] serverStaticPub)
    {
        byte[] dh1 = Dh(clientEph, serverEphPub);
        byte[] dh2 = Dh(clientEph, serverStaticPub);
        return Derive(dh1, dh2, ExportPublic(clientEph), serverEphPub);
    }

    /// <summary>Server-side derivation; must produce identical keys to <see cref="DeriveClient"/>.</summary>
    public static SessionKeys DeriveServer(ECDiffieHellman serverEph, byte[] clientEphPub, ServerIdentity identity)
    {
        byte[] dh1 = Dh(serverEph, clientEphPub);
        byte[] dh2 = identity.DeriveWithStatic(clientEphPub);
        return Derive(dh1, dh2, clientEphPub, ExportPublic(serverEph));
    }

    private static SessionKeys Derive(byte[] dh1, byte[] dh2, byte[] clientEphPub, byte[] serverEphPub)
    {
        byte[] shared = new byte[dh1.Length + dh2.Length];
        dh1.CopyTo(shared, 0);
        dh2.CopyTo(shared, dh1.Length);

        byte[] salt = SHA256.HashData(Concat(clientEphPub, serverEphPub));
        byte[] okm = new byte[SessionKeys.KeySize * 2];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, okm, salt, HkdfInfo);
        CryptographicOperations.ZeroMemory(shared);
        return new SessionKeys(okm[..SessionKeys.KeySize], okm[SessionKeys.KeySize..]);
    }

    /// <summary>6-digit numeric comparison code for TOFU (manual-IP) pairing.</summary>
    public static string PairingCode(SessionKeys keys)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Concat(keys.ControlKey, keys.AudioKey), hash);
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) % 1_000_000;
        return value.ToString("D6");
    }

    public static byte[] ResumeProof(byte[] audioKey, uint sessionId)
    {
        Span<byte> sid = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sid, sessionId);
        byte[] hash = SHA256.HashData(Concat(audioKey, sid.ToArray()));
        return hash[..16];
    }

    internal static byte[] Dh(ECDiffieHellman priv, byte[] peerPublicKeySpki)
    {
        using var peer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peer.ImportSubjectPublicKeyInfo(peerPublicKeySpki, out _);
        return priv.DeriveKeyMaterial(peer.PublicKey);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

/// <summary>AES-256-GCM sealing for audio packets and post-handshake control payloads.</summary>
public sealed class AesGcmSealer : IDisposable
{
    private readonly AesGcm _aes;

    public AesGcmSealer(byte[] key) => _aes = new AesGcm(key, ProtocolConstants.AesGcmTagSize);

    /// <summary>Encrypts in place layout: ciphertext then 16-byte tag. Returns total length.</summary>
    public int Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> output)
    {
        int tagOffset = plaintext.Length;
        _aes.Encrypt(nonce, plaintext, output[..tagOffset], output.Slice(tagOffset, ProtocolConstants.AesGcmTagSize), aad);
        return plaintext.Length + ProtocolConstants.AesGcmTagSize;
    }

    /// <summary>Decrypts ciphertext+tag; returns plaintext length or throws on auth failure.</summary>
    public int Open(ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> output)
    {
        int cipherLen = ciphertextAndTag.Length - ProtocolConstants.AesGcmTagSize;
        if (cipherLen < 0)
            throw new CryptographicException("Truncated ciphertext.");
        _aes.Decrypt(nonce, ciphertextAndTag[..cipherLen], ciphertextAndTag[cipherLen..], output[..cipherLen], aad);
        return cipherLen;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>Sliding 64-packet anti-replay window over the audio sequence number.</summary>
public sealed class ReplayWindow
{
    private ulong _bitmap;
    private uint _highest;
    private bool _initialized;

    public bool CheckAndUpdate(uint seq)
    {
        if (!_initialized)
        {
            _initialized = true;
            _highest = seq;
            _bitmap = 1;
            return true;
        }

        if (seq > _highest)
        {
            uint shift = seq - _highest;
            _bitmap = shift >= 64 ? 1UL : (_bitmap << (int)shift) | 1UL;
            _highest = seq;
            return true;
        }

        uint diff = _highest - seq;
        if (diff >= 64)
            return false;
        ulong mask = 1UL << (int)diff;
        if ((_bitmap & mask) != 0)
            return false;
        _bitmap |= mask;
        return true;
    }
}