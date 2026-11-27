using RasaMic.Audio;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;
using RasaMic.Server;
using RasaMic.Server.Backends;

int port = ProtocolConstants.ControlPort;
string name = Environment.MachineName;
string? keyPath = null;
bool useCable = false;
bool useDriver = false;
bool usePipeWire = false;
bool useHal = false;
bool useUsbmux = false;
bool useBluetooth = false;
bool discovery = true;
bool audioEncrypted = true;
bool listOutputs = false;
bool doctor = false;
string? cableName = null;
string? autoStart = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--monitor":
            break; // speaker playback, no driver needed
        case "--cable":
            useCable = true;
            break;
        case "--cable-name" when i + 1 < args.Length:
            cableName = args[++i];
            if (string.IsNullOrWhiteSpace(cableName))
            {
                Console.Error.WriteLine("--cable-name expects a non-empty endpoint name");
                return 1;
            }
            break;
        case "--list-outputs":
            listOutputs = true;
            break;
        case "--driver":
            useDriver = true;
            break;
        case "--pipewire":
            usePipeWire = true;
            break;
        case "--hal":
            useHal = true;
            break;
        case "--usbmux":
            useUsbmux = true;
            break;
        case "--bluetooth":
            useBluetooth = true;
            break;
        case "doctor":
            doctor = true;
            break;
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--key" when i + 1 < args.Length:
            keyPath = args[++i];
            break;
        case "--no-discovery":
            discovery = false;
            break;
        case "--plain":
        case "--no-encryption":
            audioEncrypted = false;
            break;
        case "--autostart" when i + 1 < args.Length:
            autoStart = args[++i];
            break;
        default:
            Console.Error.WriteLine($"rasamicd: unknown argument '{args[i]}'");
            Console.Error.WriteLine("usage: rasamicd [--monitor | --cable [--cable-name NAME] | --driver | --pipewire | --hal] [--usbmux] [--bluetooth] [--port N] [--name NAME] [--key PATH] [--no-discovery] [--plain] [--autostart enable|disable|status] | [--list-outputs] | doctor");
            return 1;
    }
}

if (cableName is not null && !useCable)
{
    Console.Error.WriteLine("--cable-name requires --cable");
    return 1;
}

if (doctor)
{
    RasaMic.Server.Cli.Doctor.Run();
    return 0;
}

if (autoStart is not null)
{
    switch (autoStart)
    {
        case "enable":
            AutoStart.Enable(AutoStart.CurrentExePath);
            Console.WriteLine($"Auto-start enabled for {AutoStart.CurrentExePath}.");
            if (OperatingSystem.IsMacOS())
                Console.WriteLine($"Loaded at login via {AutoStart.LaunchAgentPath}; to start now: launchctl load {AutoStart.LaunchAgentPath}");
            else if (!OperatingSystem.IsWindows())
                Console.WriteLine($"Unit written to {AutoStart.SystemdUnitPath}; to start now: systemctl --user enable --now rasamicd");
            break;
        case "disable":
            AutoStart.Disable();
            Console.WriteLine("Auto-start disabled.");
            break;
        case "status":
            Console.WriteLine(AutoStart.IsEnabled() ? "enabled" : "disabled");
            break;
        default:
            Console.Error.WriteLine("--autostart expects enable|disable|status");
            return 1;
    }
    return 0;
}

