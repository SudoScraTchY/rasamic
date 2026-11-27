using RasaMic.Audio;
using RasaMic.Server.Backends;
using Xunit;

namespace RasaMic.IntegrationTests;

public class ExternalCableBackendTests
{
    [Fact]
    public void Defaults_ToCableInputAndStereo()
    {
        using var backend = new ExternalCableBackend();
        Assert.Equal("External cable (CABLE Input)", backend.Name);
        Assert.True(backend.StereoCapable);
    }

    [Fact]
    public void Constructor_RejectsEmptyEndpointName()
    {
        Assert.Throws<ArgumentException>(() => new ExternalCableBackend(""));
        Assert.Throws<ArgumentException>(() => new ExternalCableBackend(new AudioDevice("id", "", false)));
    }

    [Fact]
    public void Constructor_UsesDeviceNameAndId()
    {
        var device = new AudioDevice("{endpoint-id}", "CABLE Input", false);
        using var backend = new ExternalCableBackend(device);
        Assert.Equal("External cable (CABLE Input)", backend.Name);
    }

    [Fact]
    public void WriteBeforeStart_AndRepeatedStop_AreSafe()
    {
        using var backend = new ExternalCableBackend();
        backend.Write([0.25f, -0.25f]);
        backend.Stop();
        backend.Stop();
        backend.Dispose();
    }

    [Fact]
    public void Validate_MissingEndpoint_HasActionableError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var backend = new ExternalCableBackend("RasaMic-Test-Missing-Endpoint");
        var error = Assert.Throws<InvalidOperationException>(() => backend.Validate());
        Assert.Contains("https://vb-audio.com/Cable/", error.Message);
        Assert.Contains("CABLE Output", error.Message);
    }

    [Fact]
    public void Start_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var backend = new ExternalCableBackend();
        Assert.Throws<PlatformNotSupportedException>(() => backend.Start(48000, 1));
    }
}
