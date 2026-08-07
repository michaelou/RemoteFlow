using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

public interface IRecentConnectionStore
{
    Task<RecentConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecentConnection>> ListAsync(int limit, CancellationToken cancellationToken = default);

    Task RecordOpenedAsync(
        Guid connectionId,
        DateTimeOffset openedUtc,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid connectionId, CancellationToken cancellationToken = default);
}
