using System.Net;
using System.Net.Sockets;
using Microsoft.Maui.ApplicationModel;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;

namespace RasaMic.Client;

public partial class MainPage : ContentPage
{
    private KnownServerStore? _store;
    private SessionSupervisor? _supervisor;
    private AudioPipeline? _pipeline;
    private ICaptureSource? _capture;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _senderCts;
    private Thread? _senderThread;
    private TcpListener? _usbListener;
    private ServerBrowser? _browser;
    private readonly Dictionary<string, Border> _foundRows = new();
    private readonly Dictionary<Button, string> _foundConnectIps = new();
    private bool _live;
    private bool _autoAttempted;
    private readonly IDispatcherTimer _statsTimer;
#if ANDROID
    private string[] _btAddresses = [];
    private WifiDirectHelper? _wfd;
    private Android.Net.Wifi.WifiManager.MulticastLock? _mcast;
#endif

    public MainPage()
    {
        InitializeComponent();
        _statsTimer = Dispatcher.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick += (_, _) =>
        {
            Stats? s = _supervisor?.LastStats;
            StatsLabel.Text = s is null ? "" :
                $"{s.Packets} pkt/s · {s.Bytes / 1024.0:F1} KB/s · loss {s.LossPct:F1}% · jitter {s.JitterMs:F1} ms";
        };
#if IOS
        PolarLabel.IsVisible = true;
        PolarPicker.IsVisible = true;
        UsbLabel.Text = "USB (cable)";
        UsbHint.Text = "USB: run `rasamicd --usbmux` on the PC (needs Apple Devices / iTunes), then plug in the cable.";
#endif
#if ANDROID
        HdCaptureRow.IsVisible = true;
        DirectionPicker.IsVisible = true;
        HdCaptureHint.IsVisible = true;
        StereoRow.IsVisible = true;
        BtRow.IsVisible = true;
        BtDevicePicker.IsVisible = true;
        BtHint.IsVisible = true;
        WfdButton.IsVisible = true;
        WfdStatus.IsVisible = true;
        RefreshBtDevices();
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        if (MainActivity.AutoConnectIp is string ip)
        {
            MainActivity.AutoConnectIp = null;
            ConnectWithIp(ip);
        }
#endif
        _autoAttempted = false;
        if (!_live)
            StartBrowsing();
    }

    private void StartBrowsing()
    {
        if (_browser is not null)
            return;
#if ANDROID
        // mDNS is multicast; Android filters it without a multicast lock.
        var wm = (Android.Net.Wifi.WifiManager)Android.App.Application.Context
            .GetSystemService(Android.Content.Context.WifiService)!;
        var mcast = wm.CreateMulticastLock("rasa-mdns");
        if (mcast is not null)
        {
            mcast.SetReferenceCounted(false);
            mcast.Acquire();
            _mcast = mcast;
        }
#endif
        _browser = new ServerBrowser();
        _browser.Found += OnFoundServer;
        _browser.Start();
    }

    private void StopBrowsing()
    {
        _browser?.Dispose();
        _browser = null;
#if ANDROID
        if (_mcast is not null && _mcast.IsHeld)
            _mcast.Release();
        _mcast = null;
#endif
        _foundRows.Clear();
        _foundConnectIps.Clear();
        FoundServers.Children.Clear();
        DiscoveryTitle.Text = "Looking for servers on your network…";
    }

