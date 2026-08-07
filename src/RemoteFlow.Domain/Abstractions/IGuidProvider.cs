namespace RemoteFlow.Domain.Abstractions;

public interface IGuidProvider
{
    Guid NewGuid();
}

public sealed class SystemGuidProvider : IGuidProvider
{
    public static SystemGuidProvider Instance { get; } = new();

    private SystemGuidProvider()
    {
    }

    public Guid NewGuid()
    {
        return Guid.CreateVersion7();
    }
}
