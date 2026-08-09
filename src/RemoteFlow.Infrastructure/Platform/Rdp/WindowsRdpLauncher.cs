using System.Diagnostics;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Platform.Rdp;

/// <summary>Launches a connection in Windows' own Remote Desktop Connection client.</summary>
public sealed class WindowsRdpLauncher : IRdpLauncher
{
    internal const string LaunchDirectoryPrefix = "launch-";
    internal const string RdpDirectoryName = "rdp";

    /// <summary>A launch directory lives for as long as `mstsc` takes to read the file out of it. Anything
    /// still there an hour later belongs to a session that crashed before it could clean up.</summary>
    private static readonly TimeSpan _staleAfter = TimeSpan.FromHours(1);

    private readonly ISystemPlatform _platform;
    private readonly IProcessRunner _processRunner;
    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders;
    private readonly TimeSpan _handoverWindow;
    private readonly Func<string, string?> _readFileVersion;

    public WindowsRdpLauncher(
        ISystemPlatform platform,
        IProcessRunner processRunner,
        IAppPaths paths,
        IClock clock,
        IEnumerable<ICredentialProvider> credentialProviders,
        TimeSpan? handoverWindow = null,
        Func<string, string?>? readFileVersion = null)
    {
        ArgumentNullException.ThrowIfNull(credentialProviders);
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _credentialProviders = [.. credentialProviders];

        // `mstsc` reads the .rdp file and picks up the credential while it starts. Both have to survive
        // that window and neither should outlive it, so the launch waits this long before deleting them.
        _handoverWindow = handoverWindow ?? TimeSpan.FromSeconds(5);
        _readFileVersion = readFileVersion ?? ReadFileVersion;
    }

    public string MissingClientGuidance =>
        "Remote Desktop Connection (mstsc.exe) was not found on this machine. It ships with Windows: " +
        "check that 'Remote Desktop Connection' is present under Windows Tools, or reinstall it from " +
        "Settings > System > Optional features.";

    public async Task<RdpLaunchResult> LaunchAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Protocol != ProtocolType.Rdp)
        {
            return RdpLaunchResult.NotAnRdpConnection(
                $"'{connection.Name}' is a {connection.Protocol} connection, so there is no RDP session to launch.");
        }

        var client = _platform.FindExecutable("mstsc.exe");
        if (client is null)
        {
            return RdpLaunchResult.ClientNotFound(MissingClientGuidance);
        }

        string? launchDirectory = null;
        string? credentialTarget = null;
        var launched = false;
        try
        {
            launchDirectory = Directory.CreateDirectory(Path.Combine(
                RdpDirectory(),
                $"{LaunchDirectoryPrefix}{Guid.NewGuid():N}")).FullName;
            var file = Path.Combine(launchDirectory, "connection.rdp");

            // UTF-16 LE with a BOM is what `mstsc` writes itself, so it is the encoding it is certain to
            // read back for a host or domain that is not plain ASCII.
            await File.WriteAllTextAsync(
                file,
                RdpFileBuilder.Build(connection),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                cancellationToken).ConfigureAwait(false);

            credentialTarget = await TryHandOverCredentialAsync(connection, cancellationToken).ConfigureAwait(false);
            await _processRunner
                .RunAsync(new ProcessLaunchRequest(client, [file]), cancellationToken)
                .ConfigureAwait(false);
            launched = true;
            return RdpLaunchResult.Launched;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RdpLaunchResult.Failed($"Remote Desktop Connection could not be started: {exception.Message}");
        }
        finally
        {
            if (launched)
            {
                await Task.Delay(_handoverWindow, CancellationToken.None).ConfigureAwait(false);
            }

            await RevokeCredentialAsync(credentialTarget).ConfigureAwait(false);
            Delete(launchDirectory);
        }
    }

    public Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _platform.FindExecutable("mstsc.exe");
        IReadOnlyList<RdpClientInfo> clients = client is null
            ? []
            : [new RdpClientInfo("Remote Desktop Connection", client, _readFileVersion(client))];
        return Task.FromResult(clients);
    }

    public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RdpDirectory();
        if (!Directory.Exists(root))
        {
            return Task.CompletedTask;
        }

        var cutoff = _clock.UtcNow - _staleAfter;
        foreach (var directory in Directory.EnumerateDirectories(root, $"{LaunchDirectoryPrefix}*"))
        {
            if (Directory.GetLastWriteTimeUtc(directory) <= cutoff.UtcDateTime)
            {
                Delete(directory);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Puts the stored password where `mstsc` looks for it and nowhere else, returning the
    /// credential target so the launch can take it straight back out again.</summary>
    private async Task<string?> TryHandOverCredentialAsync(Connection connection, CancellationToken cancellationToken)
    {
        // The default is to store nothing and let Windows do the asking, so a connection with no saved
        // RDP password gets no cmdkey entry at all.
        if (connection.Credential.IsEmpty || connection.Credential.Kind != CredentialKind.RdpPassword)
        {
            return null;
        }

        var user = string.IsNullOrWhiteSpace(connection.Rdp.Domain)
            ? connection.Username
            : $"{connection.Rdp.Domain}\\{connection.Username}";
        var cmdkey = _platform.FindExecutable("cmdkey.exe");
        if (cmdkey is null || string.IsNullOrWhiteSpace(connection.Username))
        {
            return null;
        }

        var provider = _credentialProviders.FirstOrDefault(candidate =>
            candidate.IsAvailable &&
            string.Equals(candidate.Name, connection.Credential.StoreProvider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        using var secret = await provider
            .GetAsync(connection.Credential.StoreKey, cancellationToken)
            .ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        var target = $"TERMSRV/{connection.Host}";
        await _processRunner.RunAsync(
            new ProcessLaunchRequest(
                cmdkey,
                [$"/generic:{target}", $"/user:{user}", $"/pass:{new string(secret.Secret.Span)}"]),
            cancellationToken).ConfigureAwait(false);
        return target;
    }

    private async Task RevokeCredentialAsync(string? target)
    {
        if (target is null)
        {
            return;
        }

        var cmdkey = _platform.FindExecutable("cmdkey.exe");
        if (cmdkey is null)
        {
            return;
        }

        try
        {
            await _processRunner
                .RunAsync(new ProcessLaunchRequest(cmdkey, [$"/delete:{target}"]), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A launch that worked should not be reported as a failure because the tidy-up did not. The
            // entry is generic and scoped to one host; the next launch overwrites it.
        }
    }

    private string RdpDirectory()
    {
        return Path.Combine(_paths.CacheDirectory, RdpDirectoryName);
    }

    private static void Delete(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere. The next startup sweep collects it.
        }
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
