using System.Numerics;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Application.Services;

/// <summary>Derives the part ladder for a chunked transfer. Deliberately arithmetic and not a setting: a
/// user picking five mebibytes for a five-hundred-gigabyte object needs 95,368 parts and hits S3's cap.
///
/// <c>partSize = Clamp(NextPowerOfTwo(Ceil(total / 8000)), 8 MiB, 1 GiB)</c>, then squeezed into the
/// provider's own floor and ceiling. The eight-mebibyte floor — not S3's five — satisfies both providers
/// with headroom against a slightly misreported size; the 8,000-part budget against a 10,000-part cap
/// absorbs a wrong total without an unrecoverable failure at part 9,999; the one-gibibyte ceiling bounds
/// the cost of retrying a single part.</summary>
public static class ObjectPartPlanner
{
    public const long PartSizeFloor = 8L * 1024 * 1024;

    public const long PartSizeCeiling = 1024L * 1024 * 1024;

    /// <summary>Parts aimed for, against a 10,000-part cap. The gap is the headroom.</summary>
    public const int PartBudget = 8_000;

    /// <summary>The largest object this ladder can address for the given limits. Anything above it is
    /// refused before the first network call rather than partway through part 9,999.</summary>
    public static long MaximumObjectSize(ObjectPartLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var addressable = Math.Min(PartSizeCeiling, limits.MaximumPartSize);
        return addressable > long.MaxValue / limits.MaximumPartCount
            ? long.MaxValue
            : addressable * limits.MaximumPartCount;
    }

    public static long PartSizeFor(long totalBytes, ObjectPartLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        var target = ((totalBytes - 1) / PartBudget) + 1;
        var rounded = (long)BitOperations.RoundUpToPowerOf2((ulong)target);
        var size = Math.Clamp(rounded, PartSizeFloor, PartSizeCeiling);

        // The provider has the last word in both directions, and the ceiling wins the tie: a part above
        // MaximumPartSize is rejected outright, whereas one below MinimumPartSize only matters when there
        // is more than one part, and a final or single part is allowed to be short.
        size = Math.Max(size, limits.MinimumPartSize);
        return Math.Min(size, limits.MaximumPartSize);
    }

    public static SftpResult<ObjectPartPlan> Plan(long totalBytes, ObjectPartLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MinimumPartSize <= 0 || limits.MaximumPartSize < limits.MinimumPartSize ||
            limits.MaximumPartCount < 1)
        {
            return SftpResult<ObjectPartPlan>.Fail(
                SftpError.NotSupported,
                "The provider reported part limits that cannot be satisfied.");
        }

        if (totalBytes <= 0)
        {
            return SftpResult<ObjectPartPlan>.Fail(
                SftpError.InvalidPath,
                "Only an object with a known, positive length can be split into parts.");
        }

        var maximum = MaximumObjectSize(limits);
        if (totalBytes > maximum)
        {
            return SftpResult<ObjectPartPlan>.Fail(
                SftpError.NotSupported,
                $"The object is {totalBytes:N0} bytes, above the {maximum:N0} bytes this provider can " +
                "store in a single object.");
        }

        var partSize = PartSizeFor(totalBytes, limits);
        var count = ((totalBytes - 1) / partSize) + 1;
        if (count > limits.MaximumPartCount)
        {
            return SftpResult<ObjectPartPlan>.Fail(
                SftpError.NotSupported,
                $"The object needs {count:N0} parts, above the provider's limit of " +
                $"{limits.MaximumPartCount:N0}.");
        }

        var parts = new ObjectPart[count];
        for (var index = 0; index < count; index++)
        {
            var offset = index * partSize;
            parts[index] = new ObjectPart(index + 1, offset, Math.Min(partSize, totalBytes - offset));
        }

        return SftpResult<ObjectPartPlan>.Success(new ObjectPartPlan(totalBytes, partSize, parts));
    }
}
