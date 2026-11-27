using RasaMic.Server;
using RasaMic.Server.Backends;
using Xunit;

namespace RasaMic.Server.App.Tests;

public class SelfMonitoringBackendTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StereoCapable_DelegatesToPrimary(bool stereoCapable)
    {
        using var primary = new RecordingBackend { StereoCapable = stereoCapable };
        using var backend = new SelfMonitoringBackend(primary, () => new RecordingBackend());

        Assert.Equal(stereoCapable, backend.StereoCapable);
    }

    [Fact]
    public void StartWriteStop_UsesPrimaryAndMonitor()
    {
        using var primary = new RecordingBackend { StereoCapable = true };
        using var monitor = new RecordingBackend();
        using var backend = new SelfMonitoringBackend(primary, () => monitor);
        float[] pcm = [0.25f, -0.5f, 0.75f];

        backend.Start(48000, 2);
        backend.Write(pcm);
        backend.Stop();

        Assert.Equal(1, primary.Starts);
        Assert.Equal(1, monitor.Starts);
        Assert.Equal(pcm, Assert.Single(primary.Writes));
        Assert.Equal(pcm, Assert.Single(monitor.Writes));
        Assert.Equal(1, primary.Stops);
        Assert.Equal(1, monitor.Stops);
    }

    [Fact]
    public void Start_PrimaryFailure_CleansUpPrimary()
    {
        var failure = new InvalidOperationException("primary failed");
        using var primary = new RecordingBackend { StartFailure = failure };
        bool monitorCreated = false;
        using var backend = new SelfMonitoringBackend(primary, () =>
        {
            monitorCreated = true;
            return new RecordingBackend();
        });

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.Start(48000, 1));

        Assert.Same(failure, thrown);
        Assert.Equal(1, primary.Stops);
        Assert.False(monitorCreated);
    }

    [Fact]
    public void Start_MonitorFactoryFailure_CleansUpPrimary()
    {
        var failure = new InvalidOperationException("monitor factory failed");
        using var primary = new RecordingBackend();
        using var backend = new SelfMonitoringBackend(primary, () => throw failure);

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.Start(48000, 1));

        Assert.Same(failure, thrown);
        Assert.Equal(1, primary.Starts);
        Assert.Equal(1, primary.Stops);
    }

    [Fact]
    public void Start_MonitorFailure_CleansUpBothAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("monitor failed");
        using var primary = new RecordingBackend { StopFailure = new IOException("primary cleanup failed") };
        using var monitor = new RecordingBackend
        {
            StartFailure = failure,
            StopFailure = new IOException("monitor cleanup failed"),
        };
        using var backend = new SelfMonitoringBackend(primary, () => monitor);

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.Start(48000, 1));

        Assert.Same(failure, thrown);
        Assert.Equal(1, primary.Stops);
        Assert.Equal(1, monitor.Stops);
    }

    [Fact]
    public void Stop_StillStopsPrimaryWhenMonitorStopFails()
    {
        using var primary = new RecordingBackend();
        using var monitor = new RecordingBackend { StopFailure = new IOException("monitor stop failed") };
        using var backend = new SelfMonitoringBackend(primary, () => monitor);
        backend.Start(48000, 1);

        Assert.Throws<IOException>(() => backend.Stop());

        Assert.Equal(1, monitor.Stops);
        Assert.Equal(1, primary.Stops);
        Assert.Equal(1, monitor.Disposes);
    }

    [Fact]
    public void Dispose_DisposesBothBackends()
    {
        using var primary = new RecordingBackend();
        using var monitor = new RecordingBackend();
        var backend = new SelfMonitoringBackend(primary, () => monitor);
        backend.Start(48000, 1);

        backend.Dispose();

        Assert.Equal(1, primary.Disposes);
        Assert.Equal(1, monitor.Disposes);
    }

    private sealed class RecordingBackend : IVirtualMicBackend
    {
        public string Name => "recording";
        public bool StereoCapable { get; init; }
        public Exception? StartFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public List<float[]> Writes { get; } = [];
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Disposes { get; private set; }

        public void Start(int sampleRate, int channels)
        {
            Starts++;
            if (StartFailure is not null)
                throw StartFailure;
        }

        public void Write(ReadOnlySpan<float> pcm) => Writes.Add(pcm.ToArray());

        public void Stop()
        {
            Stops++;
            if (StopFailure is not null)
                throw StopFailure;
        }

        public void Dispose() => Disposes++;
    }
}
