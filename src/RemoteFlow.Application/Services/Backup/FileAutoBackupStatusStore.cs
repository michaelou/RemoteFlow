using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Keeps the last run's outcome in a small file beside the database rather than in the settings
/// table. Settings are exported into every archive, so a status row would travel with them and a Replace
/// import would install another machine's "last run succeeded" — the one claim this feature cannot afford
/// to get wrong. It lives in the data directory, not the cache, so clearing caches does not make the page
/// report that automatic backup has never run.</summary>
public sealed class FileAutoBackupStatusStore(IAppPaths paths) : IAutoBackupStatusStore, IDisposable
{
    public const string FileName = "auto-backup-status.json";

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IAppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Path => System.IO.Path.Combine(_paths.DataDirectory, FileName);

    public async Task<AutoBackupStatus?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            await using var stream = File.OpenRead(Path);
            return await JsonSerializer
                .DeserializeAsync<AutoBackupStatus>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable status file is a lost record, not a reason to stop making backups.
            return null;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task WriteAsync(AutoBackupStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            // Written aside and moved into place, so a crash mid-write leaves the previous status intact
            // rather than a truncated file that reads as "never run".
            var temporary = Path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer
                    .SerializeAsync(stream, status, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the record of a backup is worth far less than the backup itself, which is already written.
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
