using Amazon.S3.Model;

namespace Worklane.Infrastructure.Services;

/// <summary>
/// Read stream that releases the S3 response when the caller disposes it.
/// </summary>
internal sealed class S3ObjectReadStream : Stream
{
    private readonly GetObjectResponse _response;
    private readonly Stream _inner;
    private bool _disposed;

    public S3ObjectReadStream(GetObjectResponse response)
    {
        _response = response;
        _inner = response.ResponseStream ?? throw new InvalidOperationException("Stored object has no body.");
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _inner.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _response.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        base.Dispose(disposing);
    }
}
