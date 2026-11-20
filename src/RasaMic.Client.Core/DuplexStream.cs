namespace RasaMic.Client.Core;

/// <summary>
/// Combines separate read/write sides (e.g. Android BluetoothSocket streams)
/// into one duplex Stream for the tunnel transport.
/// </summary>
public class DuplexStream(Stream read, Stream write) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => write.Flush();
    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => read.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => write.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => write.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            read.Dispose();
            write.Dispose();
        }
        base.Dispose(disposing);
    }
}