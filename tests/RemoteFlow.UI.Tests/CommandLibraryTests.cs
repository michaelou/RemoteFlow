using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.Views;
using RemoteFlow.UI.Views.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class CommandLibraryTests
{
    private const string _catalog = """
        {
          "groups": [
            {
              "id": "disk",
              "name": "Disk & Space",
              "commands": [
                {
                  "id": "disk-filesystem-usage",
                  "title": "Check disk usage",
                  "command": "df -h",
                  "description": "Show filesystem disk usage.",
                  "tags": ["disk", "space"],
                  "risk": "safe"
                }
              ]
            },
            {
              "id": "docker",
              "name": "Docker & Containers",
              "commands": [
                {
                  "id": "docker-logs",
                  "title": "Container logs",
                  "command": "docker logs <container>",
                  "description": "Display logs from a container.",
                  "tags": ["docker", "logs"],
                  "risk": "safe"
                },
                {
                  "id": "docker-volume-prune",
                  "title": "Prune unused Docker volumes",
                  "command": "docker volume prune",
                  "description": "Remove unused Docker volumes.",
                  "tags": ["docker", "cleanup"],
                  "risk": "danger"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ThePaletteOpensOnTheWholeLibraryAndNarrowsAsYouType()
    {
        var palette = new CommandSnippetPaletteViewModel(CommandSnippetLibrary.FromJson(_catalog));

        palette.Open();

        Assert.True(palette.IsOpen);
        Assert.Equal(3, palette.Results.Count);
        Assert.False(palette.HasEmptyState);
        Assert.Same(palette.Results[0], palette.SelectedResult);

        palette.SearchText = "prune";

        Assert.Equal("docker volume prune", Assert.Single(palette.Results).Command);
        Assert.Same(palette.Results[0], palette.SelectedResult);
    }

    [Fact]
    public void AnUnmatchedSearchSaysSoRatherThanShowingAnEmptyList()
    {
        var palette = new CommandSnippetPaletteViewModel(CommandSnippetLibrary.FromJson(_catalog));
        palette.Open();

        palette.SearchText = "kubectl";

        Assert.True(palette.HasEmptyState);
        Assert.Contains("No commands match", palette.EmptyMessage, StringComparison.Ordinal);
        Assert.Null(palette.SelectedResult);
        Assert.Null(palette.Commit());
        Assert.True(palette.IsOpen);
    }

    [Fact]
    public void TheHighlightMovesWithTheArrowKeysAndStopsAtEitherEnd()
    {
        var palette = new CommandSnippetPaletteViewModel(CommandSnippetLibrary.FromJson(_catalog));
        palette.Open();

        palette.MoveSelection(-1);
        Assert.Same(palette.Results[0], palette.SelectedResult);

        palette.MoveSelection(1);
        palette.MoveSelection(1);
        palette.MoveSelection(1);
        Assert.Same(palette.Results[^1], palette.SelectedResult);

        var chosen = palette.Commit();

        Assert.Same(palette.Results[^1], chosen);
        Assert.False(palette.IsOpen);
    }

    [Fact]
    public void RiskAndPlaceholdersAreCarriedThroughToWhatTheListShows()
    {
        var palette = new CommandSnippetPaletteViewModel(CommandSnippetLibrary.FromJson(_catalog));
        palette.Open();

        var safe = palette.Results.Single(result => result.Command == "df -h");
        var placeholder = palette.Results.Single(result => result.Command.StartsWith("docker logs", StringComparison.Ordinal));
        var destructive = palette.Results.Single(result => result.Command == "docker volume prune");

        Assert.False(safe.HasRiskBadge);
        Assert.False(safe.HasPlaceholder);
        Assert.True(placeholder.HasPlaceholder);
        Assert.True(destructive.HasRiskBadge);
        Assert.Equal("Destructive", destructive.RiskLabel);
        Assert.True(destructive.IsDestructive);
        Assert.Equal("Careful", CarefulLabel());
    }

    [AvaloniaFact]
    public async Task InsertingTypesTheCommandAtThePromptWithoutRunningIt()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new RecordingChannel();
        await using var workspace = CreateWorkspace();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());
        workspace.AddWorkspaceSession(session);

        Assert.True(workspace.OpenCommandLibrary());
        workspace.CommandLibrary.SearchText = "prune";
        Assert.True(await workspace.InsertSelectedCommandAsync(token));

        // Bracketed, so a shell that understands it will not run the command however it arrives, and
        // without a trailing newline, so the Enter that runs it is the user's own.
        Assert.Equal(
            "[200~docker volume prune[201~",
            Encoding.UTF8.GetString(channel.Written.WrittenSpan));
        Assert.False(workspace.CommandLibrary.IsOpen);
    }

    [AvaloniaFact]
    public async Task ThereIsNothingToInsertIntoWithoutATerminalSelected()
    {
        var token = TestContext.Current.CancellationToken;
        await using var workspace = CreateWorkspace();

        Assert.False(workspace.OpenCommandLibrary());
        Assert.False(workspace.CommandLibrary.IsOpen);
        Assert.Equal("Select a terminal to insert a command into.", workspace.ErrorMessage);
        Assert.False(await workspace.InsertSelectedCommandAsync(token));
    }

    /// <summary>
    /// The whole feature is one chord away from the prompt: the shortcut opens the library over the
    /// workspace with the keyboard already in the search box, and choosing a command puts the keyboard
    /// back in the terminal so the next Enter runs what was typed.
    /// </summary>
    [AvaloniaFact]
    public async Task TheShortcutOpensTheLibraryAndChoosingReturnsTheKeyboardToTheTerminal()
    {
        var channel = new RecordingChannel();
        await using var workspace = CreateWorkspace();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());
        workspace.AddWorkspaceSession(session);
        var view = new TerminalWorkspace { DataContext = workspace };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Press(view, Key.K, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.True(workspace.CommandLibrary.IsOpen);
        var overlay = Assert.IsType<Border>(view.FindControl<Border>("CommandLibraryOverlay"));
        Assert.True(overlay.IsVisible);
        var search = view.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.Name == "LibrarySearchBox");
        Assert.True(search.IsFocused, "The library opened without putting the keyboard in the search box.");

        search.Text = "prune";
        Dispatcher.UIThread.RunJobs();
        Press(search, Key.Enter, KeyModifiers.None);
        await WaitForWriteAsync(channel);

        Assert.False(workspace.CommandLibrary.IsOpen);
        Assert.False(overlay.IsVisible);
        Assert.Equal(
            "[200~docker volume prune[201~",
            Encoding.UTF8.GetString(channel.Written.WrittenSpan));
        Assert.True(
            view.GetVisualDescendants().OfType<SvcSystems.UI.Terminal.TerminalControl>().Single().IsFocused,
            "Choosing a command left the keyboard outside the terminal, so Enter would not run it.");
        window.Close();
    }

    /// <summary>The window's own Ctrl+K tunnels past every page, so the chord that opens the library has
    /// to survive the trip. It did not: a HasFlag test read Ctrl+Shift+K as quick connect.</summary>
    [AvaloniaFact]
    public void TheWindowsQuickConnectDoesNotSwallowTheLibrarysChord()
    {
        var navigation = NavigationService.CreateDefault();
        navigation.Navigate("terminals");
        var quickConnect = new CommandPaletteViewModel();
        var window = new MainWindow(
            new MainWindowViewModel(navigation, quickConnect),
            new WindowGeometryService(new InMemorySettingsStore()));
        window.Show();

        Press(window, Key.K, KeyModifiers.Control | KeyModifiers.Shift);

        Assert.False(quickConnect.IsOpen, "Ctrl+Shift+K opened quick connect instead of reaching the page.");

        Press(window, Key.K, KeyModifiers.Control);

        Assert.True(quickConnect.IsOpen, "Ctrl+K no longer opens quick connect.");
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeLeavesTheLibraryWithoutTypingAnything()
    {
        var channel = new RecordingChannel();
        await using var workspace = CreateWorkspace();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());
        workspace.AddWorkspaceSession(session);
        var view = new TerminalWorkspace { DataContext = workspace };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Press(view, Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        var search = view.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.Name == "LibrarySearchBox");

        Press(search, Key.Escape, KeyModifiers.None);

        Assert.False(workspace.CommandLibrary.IsOpen);
        Assert.Empty(channel.Written.WrittenSpan.ToArray());
        // The find bar reads Escape too, and it is not what the user was in.
        Assert.False(session.IsFindOpen);
        window.Close();
    }

    /// <summary>The amber badge has no example in this catalog, and it is the one a user meets most.</summary>
    private static string CarefulLabel()
    {
        return new CommandSnippetItemViewModel(new CommandSnippet(
            "docker-system-prune",
            "docker",
            "Docker & Containers",
            "Prune unused Docker resources",
            "docker system prune",
            "Remove unused Docker containers, networks, images and build cache.",
            ["docker", "cleanup"],
            CommandRisk.Warning)).RiskLabel;
    }

    /// <summary>The page around the command library and nothing else: the collaborators this feature does
    /// not touch — the clipboard, shell profiles, the system terminal — are left out deliberately, so a
    /// test that starts using one fails rather than quietly exercising a stub.</summary>
    private static TerminalsPageViewModel CreateWorkspace()
    {
        return new TerminalsPageViewModel(
            new UnusedPtyService(),
            new UiDispatcher(),
            new InMemorySettingsStore(),
            confirmation: null,
            new KeymapService(),
            clipboardController: null,
            terminalSettings: null,
            shellProfileService: null,
            systemTerminalLauncher: null,
            sessionManager: null,
            CommandSnippetLibrary.FromJson(_catalog));
    }

    private static void Press(Control target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs
        {
            Key = key,
            KeyModifiers = modifiers,
            RoutedEvent = InputElement.KeyDownEvent,
            Source = target,
        });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The insert runs through the view's async handler, so the write lands a turn or two after
    /// the key press.</summary>
    private static async Task WaitForWriteAsync(RecordingChannel channel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (channel.Written.WrittenCount == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArrayBufferWriter<byte> Written { get; } = new();

        public PipeReader Output => _pipe.Reader;

        public Task<int?> Exited => _exited.Task;

        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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


    private sealed class UnusedPtyService : IPtyService
    {
        public Task<IPtySession> SpawnAsync(
            PtySpawnOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This test adds its session directly.");
        }
    }



}
