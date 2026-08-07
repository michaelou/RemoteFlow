namespace RemoteFlow.Domain.Entities;

public sealed class ConnectionTag
{
    private ConnectionTag()
    {
    }

    public ConnectionTag(Guid connectionId, Guid tagId)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("The connection ID is required.", nameof(connectionId));
        }

        if (tagId == Guid.Empty)
        {
            throw new ArgumentException("The tag ID is required.", nameof(tagId));
        }

        ConnectionId = connectionId;
        TagId = tagId;
    }

    public Guid ConnectionId { get; private set; }

    public Guid TagId { get; private set; }
}
