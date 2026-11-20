using Android.Content;
using Android.Net.Wifi.P2p;
using Android.OS;
using AndroidX.Core.Content;

namespace RasaMic.Client;

/// <summary>
/// Wi-Fi Direct (M7, Android). Forms a P2P group with this phone as owner;
/// the PC joins the group via its OS Wi-Fi Direct settings, then the normal
/// Wi-Fi connect flow is used with the PC's P2P IP. No new transport — the
/// audio path stays TCP/UDP over the P2P subnet. Runtime verification needs
/// two Wi-Fi Direct capable devices.
/// </summary>
public sealed class WifiDirectHelper : IDisposable
{
    private readonly Context _context;
    private readonly WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;
    private P2pReceiver? _receiver;

    public event Action<string>? Status;

    public WifiDirectHelper(Context context)
    {
        _context = context;
        _manager = (WifiP2pManager?)context.GetSystemService(Context.WifiP2pService);
    }

    public void Start()
    {
        if (_manager is null)
        {
            Status?.Invoke("Wi-Fi Direct is not supported on this device.");
            return;
        }
        _channel = _manager.Initialize(_context, Looper.MainLooper, null);
        _receiver = new P2pReceiver(this);
        var filter = new IntentFilter();
        filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
        ContextCompat.RegisterReceiver(_context, _receiver, filter, ContextCompat.ReceiverNotExported);
        _manager.CreateGroup(_channel, new ActionListener(ok =>
            Status?.Invoke(ok
                ? "Group created — join it on the PC (Wi-Fi Direct settings), then enter the PC's P2P IP above."
                : "Could not create a Wi-Fi Direct group (is Wi-Fi on?).")));
    }

    private void OnConnectionChanged()
    {
        if (_manager is null || _channel is null)
            return;
        _manager.RequestConnectionInfo(_channel, new ConnectionInfoListener(info =>
        {
            if (!info.GroupFormed)
            {
                Status?.Invoke("Group disbanded.");
                return;
            }
            Status?.Invoke(info.IsGroupOwner
                ? "Group active — this phone is the owner (192.168.49.1). Join on the PC, then enter the PC's Wi-Fi Direct IP above."
                : $"Group active — owner is {info.GroupOwnerAddress?.HostAddress}. Enter that IP above if the server runs there.");
        }));
    }

    public void Dispose()
    {
        if (_receiver is not null)
        {
            try { _context.UnregisterReceiver(_receiver); } catch (Java.Lang.IllegalArgumentException) { }
            _receiver = null;
        }
        if (_manager is not null && _channel is not null)
            _manager.RemoveGroup(_channel, new ActionListener(_ => { }));
        _channel?.Dispose();
        _channel = null;
    }

    private sealed class ActionListener(Action<bool> done) : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        public void OnSuccess() => done(true);
        public void OnFailure(WifiP2pFailureReason reason) => done(false);
    }

    private sealed class ConnectionInfoListener(Action<WifiP2pInfo> onInfo) : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener
    {
        public void OnConnectionInfoAvailable(WifiP2pInfo? info)
        {
            if (info is not null)
                onInfo(info);
        }
    }

    private sealed class P2pReceiver(WifiDirectHelper owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == WifiP2pManager.WifiP2pConnectionChangedAction)
                owner.OnConnectionChanged();
        }
    }
}