#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class ConnectionTagConfiguration : IEntityTypeConfiguration<ConnectionTag>
{
    public void Configure(EntityTypeBuilder<ConnectionTag> builder)
    {
        builder.ToTable("ConnectionTags");
        builder.HasKey(item => new { item.ConnectionId, item.TagId });

        builder.Property(item => item.ConnectionId).HasTextGuidConversion();
        builder.Property(item => item.TagId).HasTextGuidConversion();

        builder.HasOne<Connection>()
            .WithMany(connection => connection.Tags)
            .HasForeignKey(item => item.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(item => item.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(item => new { item.TagId, item.ConnectionId });
    }
}
