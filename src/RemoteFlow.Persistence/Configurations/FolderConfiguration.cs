#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("Folders");
        builder.HasKey(folder => folder.Id);

        builder.Property(folder => folder.Id).HasTextGuidConversion();
        builder.Property(folder => folder.Name).HasMaxLength(100).IsRequired();
        builder.Property(folder => folder.ParentId).HasTextGuidConversion();
        builder.Property(folder => folder.Path).HasMaxLength(4_096).UseCollation("NOCASE").IsRequired();
        builder.Property(folder => folder.ConcurrencyStamp)
            .HasTextGuidConversion()
            .IsConcurrencyToken();

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(folder => folder.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(folder => folder.Path).IsUnique();
        builder.HasIndex(folder => folder.ParentId);
    }
}
