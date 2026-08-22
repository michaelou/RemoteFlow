using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>One part of a chunked transfer: an upload part on the way out, a ranged read on the way in.
/// Part numbers are 1-based because both providers number them that way, and the offset is absolute so a
/// retried part re-reads exactly the bytes it was given rather than wherever a shared stream happened to
/// be left.</summary>
public readonly record struct ObjectPart(int PartNumber, long Offset, long Length);

/// <summary>Every part of one object, in order, with the size the ladder settled on.</summary>
public sealed record ObjectPartPlan(long TotalBytes, long PartSize, IReadOnlyList<ObjectPart> Parts);

/// <summary>A provider's real part limits. Taken from an <see cref="IObjectUpload"/> for an upload, and
/// from <see cref="ObjectTransferOptions.PartLimits"/> for a download, which has no server-side session to
/// ask.</summary>
public sealed record ObjectPartLimits(long MinimumPartSize, long MaximumPartSize, int MaximumPartCount)
{
    /// <summary>S3's published limits, which Azure's are wider than in every dimension that matters here.
    /// </summary>
    public static ObjectPartLimits Default { get; } = new(5L * 1024 * 1024, 5L * 1024 * 1024 * 1024, 10_000);

    public static ObjectPartLimits From(IObjectUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return new ObjectPartLimits(upload.MinimumPartSize, upload.MaximumPartSize, upload.MaximumPartCount);
    }
}

/// <summary>A sibling of <see cref="TransferOptions"/> rather than an extension of it: <c>BufferSize</c>
/// and <c>MaxConcurrentTransfers</c> mean different things once one file is many parallel requests.
///
/// Peak managed memory is <see cref="MaxPartsInFlight"/> times <see cref="CopyBufferSize"/> — four times
/// one mebibyte by default — whether the object is four gigabytes or five hundred. Nothing buffers a part.
/// </summary>
public sealed record ObjectTransferOptions
{
    /// <summary>At or below this, the single-request path. Hand-rolling a three-round-trip multipart
    /// upload for a small object buys nothing.</summary>
    public long SingleShotThreshold { get; init; } = 16L * 1024 * 1024;

    /// <summary>Four, not eight: the transfer queue already runs three transfers at once, so this is
    /// twelve concurrent HTTP requests — enough to saturate a link without starving a home uplink.
    /// </summary>
    public int MaxPartsInFlight { get; init; } = 4;

    public int CopyBufferSize { get; init; } = 1024 * 1024;

    /// <summary>Four engine attempts per part, on top of the one fast in-request retry each SDK is tuned
    /// to. Bounded at eight attempts total, and explainable.</summary>
    public int MaxAttemptsPerPart { get; init; } = 4;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A function so a test can pin it and assert exact delays against a
    /// <c>FakeTimeProvider</c>.</summary>
    public Func<int, TimeSpan> Jitter { get; init; } = DefaultJitter;

    /// <summary>250 ms. The transfer queue's progress sink keeps only the latest value per 100 ms tick,
    /// so reporting faster than this is pure discard.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounds the abort on a cancelled or failed upload, so a dead network cannot hang a cancel.
    /// </summary>
    public TimeSpan AbortTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The limits used to size download ranges, and to reject an impossible object before the
    /// first network call. An upload uses its session's real limits instead.</summary>
    public ObjectPartLimits PartLimits { get; init; } = ObjectPartLimits.Default;

    public TimeSpan RetryDelayFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var exponent = Math.Min(attempt - 1, 16);
        return (InitialRetryDelay * Math.Pow(2, exponent)) + Jitter(attempt);
    }

    /// <summary>Validated where <c>TransferEngine</c> validates its own: in the engine's
    /// constructor, so a bad option fails at construction rather than mid-transfer.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPartsInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CopyBufferSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttemptsPerPart, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SingleShotThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ProgressInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(AbortTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(Jitter);
        ArgumentNullException.ThrowIfNull(PartLimits);
    }

    private static TimeSpan DefaultJitter(int attempt)
    {
        _ = attempt;
        return TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    }
}
