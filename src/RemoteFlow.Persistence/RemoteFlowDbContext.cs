using Microsoft.EntityFrameworkCore;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence;

public sealed class RemoteFlowDbContext(DbContextOptions<RemoteFlowDbContext> options) : DbContext(options)
{
    public DbSet<Connection> Connections => Set<Connection>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ConnectionTag> ConnectionTags => Set<ConnectionTag>();

    public DbSet<HostKey> HostKeys => Set<HostKey>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<RecentConnection> RecentConnections => Set<RecentConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.ApplyConfigurationsFromAssembly(typeof(RemoteFlowDbContext).Assembly);
    }
}
