#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.HasKey(tag => tag.Id);

        builder.Property(tag => tag.Id).HasTextGuidConversion();
        builder.Property(tag => tag.Name).HasMaxLength(100).UseCollation("NOCASE").IsRequired();
        builder.Property(tag => tag.ColorHex).HasMaxLength(7);

        builder.HasIndex(tag => tag.Name).IsUnique();
    }
}