    private void OnFoundServer(FoundServer s)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_live)
                return;
            DiscoveryTitle.Text = "Servers on your network";
            if (!_foundRows.TryGetValue(s.Id, out Border? row))
            {
                bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
                row = new Border
                {
                    Padding = new Thickness(12, 8),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                    Stroke = new SolidColorBrush((Color)Application.Current!.Resources[dark ? "BorderDark" : "Border"]),
                    BackgroundColor = (Color)Application.Current.Resources[dark ? "Surface2Dark" : "Surface2"],
                };
                _foundRows[s.Id] = row;
                FoundServers.Children.Add(row);
            }
            row.Content = MakeFoundRow(s);

            // Paired servers reconnect silently; new ones wait for the user's tap.
            KnownServer? known = Store.Load().FirstOrDefault(k => k.ServerId == s.Id);
            if (known is not null && !_autoAttempted && _supervisor is null && ConnectButton.IsEnabled)
            {
                _autoAttempted = true;
                PayloadEntry.Text = PairingPayload.Encode(new PairingPayload(
                    ProtocolConstants.Version, known.ServerId, known.Name,
                    Array.Empty<string>(), known.Port, known.StaticPubBase64Url));
                ConnectWithIp(s.Address.ToString());
            }
        });
    }

    private View MakeFoundRow(FoundServer s)
    {
        var connect = new Button
        {
            Text = "Connect",
            Style = (Style)Application.Current!.Resources["SecondaryButton"],
            HeightRequest = 36,
            MinimumHeightRequest = 36,
            Padding = new Thickness(12, 0),
            FontSize = 13,
        };
        _foundConnectIps[connect] = s.Address.ToString();
        connect.Clicked += OnFoundConnectClicked;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(), new(GridLength.Auto) }, ColumnSpacing = 8 };
        var text = new VerticalStackLayout { Spacing = 2 };
        text.Children.Add(new Label { Text = s.Name, Style = (Style)Application.Current!.Resources["RowLabel"] });
        text.Children.Add(new Label { Text = $"{s.Address}:{s.Port}", Style = (Style)Application.Current!.Resources["Mono"] });
        grid.Add(text);
        grid.Add(connect, 1);
        return grid;
    }

    private void OnFoundConnectClicked(object? sender, EventArgs e)
    {
        if (!ConnectButton.IsEnabled || sender is not Button b || !_foundConnectIps.TryGetValue(b, out string? ip))
            return;
        ServerIpEntry.Text = ip;
        PayloadEntry.Text = "";
        OnConnectClicked(null, EventArgs.Empty);
    }

    private void ShowLive(bool live)
    {
        _live = live;
        ConnectRoot.IsVisible = !live;
        LiveRoot.IsVisible = live;
        if (live)
        {
            StopBrowsing();
            _statsTimer.Start();
        }
        else
        {
            _statsTimer.Stop();
            StatsLabel.Text = "";
            StartBrowsing();
        }
    }

    public void ConnectWithIp(string ip)
    {
#if ANDROID
        Android.Util.Log.Info("RasaConn", $"ConnectWithIp {ip}, supervisor={_supervisor is not null}");
#endif
        if (_supervisor is not null)
            return;
        ServerIpEntry.Text = ip;
        OnConnectClicked(null, EventArgs.Empty);
    }

    private KnownServerStore Store => _store ??= new KnownServerStore(
        Path.Combine(FileSystem.AppDataDirectory, "known-servers.json"));

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_supervisor is not null)
        {
            Disconnect();
            return;
        }

        ConnectButton.IsEnabled = false;
        // Startup runs inside a NoGCRegion on Android; setting latency mode there throws.
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
        catch (InvalidOperationException) { }
        StatusLabel.Text = "Requesting microphone permission…";
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
#if ANDROID
        Android.Util.Log.Info("RasaConn", $"mic permission {status}");
