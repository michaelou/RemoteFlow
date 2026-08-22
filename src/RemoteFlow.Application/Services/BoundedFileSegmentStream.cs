namespace RemoteFlow.Application.Services;

/// <summary>A read-only, seekable window over one region of a file, with its own file handle.
///
/// This is what keeps a chunked upload's memory flat: each in-flight part streams straight off disk
/// through the copy buffer, so peak managed memory is <c>MaxPartsInFlight × CopyBufferSize</c> whether the
/// object is four gigabytes or five hundred. Its own handle, because parts run concurrently and a shared
/// <see cref="FileStream"/> has one position.</summary>
public sealed class BoundedFileSegmentStream : Stream
{
    private readonly FileStream _file;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    public BoundedFileSegmentStream(string path, long offset, long length, int bufferSize = 81_920)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _offset = offset;
        _length = length;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        if (offset > _file.Length)
        {
            _file.Dispose();
            throw new ArgumentOutOfRangeException(nameof(offset), "The segment starts past the end of the file.");
        }

        _file.Position = offset;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var window = Window(buffer.Length);
        if (window == 0)
        {
            return 0;
        }

        var read = _file.Read(buffer[..window]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var window = Window(buffer.Length);
        if (window == 0)
        {
            return 0;
        }

        var read = await _file.ReadAsync(buffer[..window], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = Math.Min(target, _length);
        _file.Position = _offset + _position;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("A file segment is read-only.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("A file segment is read-only.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Window(int requested)
    {
        var remaining = _length - _position;
        return remaining <= 0 ? 0 : (int)Math.Min(requested, remaining);
    }
}

/// <summary>Reports read progress as <c>Math.Max(high, Position)</c> rather than as a running total.
///
/// Cumulative counting would double-count: an S3 upload reads the seekable part stream once to compute a
/// checksum and then rewinds it, so the same bytes go past twice. The high-water mark is the honest
/// number, and it also makes a retried part's progress monotonic for free.</summary>
public sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _observed;
    private long _high;

    public CountingReadStream(Stream inner, Action<long> observed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(observed);
        if (!inner.CanSeek)
        {
            throw new ArgumentException("The counted stream has to be seekable.", nameof(inner));
        }

        _inner = inner;
        _observed = observed;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <summary>The furthest the reader has ever got, which is what has actually been sent.</summary>
    public long HighWaterMark => Volatile.Read(ref _high);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var read = _inner.Read(buffer, offset, count);
        Observe();
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Observe();
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe();
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _inner.Seek(offset, origin);
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("A counted read stream is read-only.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("A counted read stream is read-only.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Observe()
    {
        var position = _inner.Position;
        if (position > Volatile.Read(ref _high))
        {
            Volatile.Write(ref _high, position);
        }

        _observed(Volatile.Read(ref _high));
    }
}
