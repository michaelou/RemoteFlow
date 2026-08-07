#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class RecentConnectionConfiguration : IEntityTypeConfiguration<RecentConnection>
{
    public void Configure(EntityTypeBuilder<RecentConnection> builder)
    {
        builder.ToTable("RecentConnections");
        builder.HasKey(recent => recent.ConnectionId);

        builder.Property(recent => recent.ConnectionId).HasTextGuidConversion();

        builder.HasOne<Connection>()
            .WithOne()
            .HasForeignKey<RecentConnection>(recent => recent.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(recent => recent.LastOpenedUtc).IsDescending();
    }
}