if (listOutputs)
{
    try
    {
        Console.WriteLine("Playback devices:");
        foreach (var device in AudioDeviceEnumerator.EnumeratePlayback())
            Console.WriteLine($"  {device.Label} | {device.Id}");
        Console.WriteLine("Capture devices:");
        foreach (var device in AudioDeviceEnumerator.EnumerateCapture())
            Console.WriteLine($"  {device.Label} | {device.Id}");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
    {
        Console.Error.WriteLine($"Unable to enumerate audio devices: {ex.Message}");
        return 1;
    }
}

keyPath ??= ServerIdentityStore.DefaultPath;
using ServerIdentity identity = ServerIdentityStore.LoadOrCreate(keyPath);

{
    IVirtualMicBackend backend = useCable ? new ExternalCableBackend(cableName ?? ExternalCableBackend.DefaultEndpointName)
        : useDriver ? new WindowsDriverBackend()
        : usePipeWire ? new PipeWireBackend()
        : useHal ? new MacHalBackend()
        : new MonitorBackend();
    if (backend is ExternalCableBackend cable)
    {
        try
        {
            cable.Validate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
    using var server = new RasaMicServer(identity, backend, port, name, audioEncrypted);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine($"Rasa server — id {identity.ServerId}");
    Console.WriteLine($"Backend: {backend.Name}");
    Console.WriteLine(audioEncrypted
        ? "Audio encryption: on"
        : "Audio encryption: off (plain, unauthenticated LAN traffic)");
    Console.WriteLine("Pairing payload (encode as QR in the GUI; paste into client for manual pairing):");
    Console.WriteLine(server.PairingText);

    Task runTask = server.RunAsync(cts.Token);
    while (!server.IsListening && !runTask.IsCompleted)
        await Task.Delay(20);
    Console.WriteLine($"Listening on TCP/UDP {server.Port}. Ctrl+C to stop.");

    _ = Task.Run(async () =>
    {
        long lastPkts = -1;
        int lastSessions = 0;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, cts.Token);
                if (server.SessionCount != lastSessions)
                {
                    lastSessions = server.SessionCount;
                    Console.WriteLine($"session start: {server.LastClientName}, {server.LastSessionChannels} ch");
                }
                Stats? s = server.LastSessionStats;
                if (s is not null && s.Packets != lastPkts)
                {
                    lastPkts = s.Packets;
                    Console.WriteLine($"session: {s.Packets} pkts, {s.Bytes / 1024} KiB, loss {s.LossPct:0.0}%, jitter {s.JitterMs:0.0} ms, rtt {s.RttMs:0.0} ms, dec {s.DecodeP95Ms:0.0} dwell {s.DwellP95Ms:0.0} wr {s.WriteP95Ms:0.0} out {s.OutputBufferMs:0.0} ms dev {s.OutputDeviceBufferMs:0.0} ms drops {s.OutputDrops}");
                }
            }
        }
        catch (OperationCanceledException) { }
    });

    Task? usbTask = null;
    if (useUsbmux && server.IsListening)
    {
        usbTask = UsbBridge.RunAsync(server.Port, ct: cts.Token);
        Console.WriteLine("iOS USB bridge active (usbmuxd 127.0.0.1:27015). On the iPhone: enable USB mode, then connect the cable.");
    }

    Task? btTask = null;
    if (useBluetooth && server.IsListening)
    {
        try
        {
            btTask = BluetoothBridge.RunAsync(server.Port, cts.Token);
            Console.WriteLine("Bluetooth bridge active (RFCOMM \"Rasa Mic\"). Pair the phone with this PC, then pick it in the Android app.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bluetooth unavailable ({ex.Message}); continuing without it.");
        }
    }

    BeaconBroadcaster? beacon = null;
    RasaMdnsAdvertiser? mdns = null;
    Task? beaconTask = null;
    if (discovery && server.IsListening)
    {
        beacon = new BeaconBroadcaster(new ServerBeacon(ProtocolConstants.Version, identity.ServerId, name, server.Port));
        beaconTask = beacon.RunAsync(cts.Token);
        try
        {
            mdns = new RasaMdnsAdvertiser(name, identity.ServerId, server.Port);
            mdns.Start();
            Console.WriteLine($"Advertising via mDNS ({ProtocolConstants.MdnsServiceType}) and UDP beacon :{ProtocolConstants.BeaconPort}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mDNS advertise failed ({ex.Message}); beacon still active.");
            mdns = null;
        }
    }

    try
    {
        await runTask;
    }
    catch (OperationCanceledException) { }
    finally
    {
        mdns?.Dispose();
        beacon?.Dispose();
        if (beaconTask is not null)
            try { await beaconTask; } catch (OperationCanceledException) { }
        if (usbTask is not null)
            try { await usbTask; } catch (OperationCanceledException) { }
        if (btTask is not null)
            try { await btTask; } catch (OperationCanceledException) { }
    }
}

return 0;