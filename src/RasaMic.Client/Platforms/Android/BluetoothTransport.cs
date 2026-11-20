using Android.Bluetooth;
using Java.Util;
using RasaMic.Client.Core;
using RasaMic.Protocol;

namespace RasaMic.Client;

/// <summary>
/// Android RFCOMM client (M7). Dials the PC's Rasa Bluetooth service once the
/// devices are paired in Android settings; audio rides the framed tunnel path.
/// Needs BLUETOOTH_CONNECT (Android 12+) — requested from the UI switch.
/// </summary>
public static class BluetoothTransport
{
    public static async Task<Stream> ConnectAsync(string deviceAddress, CancellationToken ct)
    {
        BluetoothAdapter adapter = BluetoothAdapter.DefaultAdapter
            ?? throw new InvalidOperationException("No Bluetooth adapter on this device.");
        if (!adapter.IsEnabled)
            throw new InvalidOperationException("Bluetooth is turned off.");
        BluetoothDevice device = adapter.GetRemoteDevice(deviceAddress)
            ?? throw new InvalidOperationException("Invalid Bluetooth device address.");
        BluetoothSocket socket = device.CreateRfcommSocketToServiceRecord(UUID.FromString(ProtocolConstants.BluetoothServiceUuid)!)
            ?? throw new InvalidOperationException("RFCOMM socket creation failed.");
        try
        {
            using (ct.Register(() => { try { socket.Close(); } catch { } }))
            {
                await Task.Run(() => socket.Connect(), ct);
            }
        }
        catch
        {
            socket.Close();
            throw;
        }
        return new BluetoothStream(socket);
    }

    private sealed class BluetoothStream(BluetoothSocket socket)
        : DuplexStream(socket.InputStream!, socket.OutputStream!)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                socket.Close();
        }
    }
}