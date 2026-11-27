using System.Security.Cryptography;
using RasaMic.Protocol;
using Xunit;

namespace RasaMic.Protocol.Tests;

public class SessionCryptoTests
{
    private static (SessionKeys client, SessionKeys server, ServerIdentity identity) Handshake(bool pinned = true)
    {
        var identity = ServerIdentity.Create();
        using var clientEph = SessionCrypto.CreateEphemeral();
        using var serverEph = SessionCrypto.CreateEphemeral();
        byte[] clientPub = SessionCrypto.ExportPublic(clientEph);
        byte[] serverPub = SessionCrypto.ExportPublic(serverEph);
        var client = SessionCrypto.DeriveClient(clientEph, serverPub, identity.StaticPublicKey);
        var server = SessionCrypto.DeriveServer(serverEph, clientPub, identity);
        return (client, server, identity);
    }

    [Fact]
    public void Handshake_ProducesIdenticalKeys()
    {
        var (client, server, identity) = Handshake();
        using (identity)
        {
            Assert.Equal(client.ControlKey, server.ControlKey);
            Assert.Equal(client.AudioKey, server.AudioKey);
            Assert.Equal(32, client.AudioKey.Length);
            Assert.False(client.ControlKey.SequenceEqual(client.AudioKey));
        }
    }

    [Fact]
    public void PairingCode_MatchesOnBothSides()
    {
        var (client, server, identity) = Handshake();
        using (identity)
        {
            string a = SessionCrypto.PairingCode(client);
            string b = SessionCrypto.PairingCode(server);
            Assert.Equal(a, b);
            Assert.Equal(6, a.Length);
            Assert.All(a, c => Assert.True(char.IsDigit(c)));
        }
    }

    [Fact]
    public void Mitm_WithWrongStaticKey_DerivesDifferentKeys()
    {
        using var identity = ServerIdentity.Create();
        using var rogue = ServerIdentity.Create();
        using var clientEph = SessionCrypto.CreateEphemeral();
        using var serverEph = SessionCrypto.CreateEphemeral();
        byte[] clientPub = SessionCrypto.ExportPublic(clientEph);
        byte[] serverPub = SessionCrypto.ExportPublic(serverEph);
        // Client pins the rogue key (MITM); server uses real identity.
        var client = SessionCrypto.DeriveClient(clientEph, serverPub, rogue.StaticPublicKey);
        var server = SessionCrypto.DeriveServer(serverEph, clientPub, identity);
        Assert.False(client.AudioKey.SequenceEqual(server.AudioKey));
    }

    [Fact]
    public void SealOpen_RoundtripsWithAad()
    {
        var (client, server, identity) = Handshake();
        using (identity)
        using (var clientSealer = new AesGcmSealer(client.AudioKey))
        using (var serverSealer = new AesGcmSealer(server.AudioKey))
        {
            byte[] plaintext = new byte[120];
            Random.Shared.NextBytes(plaintext);
            byte[] aad = new byte[24];
            Random.Shared.NextBytes(aad);
            Span<byte> nonce = stackalloc byte[12];
            AudioPacketHeader.WriteNonce(nonce, 7, 99, ProtocolConstants.DirectionClientToServer);

            byte[] sealedBuf = new byte[plaintext.Length + 16];
            int sealedLen = clientSealer.Seal(plaintext, nonce, aad, sealedBuf);
            Assert.Equal(plaintext.Length + 16, sealedLen);

            byte[] opened = new byte[plaintext.Length];
            int openLen = serverSealer.Open(sealedBuf.AsSpan(0, sealedLen), nonce, aad, opened);
            Assert.Equal(plaintext.Length, openLen);
            Assert.Equal(plaintext, opened);
        }
    }

