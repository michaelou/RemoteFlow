using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RemoteFlow.Persistence.Configurations;

internal static class PropertyBuilderExtensions
{
    private static readonly ValueConverter<Guid, string> _guidConverter = new(
        value => value.ToString("D").ToLowerInvariant(),
        value => Guid.Parse(value));

    private static readonly ValueConverter<Guid?, string?> _nullableGuidConverter = new(
        value => value.HasValue ? value.Value.ToString("D").ToLowerInvariant() : null,
        value => value == null ? null : Guid.Parse(value));

    internal static PropertyBuilder<Guid> HasTextGuidConversion(this PropertyBuilder<Guid> builder)
    {
        return builder
            .HasConversion(_guidConverter)
            .HasColumnType("TEXT")
            .HasMaxLength(36);
    }

    internal static PropertyBuilder<Guid?> HasTextGuidConversion(this PropertyBuilder<Guid?> builder)
    {
        return builder
            .HasConversion(_nullableGuidConverter)
            .HasColumnType("TEXT")
            .HasMaxLength(36);
    }
}
