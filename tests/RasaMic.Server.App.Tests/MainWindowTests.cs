using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RasaMic.Server.App.ViewModels;
using Xunit;

namespace RasaMic.Server.App.Tests;

public class MainWindowTests
{
    internal sealed class NullBackend : IVirtualMicBackend
    {
        public string Name => "null";
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) { }
        public void Stop() { }
        public void Dispose() { }
    }

    [AvaloniaFact]
    public void MainWindow_LoadsXaml()
    {
        var window = new MainWindow();
        Assert.NotNull(window.Content);
        Assert.IsType<MainWindowViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public async Task ViewModel_StartStop_RealEngine()
    {
        using var vm = new MainWindowViewModel(() => new NullBackend());
        vm.Port = 0; // ephemeral
        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.StartsWith("Listening on TCP/UDP ", vm.Status);
        Assert.NotEmpty(vm.LocalIps);
        Assert.NotEmpty(vm.PairingText);
        Assert.NotNull(vm.QrPng);
        Assert.True(vm.QrPng!.Length > 100);
        Assert.True(vm.QrPng.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

        vm.IsMuted = true;
        vm.GainDb = 6f;

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.Equal("Stopped", vm.Status);
    }
}