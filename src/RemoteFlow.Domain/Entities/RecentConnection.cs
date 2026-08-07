using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.Entities;

public sealed class RecentConnection
{
    private RecentConnection()
    {
    }

    public Guid ConnectionId { get; private set; }

    public DateTimeOffset LastOpenedUtc { get; private set; }

    public int OpenCount { get; private set; }

    public static Result<RecentConnection> Create(Guid connectionId, DateTimeOffset? openedUtc = null)
    {
        return connectionId == Guid.Empty
            ? Result<RecentConnection>.Failure(RemoteFlowError.Validation(
                "recent_connection.connection_id",
                "The connection ID is required."))
            : Result<RecentConnection>.Success(new RecentConnection
            {
                ConnectionId = connectionId,
                LastOpenedUtc = DomainValidation.Utc(openedUtc ?? DateTimeOffset.UtcNow),
                OpenCount = 1,
            });
    }

    public RecentConnection RecordOpened(DateTimeOffset? openedUtc = null)
    {
        checked
        {
            OpenCount++;
        }

        LastOpenedUtc = DomainValidation.Utc(openedUtc ?? DateTimeOffset.UtcNow);
        return this;
    }
}
