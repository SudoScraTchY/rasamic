using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using Xunit;

namespace RasaMic.IntegrationTests;

public class SenderPacingTests
{
    [Fact]
    public async Task RunSender_PacesQueuedPackets()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var identity = ServerIdentity.Create();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task handshake = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(lifetime.Token);
            var stream = tcp.GetStream();
            var hello = Assert.IsType<Hello>(await ControlFraming.ReadAsync(stream, lifetime.Token));
            using var eph = SessionCrypto.CreateEphemeral();
            SessionKeys keys = SessionCrypto.DeriveServer(eph, hello.EphPub, identity);
            await ControlFraming.WriteAsync(stream, new HelloAck(
                "test-server", "Test", SessionCrypto.ExportPublic(eph), identity.StaticPublicKey,
                42, ProtocolConstants.Version, false), lifetime.Token);
            Assert.IsType<PairConfirm>(await ControlFraming.ReadAsync(stream, lifetime.Token));
            await ControlFraming.WriteAsync(stream, new SessionReady(
                42, SessionCrypto.SessionProof(keys, 42, false)), lifetime.Token);
            await Task.Delay(Timeout.Infinite, lifetime.Token);
        }, lifetime.Token);

        try
        {
            using var client = new ClientSession();
            await client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, port), null, "pace-test",
                lifetime.Token);
            Assert.False(client.AudioEncrypted);

            using var pipeline = new AudioPipeline(new PipelineOptions
            {
                NoiseSuppression = false,
                Agc = false,
            });
            short[] frame = new short[480];
            for (int i = 0; i < 20; i++)
            {
                for (int j = 0; j < frame.Length; j++)
                    frame[j] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i * frame.Length + j) / 48000.0));
                pipeline.Feed(frame);
            }

            using var senderCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var sender = new Thread(() => client.RunSender(pipeline, senderCts.Token))
            {
                IsBackground = true,
                Name = "sender-pacing-test",
            };
            sender.Start();

            var timestamps = new long[20];
            for (int i = 0; i < timestamps.Length; i++)
            {
                UdpReceiveResult packet = await udp.ReceiveAsync(lifetime.Token);
                Assert.True(packet.Buffer.Length >= ProtocolConstants.AudioHeaderSize);
                timestamps[i] = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            double spanMs = (timestamps[^1] - timestamps[0]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double maxGapMs = 0;
            for (int i = 1; i < timestamps.Length; i++)
                maxGapMs = Math.Max(maxGapMs,
                    (timestamps[i] - timestamps[i - 1]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Assert.True(spanMs >= 150, $"20 queued packets were sent in {spanMs:F0} ms");
            Assert.True(maxGapMs < 250, $"largest sender gap was {maxGapMs:F0} ms");

            senderCts.Cancel();
            Assert.True(sender.Join(2000));
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            try { await handshake; } catch (OperationCanceledException) { }
        }
    }
}
