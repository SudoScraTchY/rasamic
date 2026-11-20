namespace RasaMic.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int ControlPort = 47300;
    public const int AudioPort = 47300;
    public const int BeaconPort = 47301;
    public const string MdnsServiceType = "_rasamic._udp";
    public const string BluetoothServiceUuid = "7a3e9c10-5b2d-4e8f-9a61-0c4d5e6f7a8b";
    public const byte AudioMagic = 0x52; // 'R'
    public const int AudioHeaderSize = 24;
    public const int AesGcmTagSize = 16;
    public const int AesGcmNonceSize = 12;
    public const int MaxControlFrameSize = 16 * 1024;
    public const uint DirectionClientToServer = 0;
    public const uint DirectionServerToClient = 1;
    public const long FeatureStereo = 1L << 0; // Hello.Features: client sends interleaved stereo
}