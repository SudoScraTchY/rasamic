using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RasaMic.Audio;
using RasaMic.Core.Jitter;
using RasaMic.Protocol;

namespace RasaMic.Server;

/// <summary>
/// Rasa server engine: TCP control handshake (ECDH + pairing code), UDP audio receive,
/// optional AES-GCM open, paced jitter playout, Opus decode with PLC, virtual-mic backend write.
/// v2 supports one active audio session; extra clients receive Error("Busy").
/// </summary>
public sealed class RasaMicServer : IDisposable
{
    private readonly ServerIdentity _identity;
    private readonly IVirtualMicBackend _backend;
    private readonly int _port;
    private readonly string _serverName;
    private readonly bool _audioEncrypted;
    private int _activeSession;
    private int _localMuted;
    private float _localGainDb;

    public RasaMicServer(
        ServerIdentity identity, IVirtualMicBackend backend,
        int port = ProtocolConstants.ControlPort, string? serverName = null,
        bool audioEncrypted = true)
    {
        _identity = identity;
        _backend = backend;
        _port = port;
        _serverName = serverName ?? Environment.MachineName;
        _audioEncrypted = audioEncrypted;
    }

    public int Port { get; private set; }
    public bool IsListening { get; private set; }
    public bool HasActiveSession => Volatile.Read(ref _activeSession) != 0;

    /// <summary>Local (GUI/hotkey) mute, independent of the client's SetMute.</summary>
    public bool LocalMuted
    {
        get => Volatile.Read(ref _localMuted) != 0;
        set => Volatile.Write(ref _localMuted, value ? 1 : 0);
    }

    /// <summary>Local (GUI) post-gain in dB, applied on top of the client's SetGain.</summary>
    public float LocalGainDb
    {
        get => Volatile.Read(ref _localGainDb);
        set => Volatile.Write(ref _localGainDb, Math.Clamp(value, -24f, 24f));
    }

    /// <summary>Most recent 1 Hz telemetry of the active session (null when idle).</summary>
    public Stats? LastSessionStats { get; private set; }
    public int LastSessionChannels { get; private set; }
    public string? LastClientName { get; private set; }
    public int SessionCount { get; private set; }

    public PairingPayload PairingPayload =>
        PairingPayload.FromIdentity(_identity, _serverName, GetLocalIPv4(), Port == 0 ? _port : Port);

