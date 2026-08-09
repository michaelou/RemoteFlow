namespace RemoteFlow.UI.Services;

/// <summary>Reads THIRD-PARTY-NOTICES.md out of this assembly. It is embedded rather than read from disk
/// because attribution has to survive packaging: a user with an extracted portable zip has the binary and
/// nothing else.</summary>
public static class ThirdPartyNotices
{
    private const string _resourceName = "RemoteFlow.UI.THIRD-PARTY-NOTICES.md";

    private static readonly Lazy<string> _text = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The notices, or an explanation of why they are absent. Never throws: the about box is
    /// where someone goes when something is already wrong.</summary>
    public static string Text => _text.Value;

    private static string Load()
    {
        try
        {
            using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream(_resourceName);
            if (stream is null)
            {
                return NotEmbedded();
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? NotEmbedded() : text;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or NotSupportedException)
        {
            return $"The third-party notices could not be read from this build: {exception.Message}";
        }
    }

    private static string NotEmbedded()
    {
        var assembly = typeof(ThirdPartyNotices).Assembly.GetName().Name;
        return $"""
            The third-party notices are not embedded in this build of {assembly}.

            Generate them from the repository with:

                pwsh ./scripts/generate-notices.ps1

            and read THIRD-PARTY-NOTICES.md at the repository root.
            """;
    }
}
