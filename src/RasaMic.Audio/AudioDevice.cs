namespace RasaMic.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public string Label => IsDefault ? $"{Name} — default" : Name;
    public override string ToString() => Label;
}
