using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.TestSupport;

public sealed class FakeSecureRandom(params byte[] values) : ISecureRandom
{
    private readonly byte[] _values = values ?? throw new ArgumentNullException(nameof(values));

    public byte[] GetBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return count <= _values.Length
            ? _values[..count]
            : throw new InvalidOperationException("The fake does not contain enough bytes.");
    }
}
