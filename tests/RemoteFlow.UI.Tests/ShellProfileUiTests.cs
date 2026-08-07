using System.IO.Pipelines;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ShellProfileUiTests
{
    [AvaloniaFact]
    public async Task NamedProfileMenuStartsWithExactWorkingDirectoryAndEnvironment()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = Profile("dev", "Developer shell");
        var profiles = new RecordingProfileService(profile);
        var pty = new RecordingPtyService();
        await using var workspace = CreateWorkspace(pty, profiles, new RecordingLauncher());

        await workspace.InitializeAsync(token);

        var menu = Assert.Single(workspace.ShellProfiles);
        Assert.Equal("Developer shell", menu.DisplayName);
        Assert.Same(profile, Assert.Single(workspace.Sessions).ShellProfile);
        Assert.Equal("C:\\projects\\remote-flow", pty.Options!.WorkingDirectory);
        Assert.Equal("developer", pty.Options.EnvironmentVariables["PROFILE_MARKER"]);
    }

    [AvaloniaFact]
    public async Task BadProfileCreatesClearFailedTabInsteadOfCrashing()
    {
        var profile = Profile("broken", "Broken shell");
        var profiles = new RecordingProfileService(profile) { FailSpawnOptions = true };
        await using var workspace = CreateWorkspace(new RecordingPtyService(), profiles, new RecordingLauncher());

        await workspace.InitializeAsync(TestContext.Current.CancellationToken);

        var failed = Assert.Single(workspace.Sessions);
        Assert.Equal(SessionState.Failed, failed.State);
        Assert.Contains("Broken shell", failed.EndedMessage, StringComparison.Ordinal);
        Assert.Contains("not found", failed.EndedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, failed.Model.Search("RemoteFlow"));
    }

    [AvaloniaFact]
    public async Task TabContextActionOpensItsProfileInSystemTerminal()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = Profile("dev", "Developer shell");
        var launcher = new RecordingLauncher();
        await using var workspace = CreateWorkspace(
            new RecordingPtyService(),
            new RecordingProfileService(profile),
            launcher);
        await workspace.InitializeAsync(token);

        await workspace.OpenInSystemTerminalAsync(Assert.Single(workspace.Sessions), token);

        Assert.Same(profile, Assert.Single(launcher.LocalProfiles));
        Assert.Null(workspace.ErrorMessage);
    }

    [Fact]
    public async Task ConnectionDetailsShowsSystemTerminalFailureWithoutThrowing()
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "SSH server",
            "ssh.test",
            ProtocolType.Ssh,
            DateTimeOffset.UtcNow).Value;
        var details = new ConnectionDetailsViewModel(
            connection,
            "No folder",
            [],
            null,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.FromResult(SystemTerminalLaunchResult.Failure("Install OpenSSH first.")));

        Assert.True(details.OpenSystemTerminalCommand.CanExecute(null));
        await details.OpenSystemTerminalCommand.ExecuteAsync(null);

        Assert.Equal("Install OpenSSH first.", details.ExternalLaunchMessage);
    }

    private static TerminalWorkspaceViewModel CreateWorkspace(
        IPtyService pty,
        IShellProfileService profiles,
        ISystemTerminalLauncher launcher)
    {
        return new TerminalWorkspaceViewModel(
            pty,
            new ImmediateDispatcher(),
            new InMemorySettingsStore(),
            null,
            null,
            null,
            null,
            profiles,
            launcher);
    }

    private static ShellProfile Profile(string id, string name)
    {
        return new ShellProfile
        {
            Id = id,
            DisplayName = name,
            ShellPath = "C:\\Tools\\shell.exe",
            Arguments = ["--interactive"],
            WorkingDirectory = "C:\\projects\\remote-flow",
            EnvironmentVariables = new Dictionary<string, string> { ["PROFILE_MARKER"] = "developer" },
            Icon = ">_",
        };
    }

    private sealed class RecordingProfileService(ShellProfile profile) : IShellProfileService
    {
        public event EventHandler? ProfilesChanged;

        public bool FailSpawnOptions { get; init; }

        public Task<IReadOnlyList<ShellProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ShellProfile>>([profile]);
        }

        public Task<ShellProfile> GetDefaultProfileAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(profile);
        }

        public Task SaveProfilesAsync(
            IReadOnlyList<ShellProfile> profiles,
            string defaultProfileId,
            CancellationToken cancellationToken = default)
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public PtySpawnOptions CreateSpawnOptions(ShellProfile value)
        {
            return FailSpawnOptions
                ? throw new FileNotFoundException($"Shell profile '{value.DisplayName}' was not found.")
                : new PtySpawnOptions
                {
                    ShellPath = value.ShellPath,
                    Arguments = value.Arguments,
                    WorkingDirectory = value.WorkingDirectory,
                    EnvironmentVariables = value.EnvironmentVariables,
                };
        }
    }

    private sealed class RecordingLauncher : ISystemTerminalLauncher
    {
        public List<ShellProfile> LocalProfiles { get; } = [];

        public Task<SystemTerminalLaunchResult> OpenLocalAsync(
            ShellProfile profile,
            CancellationToken cancellationToken = default)
        {
            LocalProfiles.Add(profile);
            return Task.FromResult(SystemTerminalLaunchResult.Success);
        }

        public Task<SystemTerminalLaunchResult> OpenSshAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SystemTerminalLaunchResult.Success);
        }
    }

    private sealed class RecordingPtyService : IPtyService
    {
        public PtySpawnOptions? Options { get; private set; }

        public Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult<IPtySession>(new FakePtySession());
        }
    }

    private sealed class FakePtySession : IPtySession
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 42;
        public PipeReader Output => _pipe.Reader;
        public Task<int?> Exited => _exited.Task;
        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.Writer.CompleteAsync();
            if (_exited.TrySetResult(null))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(null, true));
            }
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
