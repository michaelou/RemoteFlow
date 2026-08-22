namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>One AWS region: the code that goes in the connection, and the name the console shows.</summary>
public sealed record S3Region(string Code, string DisplayName)
{
    public string Label => $"{Code} — {DisplayName}";
}

/// <summary>The AWS regions, offered as suggestions rather than as the only allowed answers.
///
/// A list, because the region is easy to get half-right — <c>eu-west</c> is not a region, and the only
/// thing that says so is a DNS failure at connect time, long after the mistake. It is a suggestion list
/// and not a closed set because the field is also used by S3-compatible services, where the region is
/// whatever that deployment calls it: MinIO commonly wants <c>us-east-1</c>, Cloudflare R2 wants
/// <c>auto</c>, and Backblaze B2 wants its own. Refusing those would break the compatibility this
/// protocol exists to support.
///
/// Hand-maintained, and deliberately so: there is no offline source for it, and a network call to
/// enumerate regions would need credentials the user is still in the middle of typing.</summary>
public static class S3Regions
{
    public static IReadOnlyList<S3Region> All { get; } =
    [
        new("us-east-1", "US East (N. Virginia)"),
        new("us-east-2", "US East (Ohio)"),
        new("us-west-1", "US West (N. California)"),
        new("us-west-2", "US West (Oregon)"),
        new("af-south-1", "Africa (Cape Town)"),
        new("ap-east-1", "Asia Pacific (Hong Kong)"),
        new("ap-south-1", "Asia Pacific (Mumbai)"),
        new("ap-south-2", "Asia Pacific (Hyderabad)"),
        new("ap-northeast-1", "Asia Pacific (Tokyo)"),
        new("ap-northeast-2", "Asia Pacific (Seoul)"),
        new("ap-northeast-3", "Asia Pacific (Osaka)"),
        new("ap-southeast-1", "Asia Pacific (Singapore)"),
        new("ap-southeast-2", "Asia Pacific (Sydney)"),
        new("ap-southeast-3", "Asia Pacific (Jakarta)"),
        new("ap-southeast-4", "Asia Pacific (Melbourne)"),
        new("ca-central-1", "Canada (Central)"),
        new("ca-west-1", "Canada West (Calgary)"),
        new("eu-central-1", "Europe (Frankfurt)"),
        new("eu-central-2", "Europe (Zurich)"),
        new("eu-north-1", "Europe (Stockholm)"),
        new("eu-south-1", "Europe (Milan)"),
        new("eu-south-2", "Europe (Spain)"),
        new("eu-west-1", "Europe (Ireland)"),
        new("eu-west-2", "Europe (London)"),
        new("eu-west-3", "Europe (Paris)"),
        new("il-central-1", "Israel (Tel Aviv)"),
        new("me-central-1", "Middle East (UAE)"),
        new("me-south-1", "Middle East (Bahrain)"),
        new("sa-east-1", "South America (São Paulo)"),
        new("cn-north-1", "China (Beijing)"),
        new("cn-northwest-1", "China (Ningxia)"),
        new("us-gov-east-1", "AWS GovCloud (US-East)"),
        new("us-gov-west-1", "AWS GovCloud (US-West)"),
    ];

    public static bool IsKnown(string? code)
    {
        return !string.IsNullOrWhiteSpace(code) &&
            All.Any(region => string.Equals(region.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The nearest known regions to something that is not one, so the warning can name the fix
    /// rather than only the mistake. <c>eu-west</c> comes back as <c>eu-west-1</c>, <c>eu-west-2</c>,
    /// <c>eu-west-3</c>.</summary>
    public static IReadOnlyList<string> Suggest(string? code, int limit = 3)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return [];
        }

        var typed = code.Trim();
        return [.. All
            .Where(region => region.Code.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Select(region => region.Code)
            .Take(limit)];
    }
}
