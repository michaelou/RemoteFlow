using RemoteFlow.Domain.Abstractions;

namespace RemoteFlow.TestSupport;

public sealed class FakeGuidProvider(params Guid[] values) : IGuidProvider
{
    private readonly Queue<Guid> _values = new(values ?? throw new ArgumentNullException(nameof(values)));
    private int _sequence;

    public Guid NewGuid()
    {
        if (_values.TryDequeue(out var value))
        {
            return value == Guid.Empty
                ? throw new InvalidOperationException("Fake GUID values cannot be empty.")
                : value;
        }

        _sequence++;
        return new Guid(_sequence, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
    }
}
