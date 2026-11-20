using System.Net;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;

// Desktop mic -> Rasa server loopback client. Reference for the MAUI client and
// a driverless end-to-end demo: rasamicd --monitor on one side, this on the other.
string? serverIp = null;
int port = ProtocolConstants.ControlPort;
string? payloadText = null;
bool tone = false;
int toneSeconds = 0;
bool ns = true;
bool tcpAudio = false;
bool stereo = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length:
            serverIp = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--payload" when i + 1 < args.Length:
            payloadText = args[++i];
            break;
        case "--tone":
            tone = true;
            break;
        case "--seconds" when i + 1 < args.Length:
            toneSeconds = int.Parse(args[++i]);
            break;
        case "--no-ns":
            ns = false;
            break;
        case "--tcp-audio":
            tcpAudio = true;
            break;
        case "--stereo":
            stereo = true;
            break;
    }
}

if (serverIp is null && payloadText is null)
{
    Console.Error.WriteLine("usage: rasamic-loopback --server IP [--port N] | --payload <base64url pairing payload> [--tone [--seconds N]] [--no-ns] [--tcp-audio] [--stereo]");
    return 1;
}

PairingPayload? pinned = payloadText is null ? null : PairingPayload.Decode(payloadText);
if (pinned is not null)
{
    serverIp = pinned.Ips.FirstOrDefault() ?? serverIp;
    port = pinned.Port;
}
if (serverIp is null)
{
    Console.Error.WriteLine("No server address available.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var client = new ClientSession();
int channels = stereo ? 2 : 1;
try
{
    await client.ConnectAsync(new IPEndPoint(IPAddress.Parse(serverIp), port), pinned, Environment.MachineName, cts.Token, tcpAudio,
        features: stereo ? ProtocolConstants.FeatureStereo : 0);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
Console.WriteLine($"Connected to '{client.ServerName}' ({client.ServerId}).");
Console.WriteLine($"Pairing code: {client.PairingCode} — verify it matches the server display.");

using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = ns, Channels = channels });
using var capture = tone ? null : new CaptureDevice(48000, channels, pcm => pipeline.Feed(pcm));
var sender = new Thread(() => client.RunSender(pipeline, cts.Token)) { IsBackground = true, Name = "rasa-sender" };
sender.Start();

Thread? toneFeed = null;
if (tone)
{
    toneFeed = new Thread(() =>
    {
        var pcm = new short[480 * channels];
        int phase = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long chunks = 0;
        while (!cts.IsCancellationRequested && (toneSeconds == 0 || sw.Elapsed.TotalSeconds < toneSeconds))
        {
            for (int i = 0; i < 480; i++)
            {
                pcm[i * channels] = (short)(12000 * Math.Sin(2 * Math.PI * 440 * (phase + i) / 48000.0));
                if (channels == 2)
                    pcm[i * 2 + 1] = (short)(12000 * Math.Sin(2 * Math.PI * 880 * (phase + i) / 48000.0));
            }
            phase += 480;
            pipeline.Feed(pcm);
            chunks++;
            // Sleep(10) rides the 15.6 ms Windows timer grid (~64 pkt/s); pace to the deadline instead.
            long due = (long)((double)chunks * freq * 480 / 48000);
            long now;
            while ((now = sw.ElapsedTicks) < due)
            {
                int msLeft = (int)((due - now) * 1000 / freq);
                if (msLeft > 2) Thread.Sleep(msLeft - 1);
                else Thread.SpinWait(50);
            }
        }
    }) { IsBackground = true, Name = "rasa-tone" };
    toneFeed.Start();
}
else
{
    capture!.Start();
}

var controls = Task.Run(async () =>
{
    try
    {
        while (!cts.IsCancellationRequested)
        {
            ControlMessage? msg = await client.ReadControlAsync(cts.Token);
            switch (msg)
            {
                case null or Bye:
                    return;
                case Ping p:
                    await client.SendControlAsync(new Pong(p.TsUs), cts.Token);
                    break;
                case Stats s:
                    Console.WriteLine($"[stats] {s.Packets} pkt/s · loss {s.LossPct:F1}% · jitter {s.JitterMs:F1} ms · rtt {s.RttMs:F1} ms");
                    break;
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidDataException) { }
}, cts.Token);

Console.WriteLine(tone
    ? (stereo ? "Streaming stereo test tones (L 440 Hz / R 880 Hz)." : "Streaming 440 Hz test tone.")
    : (stereo ? "Streaming stereo microphone. Press Enter to stop." : "Streaming microphone. Press Enter to stop."));
if (tone && toneSeconds > 0)
    toneFeed!.Join();
else
    Console.ReadLine();
cts.Cancel();
capture?.Stop();
try { await controls.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
await client.SendControlAsync(new Bye("user-stop"));
sender.Join(1000);
return 0;