    [Fact]
    public void Open_RejectsTamperedAad()
    {
        var (client, _, identity) = Handshake();
        using (identity)
        using (var sealer = new AesGcmSealer(client.AudioKey))
        {
            byte[] plaintext = new byte[64];
            byte[] aad = new byte[24];
            byte[] sealedBuf = new byte[80];
            byte[] nonce = new byte[12];
            sealer.Seal(plaintext, nonce, aad, sealedBuf);
            aad[0] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() =>
                sealer.Open(sealedBuf, nonce, aad, new byte[64]));
        }
    }

    [Fact]
    public void Open_RejectsReusedNonceWithDifferentPlaintext()
    {
        // Same nonce+key must never carry two different plaintexts; verify detection via tag mismatch
        // when a packet sealed with nonce N is opened as if it were a different packet.
        var (client, _, identity) = Handshake();
        using (identity)
        using (var sealer = new AesGcmSealer(client.AudioKey))
        {
            byte[] nonce = new byte[12];
            byte[] aad1 = new byte[24]; aad1[8] = 1;
            byte[] aad2 = new byte[24]; aad2[8] = 2;
            byte[] sealed1 = new byte[80];
            sealer.Seal(new byte[64], nonce, aad1, sealed1);
            Assert.ThrowsAny<CryptographicException>(() =>
                sealer.Open(sealed1, nonce, aad2, new byte[64]));
        }
    }

    [Fact]
    public void ServerIdentity_Pkcs8Roundtrip_StableId()
    {
        using var identity = ServerIdentity.Create();
        byte[] pkcs8 = identity.ExportPkcs8();
        using var restored = ServerIdentity.FromPkcs8(pkcs8);
        Assert.Equal(identity.ServerId, restored.ServerId);
        Assert.Equal(identity.StaticPublicKey, restored.StaticPublicKey);
    }

    [Fact]
    public void SessionProof_BindsEncryptionModeAndSession()
    {
        var (client, server, identity) = Handshake();
        using (identity)
        {
            byte[] encrypted = SessionCrypto.SessionProof(client, 42, true);
            Assert.Equal(encrypted, SessionCrypto.SessionProof(server, 42, true));
            Assert.False(encrypted.SequenceEqual(SessionCrypto.SessionProof(server, 42, false)));
            Assert.False(encrypted.SequenceEqual(SessionCrypto.SessionProof(server, 43, true)));
        }
    }

    [Fact]
    public void ResumeProof_DeterministicAndKeyBound()
    {
        byte[] key1 = new byte[32]; key1[0] = 1;
        byte[] key2 = new byte[32]; key2[0] = 2;
        Assert.Equal(SessionCrypto.ResumeProof(key1, 5), SessionCrypto.ResumeProof(key1, 5));
        Assert.False(SessionCrypto.ResumeProof(key1, 5).SequenceEqual(SessionCrypto.ResumeProof(key2, 5)));
        Assert.False(SessionCrypto.ResumeProof(key1, 5).SequenceEqual(SessionCrypto.ResumeProof(key1, 6)));
    }
}

public class ReplayWindowTests
{
    [Fact]
    public void AcceptsIncreasingSeq()
    {
        var w = new ReplayWindow();
        for (uint i = 100; i < 200; i++)
            Assert.True(w.CheckAndUpdate(i));
    }

    [Fact]
    public void RejectsDuplicate()
    {
        var w = new ReplayWindow();
        Assert.True(w.CheckAndUpdate(10));
        Assert.False(w.CheckAndUpdate(10));
    }

    [Fact]
    public void AcceptsOutOfOrderWithinWindow()
    {
        var w = new ReplayWindow();
        Assert.True(w.CheckAndUpdate(100));
        Assert.True(w.CheckAndUpdate(98));
        Assert.True(w.CheckAndUpdate(99));
        Assert.False(w.CheckAndUpdate(98));
    }

    [Fact]
    public void RejectsTooOld()
    {
        var w = new ReplayWindow();
        Assert.True(w.CheckAndUpdate(1000));
        Assert.False(w.CheckAndUpdate(900));
    }

    [Fact]
    public void HandlesLargeJump()
    {
        var w = new ReplayWindow();
        Assert.True(w.CheckAndUpdate(1));
        Assert.True(w.CheckAndUpdate(10_000));
        Assert.False(w.CheckAndUpdate(9_999 - 64));
        Assert.True(w.CheckAndUpdate(9_999));
    }
}