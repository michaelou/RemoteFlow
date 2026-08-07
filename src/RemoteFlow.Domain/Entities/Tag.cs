using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.Entities;

public sealed class Tag
{
    private Tag()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string? ColorHex { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public static Result<Tag> Create(
        IGuidProvider guidProvider,
        string? name,
        string? colorHex = null,
        DateTimeOffset? createdUtc = null)
    {
        var normalizedName = DomainValidation.Required(name, 100, "tag.name", out var error);
        if (error is not null)
        {
            return Result<Tag>.Failure(error);
        }

        var normalizedColor = DomainValidation.ColorHex(colorHex, "tag.color", out error);
        return error is not null
            ? Result<Tag>.Failure(error)
            : Result<Tag>.Success(new Tag
            {
                Id = DomainValidation.NewRequiredGuid(guidProvider),
                Name = normalizedName!,
                ColorHex = normalizedColor,
                CreatedUtc = DomainValidation.Utc(createdUtc ?? DateTimeOffset.UtcNow),
            });
    }

    public Result<Tag> Rename(string? name)
    {
        var normalizedName = DomainValidation.Required(name, 100, "tag.name", out var error);
        if (error is not null)
        {
            return Result<Tag>.Failure(error);
        }

        Name = normalizedName!;
        return Result<Tag>.Success(this);
    }

    public Result<Tag> SetColor(string? colorHex)
    {
        var normalizedColor = DomainValidation.ColorHex(colorHex, "tag.color", out var error);
        if (error is not null)
        {
            return Result<Tag>.Failure(error);
        }

        ColorHex = normalizedColor;
        return Result<Tag>.Success(this);
    }
}