    public string PairingText => PairingPayload.Encode(PairingPayload);

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsListening = true;
        var sessions = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                sessions.Add(Task.Run(() => HandleClientAsync(tcp, ct), CancellationToken.None));
                sessions.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        finally
        {
            IsListening = false;
            listener.Stop();
            try { await Task.WhenAll(sessions).ConfigureAwait(false); } catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using var tcpLease = tcp;
        tcp.NoDelay = true;
        NetworkStream stream = tcp.GetStream();
        try
        {
            // Validate Hello before claiming the session slot: bare TCP probes
            // (reconnect ladder, port scanners) must never evict a real client.
            if (await ControlFraming.ReadAsync(stream, ct).ConfigureAwait(false) is not Hello hello ||
                hello.ProtoVer != ProtocolConstants.Version)
            {
                await ControlFraming.WriteAsync(stream, new Error("Unsupported protocol version", "upgrade"), ct).ConfigureAwait(false);
                return;
            }

            if (!_audioEncrypted && (hello.Features & ProtocolConstants.FeaturePlainAudio) == 0)
            {
                await ControlFraming.WriteAsync(stream, new Error(
                    "This server requires a client that supports plain audio. Update Rasa."), ct).ConfigureAwait(false);
                return;
            }

            if (Interlocked.CompareExchange(ref _activeSession, 1, 0) != 0)
            {
                await ControlFraming.WriteAsync(stream, new Error("Busy: another device is connected"), ct).ConfigureAwait(false);
                return;
            }

            try
            {
                using var serverEph = SessionCrypto.CreateEphemeral();
                uint sessionId = (uint)Random.Shared.Next(1, int.MaxValue);
                var ack = new HelloAck(
                    _identity.ServerId, _serverName,
                    SessionCrypto.ExportPublic(serverEph), _identity.StaticPublicKey,
                    sessionId, ProtocolConstants.Version, _audioEncrypted);
                await ControlFraming.WriteAsync(stream, ack, ct).ConfigureAwait(false);

                SessionKeys keys = SessionCrypto.DeriveServer(serverEph, hello.EphPub, _identity);
                if (await ControlFraming.ReadAsync(stream, ct).ConfigureAwait(false) is not PairConfirm confirm ||
                    confirm.Code != SessionCrypto.PairingCode(keys))
                {
                    await ControlFraming.WriteAsync(stream, new Error("Pairing code mismatch"), ct).ConfigureAwait(false);
                    return;
                }
                await ControlFraming.WriteAsync(stream, new SessionReady(
                    sessionId, SessionCrypto.SessionProof(keys, sessionId, _audioEncrypted)), ct).ConfigureAwait(false);
                LastClientName = hello.ClientName;
                LastSessionChannels = (hello.Features & ProtocolConstants.FeatureStereo) != 0 ? 2 : 1;
                SessionCount++;

                var controls = new ConcurrentQueue<ControlMessage>();
                var audioFrames = new ConcurrentQueue<byte[]>();
                Task reader = Task.Run(async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            // Length-prefixed frames are control JSON or raw audio (TCP audio
                            // transport for adb-reverse/USB tunnels); demux by the audio magic.
                            byte[]? frame = await ControlFraming.ReadRawAsync(stream, ct).ConfigureAwait(false);
                            if (frame is null)
                                break;
                            int audioOverhead = _audioEncrypted ? ProtocolConstants.AesGcmTagSize : 0;
                            if (frame.Length >= ProtocolConstants.AudioHeaderSize + audioOverhead &&
                                frame[0] == ProtocolConstants.AudioMagic)
                            {
                                while (audioFrames.Count >= 8)
                                    audioFrames.TryDequeue(out _);
                                audioFrames.Enqueue(frame);
                                continue;
                            }
                            ControlMessage? msg = System.Text.Json.JsonSerializer.Deserialize(
                                frame, ProtocolJsonContext.Default.ControlMessage);
                            if (msg is null)
                                break;
                            controls.Enqueue(msg);
                        }
                    }
                    catch (Exception) { /* client disconnected */ }
                }, CancellationToken.None);

                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                int channels = LastSessionChannels;
                await Task.Run(() => AudioLoop(udp, stream, keys, sessionId, controls, audioFrames, reader, channels, _audioEncrypted, ct), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _activeSession, 0);
            }
        }
        catch (Exception) { /* connection dropped mid-handshake */ }
    }

    private void AudioLoop(
        UdpClient udp, NetworkStream stream, SessionKeys keys, uint sessionId,
        ConcurrentQueue<ControlMessage> controls, ConcurrentQueue<byte[]> audioFrames,
        Task controlReader, int channels, bool audioEncrypted, CancellationToken ct)
    {
        using var sealer = audioEncrypted ? new AesGcmSealer(keys.AudioKey) : null;
        using var decoder = new OpusDecoder(48000, channels);
        var jitter = new JitterBuffer();
        var replay = new ReplayWindow();
        int backendChannels = _backend.StereoCapable ? channels : 1;
        _backend.Start(48000, backendChannels);

        byte[] recv = new byte[2048];
        byte[] plain = new byte[1024];
        float[] pcm = new float[960 * channels];
        float[]? downmix = channels == 2 && backendChannels == 1 ? new float[960] : null;
        byte[] nonce = new byte[ProtocolConstants.AesGcmNonceSize];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        bool muted = false;
        bool done = false;
        float linearGain = 1f;
        udp.Client.Blocking = false;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long statsPkts = 0, statsBytes = 0, statsGaps = 0;
        uint lastSeq = 0;
        bool haveSeq = false;
        double lastArrivalMs = 0, jitterAccum = 0;
        long jitterSamples = 0;
        long nextStatsMs = 1000, nextPingMs = 500;
        long pingSentTs = 0;
        float rttMs = 0;
        long freq = System.Diagnostics.Stopwatch.Frequency;
        var decodeSamples = new double[512]; int decodeN = 0;
        var dwellSamples = new double[512]; int dwellN = 0;
        var writeSamples = new double[512]; int writeN = 0;
        long lastWriteTs = 0;
        long nextPlayoutTs = 0;
        int playoutSamples = 480;
        static double P95(double[] a, int n)
        {
            if (n == 0) return 0;
            var c = new double[n];
            Array.Copy(a, c, n);
            Array.Sort(c);
            return c[Math.Min(n - 1, (int)(n * 0.95))];
        }

        // Telemetry must never stall the audio loop: a client that stops reading
        // controls fills its TCP buffer, so skip sends while one is still pending.
        Task? pendingCtrl = null;
        bool TrySend(ControlMessage m)
        {
            if (pendingCtrl is not null)
            {
                if (!pendingCtrl.IsCompleted)
                    return false;
                if (pendingCtrl.IsFaulted || pendingCtrl.IsCanceled)
                {
                    done = true;
                    return false;
                }
            }
            pendingCtrl = ControlFraming.WriteAsync(stream, m, ct);
            return true;
        }

        void Ingest(byte[] buf, int n)
        {
            int tagSize = audioEncrypted ? ProtocolConstants.AesGcmTagSize : 0;
            if (n < ProtocolConstants.AudioHeaderSize + tagSize ||
                n - ProtocolConstants.AudioHeaderSize > plain.Length)
                return;
            AudioPacketHeader h = AudioPacketHeader.Read(buf);
            if (h.Magic != ProtocolConstants.AudioMagic ||
                h.Version != ProtocolConstants.Version ||
                h.MsgType != AudioPacketHeader.TypeAudio ||
                h.SessionId != sessionId ||
                !replay.CheckAndUpdate(h.Seq))
                return;
            try
            {
                int len;
                if (audioEncrypted)
                {
                    AudioPacketHeader.WriteNonce(nonce, h.SessionId, h.Seq, ProtocolConstants.DirectionClientToServer);
                    len = sealer!.Open(
                        buf.AsSpan(ProtocolConstants.AudioHeaderSize, n - ProtocolConstants.AudioHeaderSize),
                        nonce,
                        buf.AsSpan(0, ProtocolConstants.AudioHeaderSize),
                        plain);
                }
                else
                {
                    len = n - ProtocolConstants.AudioHeaderSize;
                    buf.AsSpan(ProtocolConstants.AudioHeaderSize, len).CopyTo(plain);
                }
                jitter.Push(h.Seq, plain.AsSpan(0, len));
                statsPkts++;
                statsBytes += n;
                double nowMs = clock.Elapsed.TotalMilliseconds;
                if (haveSeq)
                {
                    if (h.Seq > lastSeq + 1)
                        statsGaps += h.Seq - lastSeq - 1;
                    jitterAccum += Math.Abs(nowMs - lastArrivalMs - 10.0);
                    jitterSamples++;
                }
                haveSeq = true;
                lastSeq = h.Seq;
                lastArrivalMs = nowMs;
            }
            catch (CryptographicException) { /* forged or corrupted packet */ }
        }

        try
        {
            while (!ct.IsCancellationRequested && !done)
            {
                while (controls.TryDequeue(out ControlMessage? msg))
                {
                    switch (msg)
                    {
                        case SetMute m:
                            muted = m.Muted;
                            break;
                        case SetGain g:
                            linearGain = MathF.Pow(10f, Math.Clamp(g.Db, -24f, 24f) / 20f);
                            break;
                        case Ping p:
                            TrySend(new Pong(p.TsUs));
                            break;
                        case Pong:
                            if (pingSentTs != 0)
                            {
                                rttMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pingSentTs) * 1000f / freq;
                                pingSentTs = 0;
                            }
                            break;
                        case Bye:
                            done = true;
                            break;
                    }
                }
                if (controlReader.IsCompleted && controls.IsEmpty && audioFrames.IsEmpty)
                    done = true;

                for (int i = 0; i < 8 && audioFrames.TryDequeue(out byte[]? frame); i++)
                    Ingest(frame, frame.Length);

                bool receivedAudio = false;
                for (int i = 0; i < 128; i++)
                {
                    int n;
                    try
                    {
                        n = udp.Client.ReceiveFrom(recv, ref remote);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.TimedOut)
                    {
                        break;
                    }
                    if (n <= 0)
                        break;
                    Ingest(recv, n);
                    receivedAudio = true;
                }

                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (nextPlayoutTs == 0 || nowTs >= nextPlayoutTs)
                {
                    JitterResult r = jitter.Next(out _, out byte[] payload, out int plen);
                    if (r == JitterResult.Empty)
                    {
                        nextPlayoutTs = 0;
                    }
                    else
                    {
                        if (nextPlayoutTs == 0)
                            nextPlayoutTs = nowTs;
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        int samples = r == JitterResult.Packet
                            ? decoder.Decode(payload.AsSpan(0, plen), pcm)
                            : decoder.Plc(pcm.AsSpan(0, playoutSamples * channels));
                        if (samples <= 0)
                        {
                            samples = decoder.Plc(pcm.AsSpan(0, playoutSamples * channels));
                            if (samples <= 0)
                            {
                                Array.Clear(pcm, 0, playoutSamples * channels);
                                samples = playoutSamples;
                            }
                        }
                        playoutSamples = samples;
                        if (decodeN < decodeSamples.Length)
                            decodeSamples[decodeN++] = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
                        if (r == JitterResult.Packet && dwellN < dwellSamples.Length)
                            dwellSamples[dwellN++] = jitter.LastDwellMs;
                        long wTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (lastWriteTs != 0 && writeN < writeSamples.Length)
                            writeSamples[writeN++] = (wTs - lastWriteTs) * 1000.0 / freq;
                        lastWriteTs = wTs;
                        int values = samples * channels;
                        if (linearGain != 1f)
                            PcmPostGain.Apply(pcm.AsSpan(0, values), linearGain);
                        float localDb = LocalGainDb;
                        if (localDb != 0f)
                            PcmPostGain.Apply(pcm.AsSpan(0, values), MathF.Pow(10f, localDb / 20f));
                        if (muted || LocalMuted)
                            Array.Clear(pcm, 0, values);
                        if (downmix is not null)
                        {
                            for (int i = 0; i < samples; i++)
                                downmix[i] = (pcm[i * 2] + pcm[i * 2 + 1]) * 0.5f;
                            _backend.Write(downmix.AsSpan(0, samples));
                        }
                        else
                        {
                            _backend.Write(pcm.AsSpan(0, values));
                        }
                        nextPlayoutTs += samples * freq / 48000L;
                        long afterWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (nextPlayoutTs <= afterWriteTs)
                            nextPlayoutTs = afterWriteTs + samples * freq / 48000L;
                    }
                }
                else if (nextPlayoutTs != 0)
                {
                    // Thread.Sleep(1) rounds to about 16 ms on Windows and starves 10 ms frames.
                    while (System.Diagnostics.Stopwatch.GetTimestamp() < nextPlayoutTs)
                    {
                        if (nextPlayoutTs - System.Diagnostics.Stopwatch.GetTimestamp() > freq / 1000L)
                            Thread.Sleep(0);
                        else
                            Thread.SpinWait(32);
                    }
                }
                else if (!receivedAudio)
                {
                    Thread.Sleep(1);
                }

                long elapsedMs = clock.ElapsedMilliseconds;
                if (elapsedMs >= nextStatsMs)
                {
                    nextStatsMs = elapsedMs + 1000;
                    float lossPct = statsPkts + statsGaps > 0 ? 100f * statsGaps / (statsPkts + statsGaps) : 0f;
                    float jitterMs = jitterSamples > 0 ? (float)(jitterAccum / jitterSamples) : 0f;
                    var stats = new Stats(rttMs, lossPct, jitterMs, jitter.Depth * 10f, statsPkts, statsBytes,
                        (float)P95(decodeSamples, decodeN), (float)P95(dwellSamples, dwellN), (float)P95(writeSamples, writeN),
                        _backend is IAudioOutputDiagnostics output ? (float)output.BufferedMs : 0,
                        _backend is IAudioOutputDiagnostics diagnostics ? diagnostics.DroppedSamples : 0,
                        _backend is IAudioOutputDiagnostics device ? (float)device.DeviceBufferMs : 0);
                    LastSessionStats = stats;
                    TrySend(stats);
                    statsPkts = 0;
                    statsBytes = 0;
                    statsGaps = 0;
                    jitterAccum = 0;
                    jitterSamples = 0;
                    decodeN = 0;
                    dwellN = 0;
                    writeN = 0;
                }
                if (elapsedMs >= nextPingMs && pingSentTs == 0)
                {
                    nextPingMs = elapsedMs + 1000;
                    long ts = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (TrySend(new Ping(ts)))
                        pingSentTs = ts;
                }
            }
        }
        finally
        {
            _backend.Stop();
        }
    }

    private static string[] GetLocalIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToArray();
        }
        catch (SocketException)
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose() => _backend.Dispose();
}