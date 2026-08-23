using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Application.Services;

/// <summary>The last local folder, kept in the settings store beside the window layout.
///
/// A removable drive is the case that makes the existence check on recall non-negotiable: a pane rooted at
/// a path on an ejected stick would open on an error banner rather than on a folder, every launch, until
/// the user noticed the path box and retyped one.</summary>
public sealed class SettingsLocalFolderMemory(ISettingsStore settings) : ILocalFolderMemory
{
    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<string?> RecallAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _settings.Get(SettingKeys.LastLocalFolder, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored) || !Directory.Exists(stored) ? null : stored;
    }

    public Task RememberAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _settings.Set(SettingKeys.LastLocalFolder, path, cancellationToken);
    }
}
