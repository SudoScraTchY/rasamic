using System.Text.Json;
using System.Text.Json.Serialization;

namespace RasaMic.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(HelloAck), "helloAck")]
[JsonDerivedType(typeof(PairConfirm), "pairConfirm")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(SetMute), "setMute")]
[JsonDerivedType(typeof(SetGain), "setGain")]
[JsonDerivedType(typeof(SetBitrate), "setBitrate")]
[JsonDerivedType(typeof(SetFrameSize), "setFrameSize")]
[JsonDerivedType(typeof(SetNoiseSuppression), "setNs")]
[JsonDerivedType(typeof(SetAgc), "setAgc")]
[JsonDerivedType(typeof(SetPolarPattern), "setPolar")]
[JsonDerivedType(typeof(Stats), "stats")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(ClientInfo), "clientInfo")]
[JsonDerivedType(typeof(Bye), "bye")]
[JsonDerivedType(typeof(RasaMic.Protocol.Error), "error")]
public abstract record ControlMessage;

public sealed record Hello(
    int ProtoVer,
    string ClientId,
    string ClientName,
    byte[] EphPub,
    long Features) : ControlMessage;

public sealed record HelloAck(
    string ServerId,
    string ServerName,
    byte[] EphPub,
    byte[] StaticPub,
    uint SessionId,
    int ProtoVer) : ControlMessage;

public sealed record PairConfirm(string Code) : ControlMessage;

public sealed record Resume(uint SessionId, byte[] Proof) : ControlMessage;

public sealed record SetMute(bool Muted) : ControlMessage;

public sealed record SetGain(float Db) : ControlMessage;

public sealed record SetBitrate(int Bps) : ControlMessage;

public sealed record SetFrameSize(int Ms) : ControlMessage;

public sealed record SetNoiseSuppression(bool Enabled) : ControlMessage;

public sealed record SetAgc(bool Enabled) : ControlMessage;

public sealed record SetPolarPattern(int Pattern) : ControlMessage;

public sealed record Stats(
    float RttMs,
    float LossPct,
    float JitterMs,
    float BufferMs,
    long Packets,
    long Bytes,
    float DecodeP95Ms = 0,
    float DwellP95Ms = 0,
    float WriteP95Ms = 0) : ControlMessage;

public sealed record Ping(long TsUs) : ControlMessage;

public sealed record Pong(long TsUs) : ControlMessage;

public sealed record ClientInfo(string DeviceName, int BatteryPct, string Transport) : ControlMessage;

public sealed record Bye(string Reason) : ControlMessage;

public sealed record Error(string Message, string? UpgradeHint = null) : ControlMessage;

[JsonSerializable(typeof(ControlMessage))]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(HelloAck))]
[JsonSerializable(typeof(PairConfirm))]
[JsonSerializable(typeof(Resume))]
[JsonSerializable(typeof(SetMute))]
[JsonSerializable(typeof(SetGain))]
[JsonSerializable(typeof(SetBitrate))]
[JsonSerializable(typeof(SetFrameSize))]
[JsonSerializable(typeof(SetNoiseSuppression))]
[JsonSerializable(typeof(SetAgc))]
[JsonSerializable(typeof(SetPolarPattern))]
[JsonSerializable(typeof(Stats))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(Bye))]
[JsonSerializable(typeof(Error))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;