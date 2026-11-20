using System.Net;
using System.Net.Sockets;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using RasaMic.Protocol;

namespace RasaMic.Server;

/// <summary>
/// PC-side Bluetooth RFCOMM bridge (M7, Android clients). The phone dials the
/// PC's Rasa RFCOMM service; each accepted socket is bridged into the local TCP
/// listener so the session/crypto path stays transport-agnostic. Pair the
/// phone with the PC in OS settings first. Windows (Win32 stack) and Linux
/// (BlueZ) via 32feet; macOS has no RFCOMM listener support in 32feet.
/// </summary>
public static class BluetoothBridge
{
    public static async Task RunAsync(int serverPort, CancellationToken ct = default)
    {
        var listener = new BluetoothListener(new Guid(ProtocolConstants.BluetoothServiceUuid))
        {
            ServiceName = "Rasa Mic",
        };
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                BluetoothClient bt;
                try
                {
                    // no CT overload: interrupt a pending accept by stopping the listener
                    using (ct.Register(() => listener.Stop()))
                    {
                        bt = await listener.AcceptBluetoothClientAsync();
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                if (bt is null)
                    break;
                using (bt)
                {
                    try
                    {
                        using var local = new TcpClient { NoDelay = true };
                        await local.ConnectAsync(IPAddress.Loopback, serverPort, ct);
                        NetworkStream btStream = bt.GetStream();
                        NetworkStream localStream = local.GetStream();
                        var up = btStream.CopyToAsync(localStream, ct);
                        var down = localStream.CopyToAsync(btStream, ct);
                        await Task.WhenAny(up, down);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* drop this connection, accept the next */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }
}