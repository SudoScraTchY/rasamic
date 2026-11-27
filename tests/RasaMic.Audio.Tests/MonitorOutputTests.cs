using RasaMic.Audio;
using Xunit;

namespace RasaMic.Audio.Tests;

public class MonitorOutputTests
{
    [Fact]
    public void DefaultConstructor_OpensWindowsPlaybackDevice()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!AudioDeviceEnumerator.EnumeratePlayback().Any())
            return;

        using var output = new MonitorOutput(48000, 1);
        float[] silence = new float[480];
        Assert.Equal(silence.Length, output.Write(silence));
        MonitorStats stats = output.GetStats();
        Assert.InRange(stats.CapacitySamples, 1, 2400);
        Assert.InRange(stats.BufferedMs, 0, 50);
    }

    [Fact]
    public void ExactEndpointId_OpensKnownAndRejectsUnknownWindowsPlaybackDevice()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var device = AudioDeviceEnumerator.EnumeratePlayback()
            .FirstOrDefault(d => !string.IsNullOrEmpty(d.Id));
        if (device is null)
            return;

        using var output = new MonitorOutput(48000, 1, device);
        float[] silence = new float[480];
        Assert.Equal(silence.Length, output.Write(silence));

        var missing = new AudioDevice("{rasamic-invalid-endpoint-id}", device.Name, false);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var missingOutput = new MonitorOutput(48000, 1, missing);
        });
    }
}
