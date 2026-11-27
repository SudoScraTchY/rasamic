using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RasaMic.Audio;
using RasaMic.Server.App.Services;
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
        var window = new MainWindow(new MainWindowViewModel(settings: new AppSettings("Ctrl+Alt+M")));
        Assert.NotNull(window.Content);
        Assert.IsType<MainWindowViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public async Task ViewModel_StartStop_RealEngine()
    {
        using var vm = new MainWindowViewModel(
            () => new NullBackend(), settings: new AppSettings("Ctrl+Alt+M"));
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

    [AvaloniaFact]
    public void ViewModel_LoadsServerOptionsFromInjectedSettings()
    {
        using var enabled = new MainWindowViewModel(settings: new AppSettings("Ctrl+Alt+M"));
        using var disabled = new MainWindowViewModel(settings: new AppSettings(
            "Ctrl+Alt+M", AudioEncrypted: false, HearOnPc: false));

        Assert.True(enabled.AudioEncrypted);
        Assert.True(enabled.HearOnPc);
        Assert.False(disabled.AudioEncrypted);
        Assert.False(disabled.HearOnPc);
    }

    [AvaloniaFact]
    public void ViewModel_HearingExplanationFollowsBackendAndSetting()
    {
        using var vm = new MainWindowViewModel(settings: new AppSettings("Ctrl+Alt+M"));

        Assert.Contains("self-monitoring", vm.HearOnPcDescription);
        Assert.Contains("does not create a virtual microphone", vm.HearOnPcDescription);

        vm.HearOnPc = false;
        Assert.Contains("no PC output", vm.HearOnPcDescription);
        Assert.Contains("does not create a virtual microphone", vm.HearOnPcDescription);

        vm.BackendChoice = 1;
        Assert.Contains("only to the selected virtual microphone", vm.HearOnPcDescription);

        vm.HearOnPc = true;
        Assert.Contains("plays on this PC", vm.HearOnPcDescription);
        Assert.Contains("selected virtual microphone", vm.HearOnPcDescription);
    }

    [AvaloniaFact]
    public void MainWindow_ServerTogglesAreDisabledWhileRunning()
    {
        using var vm = new MainWindowViewModel(settings: new AppSettings("Ctrl+Alt+M"));
        var window = new MainWindow(vm);
        var encryptToggle = window.FindControl<ToggleSwitch>("EncryptAudioToggle")!;
        var hearToggle = window.FindControl<ToggleSwitch>("HearOnPcToggle")!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(encryptToggle.IsEnabled);
        Assert.True(hearToggle.IsEnabled);

        vm.IsRunning = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(encryptToggle.IsEnabled);
        Assert.False(hearToggle.IsEnabled);

        vm.IsRunning = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(encryptToggle.IsEnabled);
        Assert.True(hearToggle.IsEnabled);
    }

    [AvaloniaFact]
    public async Task ViewModel_HearingOff_UsesSilentMonitorBackend()
    {
        using var vm = new MainWindowViewModel(settings: new AppSettings(
            "Ctrl+Alt+M", HearOnPc: false));
        vm.Port = 0;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.Contains(vm.Log, line => line.Contains("Silent (no output)", StringComparison.Ordinal));
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void ViewModel_CableChoice_LoadsAndSelectsCableInput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AudioDevice[] devices =
        [
            new("{speaker-id}", "Speakers", true),
            new("{cable-id}", "CABLE Input (VB-Audio Virtual Cable)", false),
        ];
        using var vm = new MainWindowViewModel(
            () => new NullBackend(), () => devices, new AppSettings("Ctrl+Alt+M"));
        vm.BackendChoice = 2;

        Assert.True(vm.IsCableSelected);
        Assert.Equal(2, vm.OutputDevices.Count);
        Assert.Equal("{cable-id}", vm.SelectedOutputDevice?.Id);
        Assert.Contains("CABLE Output", vm.OutputDeviceStatus);
    }
}
