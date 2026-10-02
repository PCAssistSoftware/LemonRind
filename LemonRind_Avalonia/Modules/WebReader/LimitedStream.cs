namespace LemonRindAvalonia.Modules.WebReader;

/// <summary>
/// Wraps a stream and throws once more than maxBytes have been read - caps
/// how much a single fetched page can consume, so a malicious or just
/// enormous response can't exhaust memory before the size is known. Only the
/// read path used by SsrfSafeHttpFetcher is implemented; this isn't a
/// general-purpose Stream.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\WebReader\LimitedStream.vb.
/// </summary>
internal class LimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _bytesRead;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        _bytesRead += read;
        if (_bytesRead > maxBytes)
        {
            throw new InvalidOperationException($"Response exceeded the {maxBytes:N0}-byte limit for a fetched page.");
        }
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesReadNow = inner.Read(buffer, offset, count);
        _bytesRead += bytesReadNow;
        if (_bytesRead > maxBytes)
        {
            throw new InvalidOperationException($"Response exceeded the {maxBytes:N0}-byte limit for a fetched page.");
        }
        return bytesReadNow;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
