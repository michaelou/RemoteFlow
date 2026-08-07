using System.Text.Json;
using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.Entities;

public sealed class Setting
{
    private Setting()
    {
        Key = string.Empty;
        Value = "null";
    }

    public string Key { get; private set; }

    public string Value { get; private set; }

    public DateTimeOffset ModifiedUtc { get; private set; }

    public static Result<Setting> Create(string? key, string? value, DateTimeOffset? modifiedUtc = null)
    {
        var normalizedKey = DomainValidation.Required(key, 200, "setting.key", out var error);
        return error is not null
            ? Result<Setting>.Failure(error)
            : !IsJson(value)
            ? Result<Setting>.Failure(RemoteFlowError.Validation("setting.value", "The setting value must be valid JSON."))
            : Result<Setting>.Success(new Setting
            {
                Key = normalizedKey!,
                Value = value!,
                ModifiedUtc = DomainValidation.Utc(modifiedUtc ?? DateTimeOffset.UtcNow),
            });
    }

    public Result<Setting> SetValue(string? value, DateTimeOffset? modifiedUtc = null)
    {
        if (!IsJson(value))
        {
            return Result<Setting>.Failure(RemoteFlowError.Validation("setting.value", "The setting value must be valid JSON."));
        }

        Value = value!;
        ModifiedUtc = DomainValidation.Utc(modifiedUtc ?? DateTimeOffset.UtcNow);
        return Result<Setting>.Success(this);
    }

    private static bool IsJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
