using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Infrastructure.Platform.Rdp;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.ViewModels.Connections;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class WindowsRdpLauncherTests : IDisposable
{
    private const string _password = "TOP_SECRET_RDP_PASSWORD";

    private readonly TempPaths _paths = new();

    public void Dispose()
    {
        _paths.Dispose();
    }

    [Fact]
    public async Task GeneratedFileIsExactAndCarriesNoPasswordInAnyEncoding()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);

        var result = await launcher.LaunchAsync(CreateConnection(), token);

        Assert.True(result.Succeeded);
        Assert.Equal(
            string.Join("\r\n",
            [
                "full address:s:rdp.example.test:3390",
                "username:s:operator",
                "domain:s:CORP",
                "screen mode id:i:2",
                "desktopwidth:i:1920",
                "desktopheight:i:1080",
                "use multimon:i:1",
                "redirectclipboard:i:1",
                "drivestoredirect:s:*",
                "audiomode:i:0",
                "authentication level:i:2",
                "prompt for credentials:i:0",
                string.Empty,
            ]),
            runner.CapturedRdpText);

        // The text assertion alone would miss a password hidden by the UTF-16 encoding, so the raw
        // bytes are searched for it too.
        Assert.DoesNotContain(_password, runner.CapturedRdpText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", runner.CapturedRdpText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("51:b:", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.False(Contains(runner.CapturedRdpBytes!, Encoding.UTF8.GetBytes(_password)));
        Assert.False(Contains(runner.CapturedRdpBytes!, Encoding.Unicode.GetBytes(_password)));
    }

    [Fact]
    public async Task MstscArgvIsTheGeneratedFileAndNothingElse()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);

        _ = await launcher.LaunchAsync(CreateConnection(), token);

        var launch = Assert.Single(
            runner.Requests,
            request => request.FileName.EndsWith("mstsc.exe", StringComparison.OrdinalIgnoreCase));
        var file = Assert.Single(launch.Arguments);
        Assert.Equal("connection.rdp", Path.GetFileName(file));
        Assert.StartsWith(Path.Combine(_paths.CacheDirectory, "rdp"), file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchFileIsGoneOnceTheClientHasIt()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);

        _ = await launcher.LaunchAsync(CreateConnection(), token);

        var file = runner.Requests
            .Single(request => request.FileName.EndsWith("mstsc.exe", StringComparison.OrdinalIgnoreCase))
            .Arguments[0];
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task StartupSweepCollectsCrashedLaunchesAndLeavesLiveOnesAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var launcher = CreateLauncher(new RecordingProcessRunner(), out _, clock);
        var root = Directory.CreateDirectory(Path.Combine(_paths.CacheDirectory, "rdp"));
        var crashed = Directory.CreateDirectory(Path.Combine(root.FullName, "launch-crashed"));
        var live = Directory.CreateDirectory(Path.Combine(root.FullName, "launch-live"));
        var unrelated = Directory.CreateDirectory(Path.Combine(root.FullName, "notes"));
        await File.WriteAllTextAsync(Path.Combine(crashed.FullName, "connection.rdp"), "stale", token);
        Directory.SetLastWriteTimeUtc(crashed.FullName, clock.UtcNow.AddDays(-1).UtcDateTime);
        Directory.SetLastWriteTimeUtc(live.FullName, clock.UtcNow.AddMinutes(-1).UtcDateTime);

        await launcher.SweepStaleFilesAsync(token);

        Assert.False(Directory.Exists(crashed.FullName));
        Assert.True(Directory.Exists(live.FullName));
        Assert.True(Directory.Exists(unrelated.FullName));
    }

    [Fact]
    public async Task StoredPasswordIsHandedToWindowsAndTakenBackAfterwards()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);

        _ = await launcher.LaunchAsync(CreateConnection(), token);

        var cmdkey = runner.Requests
            .Where(request => request.FileName.EndsWith("cmdkey.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2, cmdkey.Length);
        Assert.Equal(
            ["/generic:TERMSRV/rdp.example.test", "/user:CORP\\operator", $"/pass:{_password}"],
            cmdkey[0].Arguments);
        Assert.Equal(["/delete:TERMSRV/rdp.example.test"], cmdkey[1].Arguments);

        // The handover has to bracket the launch: added before `mstsc` starts, removed after.
        var order = runner.Requests.Select(request => Path.GetFileName(request.FileName)).ToArray();
        Assert.Equal(["cmdkey.exe", "mstsc.exe", "cmdkey.exe"], order);
    }

    [Fact]
    public async Task WithNothingStoredWindowsIsLeftToAskAndNoCredentialIsCreated()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);
        var connection = CreateConnection(withStoredPassword: false);

        var result = await launcher.LaunchAsync(connection, token);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            runner.Requests,
            request => request.FileName.EndsWith("cmdkey.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OptionsReachTheGeneratedFileOneByOne()
    {
        var token = TestContext.Current.CancellationToken;
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);
        var connection = CreateConnection(withStoredPassword: false);
        var rdp = RdpOptions.Default();
        _ = rdp.Configure(
            domain: null,
            fullScreen: false,
            width: null,
            height: null,
            multimon: false,
            redirectClipboard: false,
            redirectDrives: false);
        _ = connection.SetOptions(SshOptions.Default(), SftpOptions.Default(), rdp, SystemGuidProvider.Instance);

        _ = await launcher.LaunchAsync(connection, token);

        Assert.Contains("screen mode id:i:1", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.Contains("use multimon:i:0", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.Contains("redirectclipboard:i:0", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.Contains("drivestoredirect:s:\r\n", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.DoesNotContain("desktopwidth", runner.CapturedRdpText, StringComparison.Ordinal);
        Assert.DoesNotContain("domain:s:", runner.CapturedRdpText, StringComparison.Ordinal);
    }

    /// <summary>The two halves of the feature meeting in the middle: what a person ticks in the editor is
    /// what the client is eventually handed. Neither the view-model test nor the launcher test catches an
    /// option that is saved but never rendered, or rendered from a field the editor never writes.</summary>
    [AvaloniaFact]
    public async Task WhatIsTickedInTheEditorIsWhatTheClientIsHanded()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(token);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var connections = new ConnectionRepository(database.Factory);
        var unitOfWork = new UnitOfWork(database.Factory);
        var guids = SystemGuidProvider.Instance;
        var provider = new RecordingCredentialProvider();
        var connectionService = new ConnectionService(
            connections,
            new RecentConnectionStore(database.Factory),
            [provider],
            unitOfWork,
            guids,
            clock);
        var editor = new ConnectionEditorViewModel(
            connectionService,
            connections,
            new ConnectionCredentialService(
                connections,
                new SingleProviderSelector(provider),
                [provider],
                unitOfWork,
                guids,
                clock),
            new FolderRepository(database.Factory),
            new TagRepository(database.Factory),
            new TagService(new TagRepository(database.Factory), connections, unitOfWork, guids, clock));
        await editor.InitializeAsync(null, token);
        editor.Name = "Jump box";
        editor.Host = "rdp.example.test";
        editor.Port = 3390;
        editor.Protocol = ProtocolType.Rdp;
        editor.Username = "operator";
        editor.RdpDomain = "CORP";
        editor.RdpFullScreen = true;
        editor.RdpMultimon = true;
        editor.RdpRedirectClipboard = true;
        editor.RdpRedirectDrives = true;
        editor.SelectedRdpResolution = ConnectionEditorViewModel.RdpResolutionChoices
            .Single(choice => choice.Label == "1920 × 1080");

        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);
        var saved = Assert.Single(await connections.ListAsync(token));

        Assert.True((await launcher.LaunchAsync(saved, token)).Succeeded);
        Assert.Equal(
            string.Join("\r\n",
            [
                "full address:s:rdp.example.test:3390",
                "username:s:operator",
                "domain:s:CORP",
                "screen mode id:i:2",
                "desktopwidth:i:1920",
                "desktopheight:i:1080",
                "use multimon:i:1",
                "redirectclipboard:i:1",
                "drivestoredirect:s:*",
                "audiomode:i:0",
                "authentication level:i:2",
                "prompt for credentials:i:0",
                string.Empty,
            ]),
            runner.CapturedRdpText);
    }

    [Fact]
    public async Task DetectionReportsMstscWithItsVersion()
    {
        var launcher = CreateLauncher(new RecordingProcessRunner(), out _);

        var clients = await launcher.DetectClientsAsync(TestContext.Current.CancellationToken);

        var client = Assert.Single(clients);
        Assert.Equal("Remote Desktop Connection", client.Name);
        Assert.Equal("C:\\Windows\\System32\\mstsc.exe", client.Path);
        Assert.Equal("10.0.26100.1", client.Version);
        Assert.Equal("Remote Desktop Connection 10.0.26100.1", client.Description);
    }

    [Fact]
    public async Task WithNoClientInstalledDetectionIsEmptyAndLaunchExplainsWhatToDo()
    {
        var token = TestContext.Current.CancellationToken;
        var platform = new FakePlatform(OperatingSystemFamily.Windows);
        var launcher = new WindowsRdpLauncher(
            platform,
            new RecordingProcessRunner(),
            _paths,
            new FakeClock(DateTimeOffset.UtcNow),
            [],
            TimeSpan.Zero);

        Assert.Empty(await launcher.DetectClientsAsync(token));
        var result = await launcher.LaunchAsync(CreateConnection(), token);

        Assert.Equal(RdpLaunchStatus.ClientNotFound, result.Status);
        Assert.Contains("mstsc.exe", result.Message, StringComparison.Ordinal);
        Assert.Equal(launcher.MissingClientGuidance, result.Message);
    }

    [Fact]
    public async Task ANonRdpConnectionIsTurnedAwayWithoutStartingAnything()
    {
        var runner = new RecordingProcessRunner();
        var launcher = CreateLauncher(runner, out _);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Shell",
            "ssh.example.test",
            ProtocolType.Ssh,
            DateTimeOffset.UtcNow).Value;

        var result = await launcher.LaunchAsync(connection, TestContext.Current.CancellationToken);

        Assert.Equal(RdpLaunchStatus.NotAnRdpConnection, result.Status);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task AClientThatRefusesToStartComesBackAsAResultNotAnException()
    {
        var runner = new RecordingProcessRunner { Failure = new InvalidOperationException("mstsc is missing a DLL") };
        var launcher = CreateLauncher(runner, out _);

        var result = await launcher.LaunchAsync(
            CreateConnection(withStoredPassword: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(RdpLaunchStatus.Failed, result.Status);
        Assert.Contains("mstsc is missing a DLL", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnMacOsAndLinuxTheLauncherSaysSoAndNamesAClientToUse()
    {
        var token = TestContext.Current.CancellationToken;
        var mac = new UnsupportedRdpLauncher(new FakePlatform(OperatingSystemFamily.MacOs));
        var linux = new UnsupportedRdpLauncher(new FakePlatform(OperatingSystemFamily.Linux));

        var macResult = await mac.LaunchAsync(CreateConnection(withStoredPassword: false), token);
        var linuxResult = await linux.LaunchAsync(CreateConnection(withStoredPassword: false), token);

        Assert.Equal(RdpLaunchStatus.UnsupportedPlatform, macResult.Status);
        Assert.Contains("Windows App", macResult.Message, StringComparison.Ordinal);
        Assert.Equal(RdpLaunchStatus.UnsupportedPlatform, linuxResult.Status);
        Assert.Contains("FreeRDP", linuxResult.Message, StringComparison.Ordinal);
        Assert.Empty(await linux.DetectClientsAsync(token));
    }

    private WindowsRdpLauncher CreateLauncher(
        RecordingProcessRunner runner,
        out RecordingCredentialProvider provider,
        FakeClock? clock = null)
    {
        var platform = new FakePlatform(OperatingSystemFamily.Windows);
        platform.Executables["mstsc.exe"] = "C:\\Windows\\System32\\mstsc.exe";
        platform.Executables["cmdkey.exe"] = "C:\\Windows\\System32\\cmdkey.exe";
        provider = new RecordingCredentialProvider();
        return new WindowsRdpLauncher(
            platform,
            runner,
            _paths,
            clock ?? new FakeClock(DateTimeOffset.UtcNow),
            [provider],
            TimeSpan.Zero,
            _ => "10.0.26100.1");
    }

    private static Connection CreateConnection(bool withStoredPassword = true)
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Jump box",
            "rdp.example.test",
            3390,
            ProtocolType.Rdp,
            DateTimeOffset.UtcNow).Value;
        _ = connection.SetDetails(
            "operator",
            AuthMethod.Password,
            null,
            EnvironmentKind.Production,
            null,
            SystemGuidProvider.Instance);
        var rdp = RdpOptions.Default();
        _ = rdp.Configure(
            domain: "CORP",
            fullScreen: true,
            width: 1_920,
            height: 1_080,
            multimon: true,
            redirectClipboard: true,
            redirectDrives: true);
        _ = connection.SetOptions(SshOptions.Default(), SftpOptions.Default(), rdp, SystemGuidProvider.Instance);
        if (withStoredPassword)
        {
            _ = connection.SetCredential(
                CredentialRef.Create(
                    CredentialKind.RdpPassword,
                    RecordingCredentialProvider.StoreKey,
                    RecordingCredentialProvider.ProviderName).Value,
                SystemGuidProvider.Instance);
        }

        return connection;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public TempPaths()
        {
            CacheDirectory = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(),
                "remoteflow-rdp-tests",
                Guid.NewGuid().ToString("N"))).FullName;
        }

        public string ConfigDirectory => CacheDirectory;

        public string DataDirectory => CacheDirectory;

        public string CacheDirectory { get; }

        public string LogDirectory => CacheDirectory;

        public void EnsureDirectories()
        {
            _ = Directory.CreateDirectory(CacheDirectory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A test machine that cannot clean up its own temp directory is not a test failure.
            }
        }
    }

    /// <summary>Reads the generated file at the moment the client is handed it, which is the only moment
    /// it exists — the launch deletes it on the way out.</summary>
    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public string CapturedRdpText { get; private set; } = string.Empty;

        public byte[]? CapturedRdpBytes { get; private set; }

        public Exception? Failure { get; init; }

        public async Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.FileName.EndsWith("mstsc.exe", StringComparison.OrdinalIgnoreCase))
            {
                CapturedRdpBytes = await File.ReadAllBytesAsync(request.Arguments[0], cancellationToken);
                CapturedRdpText = await File.ReadAllTextAsync(request.Arguments[0], cancellationToken);
                if (Failure is not null)
                {
                    throw Failure;
                }
            }
        }
    }

    private sealed class SingleProviderSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    private sealed class RecordingCredentialProvider : ICredentialProvider
    {
        public const string ProviderName = "test-store";
        public const string StoreKey = "remoteflow/connection/rdp-password";

        public string Name => ProviderName;

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(
                storeKey == StoreKey ? new SecretHandle(_password) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform(OperatingSystemFamily operatingSystem) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem { get; } = operatingSystem;

        public string CurrentDirectory => "C:\\work";

        public string HomeDirectory => "C:\\Users\\test";

        public Dictionary<string, string> Executables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name)
        {
            return null;
        }

        public string? FindExecutable(string name)
        {
            return Executables.GetValueOrDefault(name);
        }

        public bool FileExists(string path)
        {
            return false;
        }

        public string? GetLoginShellFromPasswd()
        {
            return null;
        }
    }
}
