using Microsoft.EntityFrameworkCore;

namespace RemoteFlow.Persistence;

public sealed class RemoteFlowDbContextFactory(string dataDirectory) : IDbContextFactory<RemoteFlowDbContext>
{
    private readonly DbContextOptions<RemoteFlowDbContext> _options = RemoteFlowDatabase.CreateOptions(dataDirectory);

    public RemoteFlowDbContext CreateDbContext()
    {
        return new RemoteFlowDbContext(_options);
    }

    public Task<RemoteFlowDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
