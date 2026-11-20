using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;

// Pipeline latency probe (plan: RasaMic.LatencyProbe, mode a).
// Runs a real in-process server (UDP + AES-GCM + jitter buffer + Opus decode)
// and feeds it paced 10 ms PCM with a tone burst every 500 ms. Latency =
// time from feeding the burst to the backend writing decoded audio above
// threshold. This covers DSP/encode + network + crypto + jitter + decode;
// capture and virtual-mic playback add their own platform latency on top.
// Acoustic end-to-end (mode b) needs the phone + speakers; see README.

int bursts = args.Length > 0 && int.TryParse(args[0], out int n) ? n : 20;
double gateMs = args.Length > 1 && double.TryParse(args[1], out double g) ? g : 80.0;
string? wavPath = args.Length > 2 ? args[2] : null;

var detections = new ConcurrentQueue<(long Ts, long Sample)>();
using var backend = new DetectBackend(detections, wavPath is not null);
using var identity = ServerIdentity.Create();
using var server = new RasaMicServer(identity, backend, port: 0, serverName: "latency-probe");
using var serverCts = new CancellationTokenSource();
Task serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
while (!server.IsListening) await Task.Delay(10);

using var cts = new CancellationTokenSource();
using var client = new ClientSession();
await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), null, "latency-probe", cts.Token);

using var pipeline = new AudioPipeline(new PipelineOptions
{
    NoiseSuppression = false, // keep the burst intact for detection
    Agc = false,
    Dtx = false,
});
var sender = new Thread(() => client.RunSender(pipeline, cts.Token)) { IsBackground = true, Name = "probe-sender" };
sender.Start();

const int rate = 48000;
const int chunk = 480;               // 10 ms
const int burstEveryChunks = 50;     // 500 ms
const int burstChunks = 2;           // 20 ms tone burst
var inputTimes = new List<long>();
long freq = Stopwatch.Frequency;
var chunkPcm = new short[chunk];

Console.WriteLine($"Probing {bursts} bursts over localhost (gate {gateMs:F0} ms)...");
long memBefore = GC.GetTotalMemory(true);
long start = Stopwatch.GetTimestamp();
int totalChunks = bursts * burstEveryChunks + 20;
for (int i = 0; i < totalChunks; i++)
{
    int burstIndex = i / burstEveryChunks;
    int within = i % burstEveryChunks;
    bool inBurst = burstIndex < bursts && within < burstChunks;
    if (inBurst)
    {
        for (int s = 0; s < chunk; s++)
            chunkPcm[s] = (short)(20000 * Math.Sin(2 * Math.PI * 880 * s / rate));
        if (within == 0) inputTimes.Add(Stopwatch.GetTimestamp());
    }
    else
    {
        Array.Clear(chunkPcm);
    }
    pipeline.Feed(chunkPcm);

    // Pace to real time so the jitter buffer sees production cadence.
    long due = start + (long)((i + 1) * (double)freq * chunk / rate);
    long now;
    while ((now = Stopwatch.GetTimestamp()) < due)
    {
        int msLeft = (int)((due - now) * 1000 / freq);
        if (msLeft > 2) Thread.Sleep(msLeft - 1);
        else Thread.SpinWait(50);
    }
}

Thread.Sleep(300); // let the tail drain
cts.Cancel();
try { await client.SendControlAsync(new Bye("probe-done")); } catch { }
sender.Join(1000);
serverCts.Cancel();
try { await serverTask; } catch { }

// Match each detected output burst to the latest input burst at or before it.
var latencies = new List<double>();
foreach ((long tOut, _) in detections)
{
    long tIn = inputTimes.Where(t => t <= tOut).DefaultIfEmpty(0).Max();
    if (tIn > 0) latencies.Add((tOut - tIn) * 1000.0 / freq);
}

if (latencies.Count == 0)
{
    Console.Error.WriteLine("FAIL: no bursts detected at the backend.");
    return 1;
}