#endif
        if (status != PermissionStatus.Granted)
        {
            StatusLabel.Text = "Microphone permission denied.";
            ConnectButton.IsEnabled = true;
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            PairingPayload? pinned = null;
            string payloadText = PayloadEntry.Text?.Trim() ?? "";
            if (payloadText.Length > 0)
                pinned = PairingPayload.Decode(payloadText);

            string ip = pinned?.Ips.FirstOrDefault() ?? ServerIpEntry.Text?.Trim() ?? "";
            ClientSession session;
            KnownServer known;
            Func<CancellationToken, Task<ClientSession?>>? reconnect = null;
#if IOS
            if (UsbSwitch.IsToggled)
            {
                // usbmuxd runs on the PC: the phone listens, the PC dials in
                // via `rasamicd --usbmux` and bridges into its server port.
                StatusLabel.Text = "USB: waiting for the PC (rasamicd --usbmux)…";
                _usbListener = new TcpListener(IPAddress.Any, ProtocolConstants.ControlPort);
                _usbListener.Start();
                session = await AcceptUsbAsync(pinned, _cts.Token);
                known = new KnownServer(session.ServerId, session.ServerName, session.StaticPubBase64Url,
                    Array.Empty<string>(), ProtocolConstants.ControlPort, true);
                reconnect = async ct =>
                {
                    try { return await AcceptUsbAsync(pinned, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }
                };
            }
            else
#endif
#if ANDROID
            if (BtSwitch.IsToggled)
            {
                if (BtDevicePicker.SelectedIndex < 0 || BtDevicePicker.SelectedIndex >= _btAddresses.Length)
                {
                    Disconnect();
                    StatusLabel.Text = "Pair the PC in Android Bluetooth settings first, then pick it here.";
                    return;
                }
                string addr = _btAddresses[BtDevicePicker.SelectedIndex];
                long btFeatures = SessionFeatures;
                StatusLabel.Text = "Bluetooth: connecting…";
                session = await ConnectBluetoothAsync(addr, pinned, _cts.Token, btFeatures);
                known = new KnownServer(session.ServerId, session.ServerName, session.StaticPubBase64Url,
                    Array.Empty<string>(), ProtocolConstants.ControlPort, true);
                reconnect = async ct =>
                {
                    try { return await ConnectBluetoothAsync(addr, pinned, ct, btFeatures); }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }
                };
            }
            else
#endif
            if (ip.Length == 0)
            {
                KnownServer? remembered = Store.Load().LastOrDefault();
                if (remembered is null)
                {
                    Disconnect();
                    StatusLabel.Text = "Enter a server IP or pairing payload.";
                    return;
                }
                StatusLabel.Text = "Looking for remembered server…";
                using var findCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                findCts.CancelAfter(TimeSpan.FromSeconds(15));
                ClientSession? found = await ServerFinder.ConnectWithRetryAsync(remembered, DeviceInfo.Name, findCts.Token, SessionFeatures);
                if (found is null)
                {
                    Disconnect();
                    StatusLabel.Text = "Remembered server not found.";
                    return;
                }
                session = found;
                known = remembered;
            }
            else
            {
                int port = pinned?.Port ?? ProtocolConstants.ControlPort;
                if (!IPAddress.TryParse(ip, out var address))
                {
                    Disconnect();
                    StatusLabel.Text = "Enter a valid server IP or pairing payload.";
                    return;
                }
                StatusLabel.Text = "Connecting…";
                session = new ClientSession();
                try
                {
                    await session.ConnectAsync(new IPEndPoint(address, port), pinned, DeviceInfo.Name, _cts.Token, UsbSwitch.IsToggled, features: SessionFeatures);
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
                known = new KnownServer(session.ServerId, session.ServerName, session.StaticPubBase64Url,
                    new[] { address.ToString() }, port, session.TcpAudio);
            }

            _pipeline = new AudioPipeline(new PipelineOptions { Channels = CaptureChannels });
#if ANDROID
            _capture = HdCaptureSwitch.IsToggled
                ? new AudioRecordCapture(pcm => _pipeline?.Feed(pcm), DirectionPicker.SelectedIndex)
                : new CaptureDevice(48000, CaptureChannels, pcm => _pipeline?.Feed(pcm));
#else
            _capture = new CaptureDevice(48000, 1, pcm => _pipeline?.Feed(pcm));
#endif

            _supervisor = new SessionSupervisor(Store, DeviceInfo.Name, SessionFeatures);
            _supervisor.StateChanged += OnSupervisorState;
            _ = _supervisor.RunAsync(session, known, _cts.Token, reconnect);
            _capture.Start();

            ConnectButton.Text = "Disconnect";
        }
        catch (Exception ex)
        {
            Disconnect();
            StatusLabel.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

#if ANDROID
    private static async Task<ClientSession> ConnectBluetoothAsync(string address, PairingPayload? pinned, CancellationToken ct, long features = 0)
    {
        Stream tunnel = await BluetoothTransport.ConnectAsync(address, ct);
        var session = new ClientSession();
        try
        {
            await session.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ProtocolConstants.ControlPort),
                pinned, DeviceInfo.Name, ct, tunnel: tunnel, features: features);
            return session;
        }
        catch
        {
            session.Dispose();
            tunnel.Dispose();
            throw;
        }
    }

    private void RefreshBtDevices()
    {
        try
        {
            var bonded = Android.Bluetooth.BluetoothAdapter.DefaultAdapter?.BondedDevices;
            if (bonded is null)
                return;
            var names = new List<string>();
            var addrs = new List<string>();
            foreach (var d in bonded)
            {
                if (d.Address is null)
                    continue;
                names.Add(d.Name ?? d.Address);
                addrs.Add(d.Address);
            }
            BtDevicePicker.ItemsSource = names;
            _btAddresses = addrs.ToArray();
            if (names.Count > 0)
                BtDevicePicker.SelectedIndex = 0;
        }
        catch { /* permission not granted yet; refreshed when the switch is toggled */ }
    }
