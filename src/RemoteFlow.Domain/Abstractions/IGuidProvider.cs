namespace RemoteFlow.Domain.Abstractions;

public interface IGuidProvider
{
    Guid NewGuid();
}

public sealed class SystemGuidProvider : IGuidProvider
{
    private readonly Lock _lock = new();
    private Guid _last;

    public static SystemGuidProvider Instance { get; } = new();

    private SystemGuidProvider()
    {
    }

    public Guid NewGuid()
    {
        lock (_lock)
        {
            var candidate = Guid.CreateVersion7();
            if (candidate.CompareTo(_last) <= 0)
            {
                candidate = Next(_last);
            }

            _last = candidate;
            return candidate;
        }
    }

    private static Guid Next(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes, bigEndian: true, out _);
        for (var index = bytes.Length - 1; index >= 9; index--)
        {
            bytes[index]++;
            if (bytes[index] != 0)
            {
                return new Guid(bytes, bigEndian: true);
            }
        }

        if ((bytes[8] & 0x3F) != 0x3F)
        {
            bytes[8]++;
            return new Guid(bytes, bigEndian: true);
        }

        throw new InvalidOperationException("The UUID v7 sequence was exhausted within one millisecond.");
    }
}
