using System.Net;
using System.Net.Sockets;
using RasaMic.Core.Usb;
using RasaMic.Protocol;

namespace RasaMic.Server;

/// <summary>
/// PC-side iOS USB bridge (M6). usbmuxd runs on the PC, so the server dials
/// the phone: usbmuxd Connect → tunnel to the Rasa client's listening port on
/// the iPhone → bridged into the local server listener. Retries until a
/// USB-attached device appears; the phone app must be in USB mode.
/// Requires Apple Devices / iTunes (Windows), native usbmuxd (macOS) or the
/// distro usbmuxd package (Linux).
/// </summary>
public static class UsbBridge
{
    public static async Task RunAsync(int serverPort, int phonePort = ProtocolConstants.ControlPort, int muxPort = UsbmuxClient.DefaultPort, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var mux = await UsbmuxClient.ConnectAsync(port: muxPort, ct: ct);
                var devices = await mux.ListDevicesAsync(ct);
                UsbmuxDevice? device = devices.FirstOrDefault(d => d.ConnectionType == "USB");
                if (device is null)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }
                var tunnel = await mux.ConnectToDeviceAsync(device.DeviceId, phonePort, ct);
                using var local = new TcpClient { NoDelay = true };
                await local.ConnectAsync(IPAddress.Loopback, serverPort, ct);
                var localStream = local.GetStream();
                var up = tunnel.CopyToAsync(localStream, ct);
                var down = localStream.CopyToAsync(tunnel, ct);
                await Task.WhenAny(up, down);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}