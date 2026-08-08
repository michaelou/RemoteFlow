using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;
using SvcSystems.UI.Terminal;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.RegularExpressions;

namespace RemoteFlow.UI.ViewModels.Terminal;

public sealed partial class TerminalSessionViewModel : ObservableObject, IAsyncDisposable, IDisposable
{
    internal static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan OutputFrameBudget = TimeSpan.FromMilliseconds(16);
    internal const int MaximumBytesPerFrame = 64 * 1024;
    internal const int MaximumPendingOutputBytes = 4 * 1024 * 1024;

    private static readonly char[] _pathSeparators = ['\\', '/'];

    private readonly ITerminalChannel _channel;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<CancellationToken, Task>? _retry;
    private readonly Func<CancellationToken, Task>? _close;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Utf8StreamDecoder _decoder = new();
    private readonly OscTitleParser _titleParser = new();
    private readonly Task _readTask;
    private readonly Task _exitTask;
    private readonly Lock _resizeSync = new();
    private CancellationTokenSource? _pendingResize;
    private long _droppedOutputBytes;
    private List<SearchNavigationMatch> _filteredSearchMatches = [];
    private bool _usesNativeSearch = true;
    private int _currentFilteredSearchMatch = -1;
    private int _disposeStarted;

    public TerminalSessionViewModel(
        ITerminalChannel channel,
        IUiDispatcher dispatcher,
        TerminalControlModel? model = null,
        string initialTitle = "Local shell",
        EnvironmentKind environment = EnvironmentKind.Unspecified,
        string? colorOverrideHex = null,
        ShellProfile? shellProfile = null,
        Guid? managedSessionId = null,
        SessionState initialState = SessionState.Connected,
        Func<CancellationToken, Task>? retry = null,
        Func<CancellationToken, Task>? close = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Model = model ?? new TerminalControlModel(new TerminalOptions
        {
            Cols = 120,
            Rows = 30,
            Scrollback = 10_000,
            ReflowOnResize = false,
            TermName = "xterm-256color",
        });
        Model.UserInput += OnUserInput;
        Model.SizeChanged += OnTerminalSizeChanged;
        Title = string.IsNullOrWhiteSpace(initialTitle) ? "Terminal" : initialTitle.Trim();
        UserTitleOverride = managedSessionId is null ? null : Title;
        Environment = environment;
        AccentColorHex = ResolveAccentColor(environment, colorOverrideHex);
        ShellProfile = shellProfile;
        ManagedSessionId = managedSessionId;
        _retry = retry;
        _close = close;
        State = initialState;
        _readTask = ReadOutputAsync(_lifetime.Token);
        _exitTask = ObserveExitAsync();
        Completion = ObserveCompletionAsync();
    }

    public TerminalControlModel Model { get; }

    public Task Completion { get; }

    public int? ProcessId => (_channel as IPtySession)?.ProcessId;

    public long DroppedOutputBytes => Interlocked.Read(ref _droppedOutputBytes);

    [ObservableProperty]
    public partial string FontFamilyName { get; private set; } = OperatingSystem.IsWindows() ? "Cascadia Mono" : "DejaVu Sans Mono";

    [ObservableProperty]
    public partial double TerminalFontSize { get; private set; } = 13;

    [ObservableProperty]
    public partial string TerminalBackground { get; private set; } = TerminalColorSchemes.Dark.Background;

    [ObservableProperty]
    public partial string TerminalForeground { get; private set; } = TerminalColorSchemes.Dark.Foreground;

