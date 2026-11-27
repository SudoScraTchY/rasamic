using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using RasaMic.Audio;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;
using RasaMic.Server.App.Services;
using RasaMic.Server.Backends;

namespace RasaMic.Server.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int WindowsCableBackendIndex = 2;

    private readonly Func<IVirtualMicBackend>? _backendFactory;
    private readonly Func<IReadOnlyList<AudioDevice>> _outputDeviceProvider;
    private readonly DispatcherTimer _sessionTimer;
    private AppSettings _settings;
    private RasaMicServer? _server;
    private ServerIdentity? _identity;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private BeaconBroadcaster? _beacon;
    private Task? _beaconTask;
    private RasaMdnsAdvertiser? _mdns;
    private HotkeyService? _hotkeys;
    private Hotkey _hotkey;

    public MainWindowViewModel(
        Func<IVirtualMicBackend>? backendFactory = null,
        Func<IReadOnlyList<AudioDevice>>? outputDeviceProvider = null,
        AppSettings? settings = null)
    {
        _backendFactory = backendFactory;
        _outputDeviceProvider = outputDeviceProvider ?? AudioDeviceEnumerator.EnumeratePlayback;
        _settings = settings ?? SettingsStore.Load();
        _hotkey = Hotkey.TryParse(_settings.MuteHotkey, out Hotkey saved) ? saved : Hotkey.Default;
        _hotkeyText = _hotkey.ToString();
        _backendChoice = _settings.BackendChoice;
        if (_backendChoice < 0 || _backendChoice >= BackendChoices.Count)
            _backendChoice = 0;
        _audioEncrypted = _settings.AudioEncrypted;
        _hearOnPc = _settings.HearOnPc;
        _hearOnPcDescription = GetHearOnPcDescription();
        _localIps = string.Join(" · ", LanIps());
        if (IsCableSelected)
            RefreshOutputDevices();
        _sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            HasSession = _server?.HasActiveSession == true;
            Stats? s = _server?.LastSessionStats;
            StatsText = s is null ? "" :
                $"{s.Packets} pkt/s · {s.Bytes / 1024.0:F1} KB/s · loss {s.LossPct:F1}% · jitter {s.JitterMs:F1} ms · buffer {s.BufferMs:F0} ms · output {s.OutputBufferMs:F0} ms · RTT {s.RttMs:F1} ms";
            UpdateTally();
        });
        UpdateTally();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTally();

    // The tally lamp: amber while a session is live, clay while listening, muted when stopped.
    private void UpdateTally()
    {
        string key = HasSession ? "Primary" : IsRunning ? "Secondary" : "TextMuted";
        if (Application.Current is { } app &&
            app.TryGetResource(key, app.ActualThemeVariant, out object? res) && res is Color c)
            TallyBrush = new SolidColorBrush(c);
    }

    [ObservableProperty]
    private string _serverName = Environment.MachineName;

    [ObservableProperty]
    private int _port = ProtocolConstants.ControlPort;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "Stopped";

    [ObservableProperty]
    private string _pairingText = "";

    [ObservableProperty]
    private byte[]? _qrPng;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private float _gainDb;

    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    private string _statsText = "";

    [ObservableProperty]
    private string _localIps = "";

    [ObservableProperty]
    private int _backendChoice;

    [ObservableProperty]
    private bool _audioEncrypted;

    [ObservableProperty]
    private bool _hearOnPc;

    [ObservableProperty]
    private string _hearOnPcDescription = "";

    [ObservableProperty]
    private AudioDevice? _selectedOutputDevice;

    [ObservableProperty]
    private string _outputDeviceStatus = "Choose the playback endpoint that receives phone audio.";

    public ObservableCollection<AudioDevice> OutputDevices { get; } = new();

    public bool IsCableSelected => OperatingSystem.IsWindows() && BackendChoice == WindowsCableBackendIndex;

    public IReadOnlyList<string> BackendChoices { get; } =
        OperatingSystem.IsWindows() ? ["Monitor (PC speakers)", "Rasa Mic (virtual mic)", "VB-CABLE (virtual mic)"] :
        OperatingSystem.IsLinux() ? ["Monitor (PC speakers)", "PipeWire virtual mic"] :
        ["Monitor (PC speakers)", "macOS HAL virtual mic"];

    private IVirtualMicBackend CreateBackend()
    {
        // An injected factory supplies a complete backend, preserving test and host overrides.
        if (_backendFactory is not null)
            return _backendFactory.Invoke();

        IVirtualMicBackend primary = BackendChoice switch
        {
            WindowsCableBackendIndex when OperatingSystem.IsWindows() =>
                SelectedOutputDevice is { } device ? new ExternalCableBackend(device) : new ExternalCableBackend(),
            1 when OperatingSystem.IsWindows() => new WindowsDriverBackend(),
            1 when OperatingSystem.IsLinux() => new PipeWireBackend(),
            1 when OperatingSystem.IsMacOS() => new MacHalBackend(),
            _ => HearOnPc ? new MonitorBackend() : new SilentBackend(),
        };
        return BackendChoice != 0 && HearOnPc ? new SelfMonitoringBackend(primary) : primary;
    }

    partial void OnBackendChoiceChanged(int value)
    {
        OnPropertyChanged(nameof(IsCableSelected));
        HearOnPcDescription = GetHearOnPcDescription();
        if (IsCableSelected)
            RefreshOutputDevices();
    }

    partial void OnHearOnPcChanged(bool value) => HearOnPcDescription = GetHearOnPcDescription();

    private string GetHearOnPcDescription() => BackendChoice == 0
        ? HearOnPc
            ? "Monitor sends phone audio to this PC's speakers for self-monitoring; it does not create a virtual microphone."
            : "Monitor has no PC output while phone hearing is off; it does not create a virtual microphone."
        : HearOnPc
            ? "Phone audio plays on this PC and is sent to the selected virtual microphone."
            : "Phone audio goes only to the selected virtual microphone.";

    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        OutputDeviceStatus = value is null
            ? "No playback endpoint selected. Install VB-CABLE and click Refresh, or choose an available endpoint."
            : "Rasa plays to this endpoint. In Discord/OBS, select the matching CABLE Output recording device.";
    }

    [RelayCommand]
    private void RefreshDevices() => RefreshOutputDevices();

    private void RefreshOutputDevices()
    {
        OutputDevices.Clear();
        SelectedOutputDevice = null;
        if (!OperatingSystem.IsWindows())
        {
            OutputDeviceStatus = "External audio cables are configured on Windows.";
            return;
        }

        try
        {
            foreach (var device in _outputDeviceProvider())
                OutputDevices.Add(device);

            if (OutputDevices.Count == 0)
            {
                OutputDeviceStatus = "No playback endpoints were found.";
                return;
            }

            SelectedOutputDevice =
                OutputDevices.FirstOrDefault(d => d.Id == _settings.OutputDeviceId && !string.IsNullOrEmpty(d.Id))
                ?? OutputDevices.FirstOrDefault(d => d.Name == _settings.OutputDeviceName)
                ?? OutputDevices.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            OutputDeviceStatus = SelectedOutputDevice is null
                ? "No CABLE Input endpoint found. Install VB-CABLE and click Refresh, or choose another playback endpoint."
                : "Rasa plays to this endpoint. In Discord/OBS, select the matching CABLE Output recording device.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            OutputDeviceStatus = $"Unable to list playback endpoints: {ex.Message}";
        }
    }

    private void SaveSettings()
    {
        _settings = new AppSettings(
            _hotkey.ToString(), BackendChoice, SelectedOutputDevice?.Id, SelectedOutputDevice?.Name,
            AudioEncrypted, HearOnPc);
        SettingsStore.Save(_settings);
    }

    private static string[] LanIps()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();
        // Virtual switches (WSL/Hyper-V/VPN) advertise IPs a phone cannot reach.
        var physical = up.Where(n => !VirtualAdapter.IsMatch(n.Description + " " + n.Name)).ToArray();
        return (physical.Length > 0 ? physical : up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToArray();
    }

    private static readonly Regex VirtualAdapter = new(
        "vEthernet|Hyper-V|WSL|Virtual|VPN|Tunnel|Bluetooth|Pseudo|Loopback",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [ObservableProperty]
    private IBrush _tallyBrush = new SolidColorBrush(Colors.Gray);

    [ObservableProperty]
    private string _hotkeyText;

    [ObservableProperty]
    private bool _isCapturingHotkey;

    public ObservableCollection<string> Log { get; } = new();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
            return;
        IVirtualMicBackend? pendingBackend = null;
        string? backendName = null;
        try
        {
            SaveSettings();
            _identity = ServerIdentityStore.LoadOrCreate();
            pendingBackend = CreateBackend();
            backendName = pendingBackend.Name;
            if (pendingBackend is ExternalCableBackend cable)
                cable.Validate();
            _server = new RasaMicServer(_identity, pendingBackend, Port, ServerName, audioEncrypted: AudioEncrypted);
            pendingBackend = null;
            _server.LocalMuted = IsMuted;
            _server.LocalGainDb = GainDb;
            _cts = new CancellationTokenSource();
            _runTask = _server.RunAsync(_cts.Token);
            while (!_server.IsListening && !_runTask.IsCompleted)
                await Task.Delay(20);
            if (!_server.IsListening)
                throw new InvalidOperationException("Server failed to start.");

            LocalIps = string.Join(" · ", LanIps());
            PairingText = _server.PairingText;
            QrPng = MakeQr(PairingText);
            IsRunning = true;
            Status = $"Listening on TCP/UDP {_server.Port}";
            UpdateTally();
            AddLog($"Server id {_identity.ServerId}");
            AddLog($"Output: {backendName}");
            AddLog(AudioEncrypted ? "Audio encryption on." : "Audio encryption off; LAN audio is unauthenticated.");

            _beacon = new BeaconBroadcaster(new ServerBeacon(ProtocolConstants.Version, _identity.ServerId, ServerName, _server.Port));
            _beaconTask = _beacon.RunAsync(_cts.Token);
            try
            {
                _mdns = new RasaMdnsAdvertiser(ServerName, _identity.ServerId, _server.Port);
                _mdns.Start();
                AddLog("Advertising via mDNS + UDP beacon.");
            }
            catch (Exception ex)
            {
                AddLog($"mDNS advertise failed ({ex.Message}); beacon still active.");
                _mdns = null;
            }

            _hotkeys = new HotkeyService(_hotkey);
            _hotkeys.MuteHotkey += () => Dispatcher.UIThread.Post(() => IsMuted = !IsMuted);
            _hotkeys.HotkeyCaptured += OnHotkeyCaptured;
            _hotkeys.Start();
            _sessionTimer.Start();
        }
        catch (Exception ex)
        {
            pendingBackend?.Dispose();
            Status = $"Failed: {ex.Message}";
            await StopAsync();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _sessionTimer.Stop();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _mdns?.Dispose();
        _mdns = null;
        _cts?.Cancel();
        _beacon?.Dispose();
        _beacon = null;
        if (_beaconTask is not null)
            try { await _beaconTask; } catch (OperationCanceledException) { }
        _beaconTask = null;
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }
        _runTask = null;
        _cts?.Dispose();
        _cts = null;
        _server?.Dispose();
        _server = null;
        _identity?.Dispose();
        _identity = null;
        IsRunning = false;
        HasSession = false;
        Status = "Stopped";
        UpdateTally();
    }

    public void Shutdown() => StopAsync().GetAwaiter().GetResult();

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        string[] ips = LanIps();
        if (ips.Length == 0)
            return;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync($"{ips[0]}:{Port}");
            AddLog($"Address {ips[0]}:{Port} copied to clipboard.");
        }
    }

    [RelayCommand]
    private async Task CopyPairingAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(PairingText);
            AddLog("Pairing payload copied to clipboard.");
        }
    }

    [RelayCommand]
    private void ChangeHotkey()
    {
        if (_hotkeys is null || IsCapturingHotkey)
            return;
        IsCapturingHotkey = true;
        HotkeyText = "Press new hotkey…";
        _hotkeys.CaptureMode = true;
    }

    private void OnHotkeyCaptured(Hotkey hotkey) => Dispatcher.UIThread.Post(() =>
    {
        _hotkey = hotkey;
        SaveSettings();
        HotkeyText = hotkey.ToString();
        IsCapturingHotkey = false;
        AddLog($"Mute hotkey set to {hotkey}");
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(_hotkey);
        _hotkeys.MuteHotkey += () => Dispatcher.UIThread.Post(() => IsMuted = !IsMuted);
        _hotkeys.HotkeyCaptured += OnHotkeyCaptured;
        _hotkeys.Start();
    });

    partial void OnIsMutedChanged(bool value)
    {
        if (_server is not null)
            _server.LocalMuted = value;
    }

    partial void OnGainDbChanged(float value)
    {
        if (_server is not null)
            _server.LocalGainDb = value;
    }

    private static byte[] MakeQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }

    private void AddLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 400)
            Log.RemoveAt(0);
    }

    private sealed class SilentBackend : IVirtualMicBackend
    {
        public string Name => "Silent (no output)";
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) { }
        public void Stop() { }
        public void Dispose() { }
    }

    public void Dispose()
    {
        _sessionTimer.Stop();
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnThemeChanged;
        _hotkeys?.Dispose();
        _mdns?.Dispose();
        _beacon?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _server?.Dispose();
        _identity?.Dispose();
    }
}