latencies.Sort();
double median = latencies[latencies.Count / 2];
double p95 = latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * 0.95))];
double max = latencies[^1];
Console.WriteLine($"Detected {latencies.Count}/{bursts} bursts.");
Console.WriteLine($"Feed->backend latency: median {median:F1} ms, p95 {p95:F1} ms, max {max:F1} ms");
Console.WriteLine($"(jitter target 20 ms + 10 ms frame + codec/network; capture + playback add platform latency)");
if (wavPath is not null && backend.Pcm.Count > 0)
{
    float[] wav = backend.Pcm.ToArray();
    int onset = -1;
    for (int i = 0; i + 16 < wav.Length; i++)
    {
        bool ok = true;
        for (int k = 0; k < 16; k++)
            if (Math.Abs(wav[i + k]) < 0.2f) { ok = false; break; }
        if (ok) { onset = i; break; }
    }
    if (onset >= 0)
    {
        // Template correlation ±20 ms around the energy onset; the per-chunk phase
        // reset in the fed burst makes the full-overlap offset the unique peak.
        var refBurst = new float[960];
        for (int c = 0; c < 2; c++)
            for (int s = 0; s < 480; s++)
                refBurst[c * 480 + s] = MathF.Sin(2f * MathF.PI * 880 * s / 48000);
        int best = onset;
        double bestScore = double.NegativeInfinity;
        for (int off = Math.Max(0, onset - 960); off <= Math.Min(wav.Length - 960, onset + 960); off++)
        {
            double score = 0;
            for (int k = 0; k < 960; k++) score += wav[off + k] * refBurst[k];
            if (score > bestScore) { bestScore = score; best = off; }
        }
        double feedOffsetMs = (inputTimes[0] - start) * 1000.0 / freq;
        // Anchor the sample clock to the wall time of the write that contained
        // the first onset; the sample domain then adds sub-chunk precision.
        (long detTs, long detSample) = detections.TryPeek(out var d0) ? d0 : (0, 0);
        double anchorMs = (detTs - start) * 1000.0 / freq;
        double wavLatencyMs = anchorMs + (best - detSample) / 48.0 - feedOffsetMs;
        Console.WriteLine($"WAV sample-accurate feed->write latency: {wavLatencyMs:F2} ms (energy onset {anchorMs + (onset - detSample) / 48.0 - feedOffsetMs:F2} ms)");
    }
    WriteWav(wavPath, wav);
    Console.WriteLine($"WAV written: {wavPath}");
}
long memAfter = GC.GetTotalMemory(true);
Console.WriteLine($"Managed heap delta: {(memAfter - memBefore) / 1024.0:F0} KiB over {(Stopwatch.GetTimestamp() - start) * 1000.0 / freq:F0} ms");
if (p95 > gateMs)
{
    Console.Error.WriteLine($"FAIL: p95 {p95:F1} ms exceeds {gateMs:F0} ms gate.");
    return 1;
}
Console.WriteLine("PASS");
return 0;

static void WriteWav(string path, float[] pcm)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    int dataLen = pcm.Length * 2;
    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    w.Write(36 + dataLen);
    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
    w.Write(16);
    w.Write((short)1);
    w.Write((short)1);
    w.Write(48000);
    w.Write(48000 * 2);
    w.Write((short)2);
    w.Write((short)16);
    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    w.Write(dataLen);
    foreach (float v in pcm)
        w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
}

sealed class DetectBackend(ConcurrentQueue<(long Ts, long Sample)> detections, bool record) : IVirtualMicBackend
{
    private bool _inBurst;
    private long _samples;
    public List<float> Pcm { get; } = new();
    public string Name => "latency-detector";
    public void Start(int sampleRate, int channels) { }
    public void Write(ReadOnlySpan<float> pcm)
    {
        if (record)
            foreach (float v in pcm) Pcm.Add(v);
        float peak = 0;
        foreach (float v in pcm) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 0.2f && !_inBurst)
        {
            _inBurst = true;
            detections.Enqueue((Stopwatch.GetTimestamp(), _samples));
        }
        else if (peak < 0.05f)
        {
            _inBurst = false;
        }
        _samples += pcm.Length;
    }
    public void Stop() { }
    public void Dispose() { }
}