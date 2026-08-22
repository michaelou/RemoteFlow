using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class BoundedFileSegmentStreamTests
{
    [Fact]
    public async Task ASegmentReadsOnlyItsOwnWindow()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var content = new byte[1000];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        await File.WriteAllBytesAsync(path, content, token);
        try
        {
            await using var segment = new BoundedFileSegmentStream(path, 400, 250);
            using var buffer = new MemoryStream();
            await segment.CopyToAsync(buffer, token);

            Assert.Equal(250, segment.Length);
            Assert.Equal(content.AsSpan(400, 250).ToArray(), buffer.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentSegmentsOverOneFileDoNotShareAPosition()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var content = new byte[900];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(path, content, token);
        try
        {
            // Its own handle per segment is what makes parallel parts possible at all.
            var reads = await Task.WhenAll(Enumerable.Range(0, 3).Select(async index =>
            {
                await using var segment = new BoundedFileSegmentStream(path, index * 300, 300);
                using var buffer = new MemoryStream();
                await segment.CopyToAsync(buffer, token);
                return buffer.ToArray();
            }));

            Assert.Equal(content, reads.SelectMany(read => read).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ASecondStreamFromTheSameFactoryStartsFresh()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8], token);
        try
        {
            // The invariant the part-content factory exists to make structural: a retried part must not be
            // handed the stream the failed attempt already consumed.
            ValueTask<Stream> Factory(CancellationToken _)
            {
                return ValueTask.FromResult<Stream>(new BoundedFileSegmentStream(path, 2, 4));
            }

            var first = await Factory(token);
            byte[] firstBytes;
            await using (first)
            {
                using var buffer = new MemoryStream();
                await first.CopyToAsync(buffer, token);
                firstBytes = buffer.ToArray();
            }

            var second = await Factory(token);
            await using (second)
            {
                using var buffer = new MemoryStream();
                await second.CopyToAsync(buffer, token);
                Assert.Equal(firstBytes, buffer.ToArray());
                Assert.Equal<byte[]>([3, 4, 5, 6], buffer.ToArray());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CountingReadStreamDoesNotDoubleCountASeekAndRewind()
    {
        var token = TestContext.Current.CancellationToken;
        var observed = new List<long>();
        var inner = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        await using var counting = new CountingReadStream(inner, observed.Add);
        var buffer = new byte[8];

        _ = await counting.ReadAsync(buffer, token);

        // S3 reads a seekable part stream once for a checksum and then rewinds it. A running total would
        // report sixteen bytes sent for an eight-byte part.
        _ = counting.Seek(0, SeekOrigin.Begin);
        _ = await counting.ReadAsync(buffer, token);

        Assert.Equal(8, counting.HighWaterMark);
        Assert.Equal([8L, 8L], observed);
    }
}