#endif

    private void OnBtToggled(object? sender, ToggledEventArgs e)
    {
#if ANDROID
        if (e.Value)
            _ = RequestBtPermissionAsync();
#endif
    }

    // Stereo is an AAudio-path option; AudioRecord capture stays mono. iOS mic is mono.
    private long SessionFeatures
    {
        get
        {
#if ANDROID
            return StereoSwitch.IsToggled && !HdCaptureSwitch.IsToggled ? ProtocolConstants.FeatureStereo : 0;
#else
            return 0;
#endif
        }
    }

    private int CaptureChannels
    {
        get
        {
#if ANDROID
            return StereoSwitch.IsToggled && !HdCaptureSwitch.IsToggled ? 2 : 1;
#else
            return 1;
#endif
        }
    }

    private async void OnWfdClicked(object? sender, EventArgs e)
    {
#if ANDROID
        if (_wfd is not null)
        {
            _wfd.Dispose();
            _wfd = null;
            WfdButton.Text = "Create Wi-Fi Direct group";
            WfdStatus.Text = "";
            return;
        }
        PermissionStatus perm = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? await Permissions.RequestAsync<Permissions.NearbyWifiDevices>()
            : await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (perm != PermissionStatus.Granted)
        {
            WfdStatus.Text = "Wi-Fi Direct permission denied.";
            return;
        }
        _wfd = new WifiDirectHelper(Android.App.Application.Context);
        _wfd.Status += s => MainThread.BeginInvokeOnMainThread(() => WfdStatus.Text = s);
        _wfd.Start();
        WfdButton.Text = "Remove Wi-Fi Direct group";
#endif
    }

#if ANDROID
    private async Task RequestBtPermissionAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.Bluetooth>();
        if (status == PermissionStatus.Granted)
            RefreshBtDevices();
        else
            StatusLabel.Text = "Bluetooth permission denied.";
    }