    [ObservableProperty]
    public partial bool IsFindOpen { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchCaseSensitive { get; set; }

    [ObservableProperty]
    public partial bool IsSearchRegex { get; set; }

    [ObservableProperty]
    public partial int SearchMatchCount { get; private set; }

    [ObservableProperty]
    public partial string SearchStatus { get; private set; } = "Type to find in scrollback";

    [ObservableProperty]
    public partial string? SearchError { get; private set; }

    public EnvironmentKind Environment { get; }

    public ShellProfile? ShellProfile { get; }

    public Guid? ManagedSessionId { get; }

    public string AccentColorHex { get; }

    public string TabBackgroundHex => IsActive ? $"#33{AccentColorHex[1..]}" : "#121821";

    public string ChromeTintHex => IsActive ? $"#1F{AccentColorHex[1..]}" : "#101418";

    public string EnvironmentCue => Environment switch
    {
        EnvironmentKind.Development => "DEV",
        EnvironmentKind.Staging => "STG",
        EnvironmentKind.Production => "PROD !",
        EnvironmentKind.Unspecified => "LOCAL",
        _ => throw new ArgumentOutOfRangeException(nameof(Environment)),
    };

    public bool IsProduction => Environment == EnvironmentKind.Production;

    public bool IsLive => State is SessionState.Created or SessionState.Connecting or
        SessionState.Connected or SessionState.Reconnecting;

    public bool IsEnded => !IsLive;

    public string RecoveryActionLabel => State == SessionState.Disconnected ? "Reconnect" : "Retry";

    public bool ApplicationCursorKeys { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    public partial string Title { get; private set; }

    /// <summary>
    /// The tab-strip form of <see cref="Title" />.
    /// </summary>
    public string TabTitle => CondenseTitle(Title);

    [ObservableProperty]
    public partial string? UserTitleOverride { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    [ObservableProperty]
    public partial SessionState State { get; private set; } = SessionState.Created;

    [ObservableProperty]
    public partial string? EndedMessage { get; private set; }

    /// <summary>
    /// Reduces a shell-reported window title to something that fits a tab.
    /// </summary>
    /// <remarks>
    /// Shells report their working directory as the title — <c>cmd.exe</c> reports the full path,
    /// sometimes behind a label such as <c>Administrator: C:\…</c>. Only the leaf segment is useful
    /// on a tab; the full title stays available as the tab tooltip.
    /// </remarks>
    internal static string CondenseTitle(string? title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Terminal";
        }

        var candidate = trimmed;
        var label = candidate.LastIndexOf(": ", StringComparison.Ordinal);
        if (label >= 0 && IsRootedPath(candidate[(label + 2)..]))
        {
            candidate = candidate[(label + 2)..];
        }

        if (!IsRootedPath(candidate))
        {
            return trimmed;
        }

        var path = candidate.TrimEnd('\\', '/');
        var separator = path.LastIndexOfAny(_pathSeparators);
        var leaf = separator < 0 ? path : path[(separator + 1)..];
        return leaf.Length == 0 ? candidate : leaf;
    }

    private static bool IsRootedPath(string value)
    {
        return value.Length >= 2 &&
            ((value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] is '\\' or '/')) ||
                (value[0] == '\\' && value[1] == '\\') ||
                value[0] == '/' ||
                (value[0] == '~' && value[1] is '/' or '\\'));
    }

    public void SetTitleOverride(string? title)
    {
        UserTitleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (UserTitleOverride is not null)
        {
            Title = UserTitleOverride;
        }
    }

    public void ApplyManagedState(SessionState state, string? message)
    {
        State = state;
        EndedMessage = message;
        RetryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        if (_retry is not null)
        {
            await _retry(cancellationToken).ConfigureAwait(true);
        }
    }

    private bool CanRetry()
    {
        return _retry is not null && State is SessionState.Failed or SessionState.Disconnected;
    }

    internal void SetActive(bool isActive)
    {
        IsActive = isActive;
    }

    public async ValueTask SendInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        try
        {
            await _channel.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal input failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    public void RequestResize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        CancellationTokenSource pending;
        lock (_resizeSync)
        {
            _pendingResize?.Cancel();
            _pendingResize?.Dispose();
            pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pendingResize = pending;
        }

        _ = DebounceResizeAsync(columns, rows, pending);
    }

