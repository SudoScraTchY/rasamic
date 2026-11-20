namespace RasaMic.Audio;

public interface ICaptureSource : IDisposable
{
    void Start();
    void Stop();
}