#endif

    private async Task<ClientSession> AcceptUsbAsync(PairingPayload? pinned, CancellationToken ct)
    {
        TcpClient accepted = await _usbListener!.AcceptTcpClientAsync(ct);
        var session = new ClientSession();
        try
        {
            await session.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ProtocolConstants.ControlPort),
                pinned, DeviceInfo.Name, ct, tunnel: accepted.GetStream());
            return session;
        }
        catch
        {
            session.Dispose();
            accepted.Dispose();
            throw;
        }
    }

    private void OnSupervisorState(string state)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_supervisor is null)
                return;
            switch (state)
            {
                case "connected":
                    RestartSender();
#if ANDROID
                    MicForegroundService.Start(Android.App.Application.Context);
#endif
                    ShowLive(true);
                    SetTally("live");
                    StatusLabel.Text = $"Connected to {_supervisor.Session?.ServerName}";
                    PairingCodeLabel.Text = _supervisor.Session?.PairingCode ?? "";
                    PairingCodeLabel.IsVisible = true;
                    PairingHintLabel.IsVisible = true;
                    break;
                case "reconnecting":
                    StopSender();
                    SetTally("warn");
                    StatusLabel.Text = "Connection lost — reconnecting…";
                    break;
            }
        });
    }

    private void RestartSender()
    {
        StopSender();
        ClientSession? session = _supervisor?.Session;
        if (session is null || _pipeline is null || _cts is null)
            return;
        _senderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        AudioPipeline pipeline = _pipeline;
        CancellationToken token = _senderCts.Token;
        _senderThread = new Thread(() => session.RunSender(pipeline, token))
        {
            IsBackground = true,
            Name = "rasa-sender",
        };
        _senderThread.Start();
    }

    private void StopSender()
    {
        _senderCts?.Cancel();
        _senderThread?.Join(500);
        _senderThread = null;
        _senderCts?.Dispose();
        _senderCts = null;
    }

    private void Disconnect()
    {
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; }
        catch (InvalidOperationException) { }
#if ANDROID
        MicForegroundService.Stop(Android.App.Application.Context);
#endif
        try { _supervisor?.Session?.SendControlAsync(new Bye("user-stop")).Wait(500); } catch { }
        _cts?.Cancel();
        _usbListener?.Stop();
        _usbListener = null;
        StopSender();
        _capture?.Stop();
        _capture?.Dispose();
        _capture = null;
        _pipeline?.Dispose();
        _pipeline = null;
        _supervisor?.Dispose();
        _supervisor = null;
        _cts?.Dispose();
        _cts = null;
        PairingCodeLabel.IsVisible = false;
        PairingHintLabel.IsVisible = false;
        ConnectButton.Text = "Connect";
        StatusLabel.Text = "Not connected";
        SetTally("idle");
        ShowLive(false);
    }

    // The tally lamp: amber while live, clay while reconnecting, muted otherwise.
    private void SetTally(string state)
    {
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        TallyDot.Color = state switch
        {
            "live" => (Color)Application.Current!.Resources[dark ? "PrimaryDark" : "Primary"],
            "warn" => (Color)Application.Current!.Resources[dark ? "SecondaryDark" : "Secondary"],
            _ => (Color)Application.Current!.Resources[dark ? "TextMutedDark" : "TextMuted"],
        };
    }

    private void OnMuteToggled(object? sender, ToggledEventArgs e) => _pipeline?.SetMute(e.Value);

    private void OnTransportsToggled(object? sender, EventArgs e) => ToggleSection(TransportsContent, TransportsChevron);
    private void OnProcessingToggled(object? sender, EventArgs e) => ToggleSection(ProcessingContent, ProcessingChevron);

    private static void ToggleSection(View content, Label chevron)
    {
        content.IsVisible = !content.IsVisible;
        chevron.Text = content.IsVisible ? Helpers.IconFont.ChevronUp : Helpers.IconFont.ChevronDown;
    }
    private void OnNsToggled(object? sender, ToggledEventArgs e) => _pipeline?.SetNoiseSuppression(e.Value);
    private void OnAgcToggled(object? sender, ToggledEventArgs e) => _pipeline?.SetAgc(e.Value);
    private void OnGainChanged(object? sender, ValueChangedEventArgs e) => _pipeline?.SetGain((float)e.NewValue);

    private void OnBitrateChanged(object? sender, EventArgs e)
    {
        int[] rates = [16000, 32000, 64000, 96000];
        int idx = BitratePicker.SelectedIndex;
        if (idx >= 0 && idx < rates.Length)
            _pipeline?.SetBitrate(rates[idx]);
    }

    private void OnPolarChanged(object? sender, EventArgs e)
    {
        int idx = PolarPicker.SelectedIndex;
        if (idx >= 0 && _capture is CaptureDevice cd && !cd.SetPolarPattern(idx))
            StatusLabel.Text = "This input has no selectable polar patterns.";
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Streaming continues via the Android foreground service while backgrounded.
        StopBrowsing();
    }
}