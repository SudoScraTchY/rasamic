using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RasaMic.Audio;
using RasaMic.Protocol;

namespace RasaMic.Client.Core;

/// <summary>
/// Client session: TCP handshake with QR-pinned or TOFU pairing, then UDP audio send.
/// RunSender blocks on a dedicated thread draining the native pipeline.
/// </summary>
public sealed class ClientSession : IDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private Stream? _tunnel;
    private UdpClient? _udp;
    private AesGcmSealer? _sealer;
    private uint _sessionId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _sendBuf = new byte[ProtocolConstants.AudioHeaderSize + 512 + ProtocolConstants.AesGcmTagSize];
    private readonly byte[] _nonce = new byte[ProtocolConstants.AesGcmNonceSize];

    public string ServerId { get; private set; } = "";
    public string ServerName { get; private set; } = "";
    public string PairingCode { get; private set; } = "";
    public string StaticPubBase64Url { get; private set; } = "";
    public IPEndPoint? RemoteEndPoint { get; private set; }
    public bool IsConnected => _tcp?.Connected == true || _tunnel is not null;

    /// <summary>Audio rides the control TCP connection (adb-reverse / USB tunnels that only forward TCP).</summary>
    public bool TcpAudio { get; private set; }
    public bool AudioEncrypted { get; private set; }

    /// <summary>
    /// tunnel: a pre-connected transport (e.g. usbmuxd iOS USB tunnel) used
    /// instead of a direct TCP connect; audio is always framed over it.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint server, PairingPayload? pinned, string clientName, CancellationToken ct = default, bool tcpAudio = false, Stream? tunnel = null, long features = 0)
    {
        TcpAudio = tcpAudio || tunnel is not null;
        if (tunnel is not null)
        {
            _tunnel = tunnel;
            _stream = tunnel;
        }
        else
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(server.Address, server.Port, ct).ConfigureAwait(false);
            _stream = _tcp.GetStream();
        }

        using var eph = SessionCrypto.CreateEphemeral();
        var hello = new Hello(ProtocolConstants.Version, Guid.NewGuid().ToString("N"), clientName,
            SessionCrypto.ExportPublic(eph), features | ProtocolConstants.FeaturePlainAudio);
        await ControlFraming.WriteAsync(_stream, hello, ct).ConfigureAwait(false);

        var reply = await ControlFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (reply is Error err)
            throw new InvalidDataException($"Server refused the connection: {err.Message}");
        if (reply is not HelloAck ack)
            throw new InvalidDataException("Expected HelloAck from server.");
        if (ack.ProtoVer != ProtocolConstants.Version)
            throw new InvalidDataException($"Server protocol version {ack.ProtoVer} unsupported.");

        byte[] staticPub = pinned?.StaticPublicKey ?? ack.StaticPub;
        if (pinned is not null && !ack.StaticPub.AsSpan().SequenceEqual(staticPub))
            throw new InvalidOperationException("Server identity key does not match the pinned QR key.");

        SessionKeys keys = SessionCrypto.DeriveClient(eph, ack.EphPub, staticPub);
        _sessionId = ack.SessionId;
        ServerId = ack.ServerId;
        ServerName = ack.ServerName;
        StaticPubBase64Url = Base64Url.EncodeToString(staticPub);
        RemoteEndPoint = server;
        PairingCode = SessionCrypto.PairingCode(keys);
        await ControlFraming.WriteAsync(_stream, new PairConfirm(PairingCode), ct).ConfigureAwait(false);

        ControlMessage? ready = await ControlFraming.ReadAsync(_stream, ct).ConfigureAwait(false);
        if (ready is Error readyError)
            throw new InvalidDataException($"Server refused the connection: {readyError.Message}");
        byte[] expectedProof = SessionCrypto.SessionProof(keys, ack.SessionId, ack.AudioEncrypted);
        if (ready is not SessionReady sessionReady ||
            sessionReady.SessionId != ack.SessionId ||
            !CryptographicOperations.FixedTimeEquals(sessionReady.Proof, expectedProof))
            throw new InvalidDataException("Server did not authenticate the session settings.");

        AudioEncrypted = ack.AudioEncrypted;
        _sealer = AudioEncrypted ? new AesGcmSealer(keys.AudioKey) : null;
        if (!TcpAudio)
        {
            _udp = new UdpClient();
            _udp.Connect(server);
        }
    }

    public void SendAudio(ReadOnlySpan<byte> opusPayload, uint seq, ulong captureTsUs, AudioFlags flags = AudioFlags.None)
    {
        if (_stream is null || (!TcpAudio && _udp is null) || (AudioEncrypted && _sealer is null))
            throw new InvalidOperationException("Not connected.");
        var header = new AudioPacketHeader
        {
            Magic = ProtocolConstants.AudioMagic,
            Version = ProtocolConstants.Version,
            MsgType = AudioPacketHeader.TypeAudio,
            Flags = flags,
            SessionId = _sessionId,
            Seq = seq,
            CaptureTsUs = captureTsUs,
        };
        AudioPacketHeader.Write(_sendBuf, header);
        int payloadLen = opusPayload.Length;
        if (AudioEncrypted)
        {
            AudioPacketHeader.WriteNonce(_nonce, _sessionId, seq, ProtocolConstants.DirectionClientToServer);
            payloadLen = _sealer!.Seal(
                opusPayload, _nonce, _sendBuf.AsSpan(0, ProtocolConstants.AudioHeaderSize),
                _sendBuf.AsSpan(ProtocolConstants.AudioHeaderSize));
        }
        else
        {
            opusPayload.CopyTo(_sendBuf.AsSpan(ProtocolConstants.AudioHeaderSize));
        }
        var packet = _sendBuf.AsSpan(0, ProtocolConstants.AudioHeaderSize + payloadLen);
        if (TcpAudio)
        {
            _writeLock.Wait();
            try { ControlFraming.WriteRaw(_stream, packet); }
            finally { _writeLock.Release(); }
        }
        else
        {
            _udp!.Send(packet);
        }
    }

    /// <summary>Blocking sender loop; run on a dedicated thread. Exits quietly if the session dies.</summary>
    public void RunSender(AudioPipeline pipeline, CancellationToken ct)
    {
        byte[] packet = new byte[512];
        long frameTicks = System.Diagnostics.Stopwatch.Frequency * pipeline.FrameMs / 1000L;
        long nextSendTs = 0;
        while (!ct.IsCancellationRequested)
        {
            int len = pipeline.ReadPacket(packet, out uint seq, out ulong ts);
            if (len > 0)
            {
                try
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (nextSendTs != 0)
                    {
                        while (now < nextSendTs && !ct.IsCancellationRequested)
                        {
                            if (nextSendTs - now > System.Diagnostics.Stopwatch.Frequency / 500L)
                                Thread.Sleep(1);
                            else
                                Thread.SpinWait(32);
                            now = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                    }
                    if (ct.IsCancellationRequested)
                        return;
                    nextSendTs = nextSendTs == 0 ? now + frameTicks : nextSendTs + frameTicks;
                    if (now - nextSendTs > frameTicks * 2)
                        nextSendTs = now + frameTicks;
                    SendAudio(packet.AsSpan(0, len), seq, ts, AudioFlags.Fec);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    public async Task SendControlAsync(ControlMessage message, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ControlFraming.WriteAsync(stream, message, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public ValueTask<ControlMessage?> ReadControlAsync(CancellationToken ct = default) =>
        ControlFraming.ReadAsync(_stream ?? throw new InvalidOperationException("Not connected."), ct);

    public void Dispose()
    {
        _sealer?.Dispose();
        _udp?.Dispose();
        _tcp?.Dispose();
        _tunnel?.Dispose();
        _writeLock.Dispose();
        _sealer = null;
        _udp = null;
        _tcp = null;
        _tunnel = null;
        _stream = null;
    }
}