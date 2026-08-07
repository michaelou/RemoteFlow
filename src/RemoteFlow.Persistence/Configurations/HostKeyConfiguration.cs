#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class HostKeyConfiguration : IEntityTypeConfiguration<HostKey>
{
    public void Configure(EntityTypeBuilder<HostKey> builder)
    {
        builder.ToTable("HostKeys");
        builder.HasKey(hostKey => hostKey.Id);

        builder.Property(hostKey => hostKey.Id).HasTextGuidConversion();
        builder.Property(hostKey => hostKey.Host).HasMaxLength(255).UseCollation("NOCASE").IsRequired();
        builder.Property(hostKey => hostKey.KeyAlgorithm).HasMaxLength(100).IsRequired();
        builder.Property(hostKey => hostKey.PublicKeyBase64).HasMaxLength(16_384).IsRequired();
        builder.Property(hostKey => hostKey.Sha256Fingerprint).HasMaxLength(200).IsRequired();
        builder.Property(hostKey => hostKey.TrustState).HasConversion<int>();
        builder.Property(hostKey => hostKey.Source).HasConversion<int>();
        builder.Property(hostKey => hostKey.Comment).HasMaxLength(4_000);

        builder.HasIndex(hostKey => new { hostKey.Host, hostKey.Port, hostKey.KeyAlgorithm }).IsUnique();
        builder.HasIndex(hostKey => new { hostKey.Host, hostKey.Port });
    }
}