    public void ApplyAppearance(TerminalAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FontFamilyName = settings.FontFamily;
        TerminalFontSize = settings.FontSize;
        TerminalBackground = settings.ColorScheme.Background;
        TerminalForeground = settings.ColorScheme.Foreground;

        var terminal = Model.Terminal;
        terminal.Options.Scrollback = settings.Scrollback;
        var engine = terminal.Engine;
        engine.Options.FontFamily = settings.FontFamily;
        engine.Options.FontSize = settings.FontSize;
        engine.Options.Scrollback = settings.Scrollback;
        engine.Options.Theme = settings.ColorScheme.ToThemeOptions();
        engine.Options.BellStyle = settings.BellMode switch
        {
            TerminalBellMode.None => XTerm.Options.BellStyle.None,
            TerminalBellMode.Audible => XTerm.Options.BellStyle.Sound,
            TerminalBellMode.Visual => XTerm.Options.BellStyle.Visual,
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        terminal.Buffer.Lines.Resize(Math.Max(terminal.Rows, terminal.Rows + settings.Scrollback));
        engine.SetCursorStyle(TerminalSettingsViewModel.ToXTermCursorStyle(settings.CursorStyle), settings.CursorBlink);
        Model.FullBufferUpdate();
    }

    public static TerminalSessionViewModel CreateFailed(
        IUiDispatcher dispatcher,
        string title,
        string message,
        ShellProfile? shellProfile = null)
    {
        var viewModel = new TerminalSessionViewModel(
            new UnavailableTerminalChannel(),
            dispatcher,
            initialTitle: title,
            shellProfile: shellProfile)
        {
            State = SessionState.Failed,
            EndedMessage = message,
        };
        viewModel.Model.Feed($"\r\n[RemoteFlow: {message}]\r\n");
        return viewModel;
    }

    [RelayCommand]
    public void OpenFind()
    {
        IsFindOpen = true;
        RefreshSearch();
    }

    [RelayCommand]
    public void CloseFind()
    {
        IsFindOpen = false;
        SearchError = null;
        SearchStatus = string.Empty;
        SearchMatchCount = 0;
        _filteredSearchMatches = [];
        _currentFilteredSearchMatch = -1;
        _ = Model.Search(string.Empty);
    }

    [RelayCommand]
    public void FindNext()
    {
        NavigateSearch(forward: true);
    }

    [RelayCommand]
    public void FindPrevious()
    {
        NavigateSearch(forward: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Model.UserInput -= OnUserInput;
        Model.SizeChanged -= OnTerminalSizeChanged;
        lock (_resizeSync)
        {
            _pendingResize?.Cancel();
            _pendingResize?.Dispose();
            _pendingResize = null;
        }
        _lifetime.Cancel();
        if (_close is not null)
        {
            await _close(CancellationToken.None).ConfigureAwait(false);
        }
        await _channel.DisposeAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        await _channel.Output.CompleteAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lastFrame = Stopwatch.GetTimestamp();
            while (true)
            {
                var result = await _channel.Output.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                try
                {
                    var droppedThisFrame = 0L;
                    if (buffer.Length > MaximumPendingOutputBytes)
                    {
                        // Once the bounded backlog is exceeded, keeping the full cap would still
                        // force dozens of expensive terminal parses. Preserve only the newest UI
                        // frame so input and Ctrl+C remain responsive during an unbounded producer.
                        droppedThisFrame = buffer.Length - MaximumBytesPerFrame;
                        consumed = buffer.GetPosition(droppedThisFrame);
                        buffer = buffer.Slice(consumed);
                        _ = Interlocked.Add(ref _droppedOutputBytes, droppedThisFrame);
                    }

                    var frameLength = (int)Math.Min(buffer.Length, MaximumBytesPerFrame);
                    var frame = buffer.Slice(0, frameLength);
                    consumed = frame.End;
                    var flush = result.IsCompleted && frameLength == buffer.Length;
                    var text = _decoder.Decode(frame, flush);
                    if (text.Length > 0)
                    {
                        var reportedTitles = _titleParser.Process(text);
                        var truncationNotice = droppedThisFrame == 0
                            ? string.Empty
                            : $"\r\n[RemoteFlow: output truncated; dropped {droppedThisFrame:N0} bytes]\r\n";
                        await _dispatcher.InvokeAsync(() =>
                        {
                            var wasAtBottom = Model.Terminal.Buffer.IsAtBottom;
                            var previousViewport = Model.Terminal.Buffer.YDisp;
                            if (truncationNotice.Length > 0)
                            {
                                Model.Feed(truncationNotice);
                            }

                            Model.Feed(text);
                            if (IsFindOpen && SearchText.Length > 0)
                            {
                                RefreshSearch();
                            }

                            if (wasAtBottom)
                            {
                                Model.EnsureCaretIsVisible();
                            }
                            else
                            {
                                Model.ScrollToYDisp(previousViewport);
                                Model.Terminal.Buffer.ViewportY = previousViewport;
                                Model.FullBufferUpdate();
                            }

                            if (UserTitleOverride is null && reportedTitles.Count > 0)
                            {
                                Title = reportedTitles[^1];
                            }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    else if (droppedThisFrame > 0)
                    {
                        await _dispatcher.InvokeAsync(
                            () => Model.Feed($"\r\n[RemoteFlow: output truncated; dropped {droppedThisFrame:N0} bytes]\r\n"),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    var examined = consumed.Equals(result.Buffer.End) ? result.Buffer.End : consumed;
                    _channel.Output.AdvanceTo(consumed, examined);
                }

                if (result.IsCompleted && consumed.Equals(result.Buffer.End))
                {
                    break;
                }

                var elapsed = Stopwatch.GetElapsedTime(lastFrame);
                if (elapsed < OutputFrameBudget)
                {
                    await Task.Delay(OutputFrameBudget - elapsed, cancellationToken).ConfigureAwait(false);
                }

                lastFrame = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal output failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            var exitCode = await _channel.Exited.ConfigureAwait(false);
            void UpdateEndedState()
            {
                State = SessionState.Closed;
                EndedMessage = exitCode is { } code
                    ? $"Session ended (exit code {code})."
                    : "Session ended (terminated).";
            }

            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                UpdateEndedState();
            }
            else
            {
                await _dispatcher.InvokeAsync(UpdateEndedState).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal session failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task ObserveCompletionAsync()
    {
        await Task.WhenAll(_readTask, _exitTask).ConfigureAwait(false);
    }

    private async void OnUserInput(object? sender, TerminalUserInputEventArgs e)
    {
        if (e.Data.IsEmpty || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        await SendInputAsync(e.Data, _lifetime.Token).ConfigureAwait(false);
    }

    private void OnTerminalSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        RequestResize(e.Cols, e.Rows);
    }

    private async Task DebounceResizeAsync(int columns, int rows, CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(ResizeDebounce, pending.Token).ConfigureAwait(false);
            await _channel.ResizeAsync(columns, rows, pending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal resize failed: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            lock (_resizeSync)
            {
                if (ReferenceEquals(_pendingResize, pending))
                {
                    _pendingResize = null;
                    pending.Dispose();
                }
            }
        }
    }

    private ValueTask SetFailedAsync(string message)
    {
        void UpdateFailedState()
        {
            State = SessionState.Failed;
            EndedMessage = message;
        }

        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            UpdateFailedState();
            return ValueTask.CompletedTask;
        }

        return _dispatcher.InvokeAsync(UpdateFailedState);
    }

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsEnded));
        OnPropertyChanged(nameof(RecoveryActionLabel));
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackgroundHex));
        OnPropertyChanged(nameof(ChromeTintHex));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (IsFindOpen)
        {
            RefreshSearch();
        }
    }

    partial void OnIsSearchCaseSensitiveChanged(bool value)
    {
        if (IsFindOpen)
        {
            RefreshSearch();
        }
    }

    partial void OnIsSearchRegexChanged(bool value)
    {
        if (IsFindOpen)
        {
            RefreshSearch();
        }
    }

    private void RefreshSearch()
    {
        SearchError = null;
        _currentFilteredSearchMatch = -1;
        if (string.IsNullOrEmpty(SearchText))
        {
            _ = Model.Search(string.Empty);
            SearchMatchCount = 0;
            SearchStatus = "Type to find in scrollback";
            _filteredSearchMatches = [];
            return;
        }

        _usesNativeSearch = !IsSearchCaseSensitive && !IsSearchRegex;
        if (_usesNativeSearch)
        {
            SearchMatchCount = Model.Search(SearchText);
            Model.CurrentSearchResultIndex = -1;
            SearchStatus = SearchMatchCount == 0 ? "No matches" : $"{SearchMatchCount:N0} matches";
            _filteredSearchMatches = [];
            return;
        }

        try
        {
            var pattern = IsSearchRegex ? SearchText : Regex.Escape(SearchText);
            var options = RegexOptions.CultureInvariant;
            if (!IsSearchCaseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(100));
            var snapshot = Model.SearchService.GetSnapshot();
            var matches = regex.Matches(snapshot.Text).Cast<Match>().Where(match => match.Length > 0).ToArray();
            _filteredSearchMatches = ToNavigationMatches(snapshot.Text, matches);
            SearchMatchCount = _filteredSearchMatches.Count;
            SearchStatus = SearchMatchCount == 0 ? "No matches" : $"{SearchMatchCount:N0} matches";
            _ = Model.Search(matches.FirstOrDefault()?.Value ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            _ = Model.Search(string.Empty);
            SearchMatchCount = 0;
            _filteredSearchMatches = [];
            SearchError = $"Invalid regular expression: {exception.Message}";
            SearchStatus = "Invalid regular expression";
        }
        catch (RegexMatchTimeoutException)
        {
            _ = Model.Search(string.Empty);
            SearchMatchCount = 0;
            _filteredSearchMatches = [];
            SearchError = "The regular expression took too long to evaluate.";
            SearchStatus = "Search timed out";
        }
    }

    private void NavigateSearch(bool forward)
    {
        if (SearchMatchCount == 0 || SearchError is not null)
        {
            return;
        }

        if (_usesNativeSearch)
        {
            var index = forward ? Model.SelectNextSearchResult() : Model.SelectPreviousSearchResult();
            SearchStatus = index < 0 ? "No matches" : $"{index + 1:N0} / {SearchMatchCount:N0}";
            return;
        }

        _currentFilteredSearchMatch = forward
            ? (_currentFilteredSearchMatch + 1) % _filteredSearchMatches.Count
            : (_currentFilteredSearchMatch <= 0 ? _filteredSearchMatches.Count : _currentFilteredSearchMatch) - 1;
        var match = _filteredSearchMatches[_currentFilteredSearchMatch];
        _ = Model.Search(match.Text);
        _ = Model.SelectNextSearchResult();
        Model.ScrollToYDisp(match.Line);
        SearchStatus = $"{_currentFilteredSearchMatch + 1:N0} / {SearchMatchCount:N0}";
    }

    private static List<SearchNavigationMatch> ToNavigationMatches(string text, Match[] matches)
    {
        var result = new List<SearchNavigationMatch>(matches.Length);
        var line = 0;
        var scanned = 0;
        foreach (var match in matches)
        {
            for (var index = scanned; index < match.Index; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                }
            }

            result.Add(new SearchNavigationMatch(line, match.Value));
            scanned = match.Index;
        }

        return result;
    }

    private static string ResolveAccentColor(EnvironmentKind environment, string? colorOverrideHex)
    {
        return !string.IsNullOrWhiteSpace(colorOverrideHex) &&
            System.Text.RegularExpressions.Regex.IsMatch(colorOverrideHex, "^#[0-9A-Fa-f]{6}$")
            ? colorOverrideHex.ToUpperInvariant()
            : environment switch
            {
                EnvironmentKind.Development => "#5DE28C",
                EnvironmentKind.Staging => "#FFCA58",
                EnvironmentKind.Production => "#FF7B72",
                EnvironmentKind.Unspecified => "#7E8998",
                _ => throw new ArgumentOutOfRangeException(nameof(environment)),
            };
    }

    private sealed record SearchNavigationMatch(int Line, string Text);

    private sealed class UnavailableTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            if (_exited.TrySetResult(null))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(null, true));
            }
        }
    }
}
