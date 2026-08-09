using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Settings;

/// <summary>What the about box shows: which build this is, which commit it came from, what it is licensed
/// under, what it ships, and where its files are.
///
/// The last two make it the diagnostics page as well. "Open the log folder" is the whole of RemoteFlow's
/// crash reporting: the last error is named on screen and the logs are one click away, on the user's own
/// disk. Nothing is uploaded and nothing is queued for upload.
///
/// It is also the only place in the application that reaches a host the user did not configure, and only
/// when asked: the update check runs when the button is pressed, or on the first launch after
/// <see cref="SettingKeys.CheckForUpdates"/> has been switched on. It reads a version number and a link.
/// It downloads nothing and installs nothing — there is no auto-updater.</summary>
public sealed partial class AboutViewModel : ObservableObject, IDisposable
{
    public const string RepositoryUrl = "https://github.com/michaelou/RemoteFlow";

    private const string _unknownCommit = "unknown";

    private readonly IShellOpenService? _shell;
    private readonly ILastErrorStore? _lastErrorStore;
    private readonly IUiDispatcher? _dispatcher;
    private readonly IUpdateChecker? _updateChecker;
    private readonly ISettingsStore? _settings;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _loadingSettings;
    private bool _checkedThisSession;

    public AboutViewModel(
        IAppVersionInfo version,
        IAppPaths? paths = null,
        IShellOpenService? shell = null,
        ILastErrorStore? lastErrorStore = null,
        IUiDispatcher? dispatcher = null,
        IUpdateChecker? updateChecker = null,
        ISettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version.Version;
        Commit = string.IsNullOrEmpty(version.CommitSha) ? _unknownCommit : version.CommitSha;
        _shell = shell;
        _lastErrorStore = lastErrorStore;
        _dispatcher = dispatcher;
        _updateChecker = updateChecker;
        _settings = settings;
        LogDirectory = paths?.LogDirectory ?? string.Empty;
        DataDirectory = paths?.DataDirectory ?? string.Empty;
        RefreshLastError();
        if (_lastErrorStore is { } store)
        {
            store.Changed += OnLastErrorChanged;
        }
    }

    // Instance rather than static: the about box binds to it, and Avalonia bindings cannot see statics.
    public string ProductName { get; } = "RemoteFlow";

    /// <summary>The SemVer version, for example <c>0.1.0</c> or <c>0.0.0-alpha.0.57</c>.</summary>
    public string Version { get; }

    /// <summary>The full commit hash, or <c>unknown</c> when the build recorded none. The full hash rather
    /// than a short prefix, because this is the line people paste into a bug report.</summary>
    public string Commit { get; }

    public string License { get; } = "MIT";

    public string Repository { get; } = RepositoryUrl;

    /// <summary>Where the logs are. Shown as well as opened: a path that can be read out is what makes
    /// this usable over a support conversation, and it is the answer when the file manager will not
    /// start.</summary>
    public string LogDirectory { get; }

    /// <summary>Connections, settings, trusted host keys, and credential references.</summary>
    public string DataDirectory { get; }

    /// <summary>Every third-party package with its licence, embedded in the binary so it travels with a
    /// portable zip.</summary>
    public string Notices { get; } = ThirdPartyNotices.Text;

    /// <summary>Empty when nothing has gone wrong this session, which is the normal case and is why the
    /// crash section is hidden rather than showing a reassuring nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastError))]
    public partial string LastErrorSummary { get; private set; } = string.Empty;

    public bool HasLastError => LastErrorSummary.Length > 0;

    /// <summary>The outcome of the most recent action, or empty. Failures land here rather than in a
    /// dialog: none of these actions is important enough to interrupt someone over.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Whether this build has an update check available at all. False in a host that registered
    /// no checker, and the button is hidden rather than present and dead.</summary>
    public bool CanCheckForUpdates => _updateChecker is not null;

    /// <summary>What the last check found, in a sentence. Empty until one has run, because a page opened
    /// to read a version number should not claim a check happened when none did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    public partial string UpdateStatus { get; private set; } = string.Empty;

    public bool HasUpdateStatus => UpdateStatus.Length > 0;

    /// <summary>Shown as text, not only as a spinner: a check that reaches an unresponsive network takes
    /// fifteen seconds to give up, and a button that looks idle for that long reads as broken.</summary>
    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; private set; }

    /// <summary>Set only when a newer release exists, and it is what the release-page button opens. A
    /// check that found nothing newer leaves it null and the button hidden — there is nowhere useful to
    /// send someone who is already current.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    public partial Uri? ReleasePageUrl { get; private set; }

    public bool IsUpdateAvailable => ReleasePageUrl is not null;

    /// <summary>The opt-in. Off until switched on, and when it is on RemoteFlow checks once per launch
    /// rather than on a timer — the question "is there a newer version" does not change often enough to
    /// ask it more than that, and a background poll is a network call the user did not watch happen.</summary>
    [ObservableProperty]
    public partial bool AutomaticUpdateCheckEnabled { get; set; }

    [RelayCommand]
    public Task OpenLogFolderAsync(CancellationToken cancellationToken)
    {
        return OpenFolderAsync(LogDirectory, "log", cancellationToken);
    }

    [RelayCommand]
    public Task OpenDataFolderAsync(CancellationToken cancellationToken)
    {
        return OpenFolderAsync(DataDirectory, "data", cancellationToken);
    }

    [RelayCommand]
    public async Task OpenRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_shell is null)
        {
            StatusText = "Opening links is unavailable in this build.";
            return;
        }

        var result = await _shell.OpenUrlAsync(new Uri(RepositoryUrl), cancellationToken)
            .ConfigureAwait(true);
        StatusText = result.Succeeded ? string.Empty : result.ErrorMessage ?? "The link could not be opened.";
    }

    /// <summary>Reads the opt-in, and honours it. Called from application startup and again when the page
    /// is shown; it is idempotent, and the check it may start runs at most once a session.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (_settings is { } settings)
            {
                _loadingSettings = true;
                try
                {
                    AutomaticUpdateCheckEnabled = await settings
                        .Get(SettingKeys.CheckForUpdates, cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    _loadingSettings = false;
                }
            }

            _initialized = true;
        }
        finally
        {
            _ = _initializationGate.Release();
        }

        if (AutomaticUpdateCheckEnabled && !_checkedThisSession)
        {
            // Started rather than awaited. One caller is the application's startup path, and a check that
            // takes fifteen seconds to give up on an unreachable network must not stand between the user
            // and their window. RunCheckAsync reports every failure instead of throwing, so nothing here
            // can become an unobserved exception.
            _ = RunCheckAsync(CancellationToken.None);
        }
    }

    /// <summary>Asks whether a newer release exists. Pressing this is the consent: it is the one action in
    /// RemoteFlow that contacts a host the user did not configure, and it reports what it found rather
    /// than acting on it.</summary>
    [RelayCommand]
    public Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        return RunCheckAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task OpenReleasePageAsync(CancellationToken cancellationToken)
    {
        if (ReleasePageUrl is not { } url)
        {
            return;
        }

        if (_shell is null)
        {
            StatusText = "Opening links is unavailable in this build.";
            return;
        }

        var result = await _shell.OpenUrlAsync(url, cancellationToken).ConfigureAwait(true);
        StatusText = result.Succeeded ? string.Empty : result.ErrorMessage ?? "The link could not be opened.";
    }

    /// <summary>Re-reads the last error. The about box is constructed once and lives for the session, so
    /// without this it would show whatever had gone wrong before it was first opened.</summary>
    public void RefreshLastError()
    {
        var error = _lastErrorStore?.Current;
        LastErrorSummary = error is null
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} during {1}: {2} — {3}",
                error.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                error.Context,
                error.ExceptionType,
                error.Message);
    }

    [RelayCommand]
    public void DismissLastError()
    {
        _lastErrorStore?.Clear();
        RefreshLastError();
        StatusText = string.Empty;
    }

    public void Dispose()
    {
        if (_lastErrorStore is { } store)
        {
            store.Changed -= OnLastErrorChanged;
        }

        _initializationGate.Dispose();
    }

    // Writing the opt-in as soon as it is toggled, rather than behind an Apply button: it is one boolean,
    // and a preference about network access that had not been saved yet would be the wrong thing to be
    // unsure about. The guard keeps loading the stored value from writing it straight back.
    partial void OnAutomaticUpdateCheckEnabledChanged(bool value)
    {
        if (_loadingSettings || _settings is not { } settings)
        {
            return;
        }

        // Fire and forget: the toggle is already showing the new state, and a settings write that failed
        // has the log for it. Nothing here is worth blocking the checkbox on.
        _ = PersistAutomaticCheckAsync(settings, value);
    }

    private async Task PersistAutomaticCheckAsync(ISettingsStore settings, bool value)
    {
        await settings.Set(SettingKeys.CheckForUpdates, value).ConfigureAwait(true);

        // Switching it on is as much a request to check as pressing the button is, and waiting until the
        // next launch to act on it would look like the setting did nothing.
        if (value && !_checkedThisSession)
        {
            await RunCheckAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        if (_updateChecker is not { } checker || IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatus = "Checking for updates…";
        ReleasePageUrl = null;
        try
        {
            var result = await checker.CheckAsync(cancellationToken).ConfigureAwait(true);
            _checkedThisSession = true;
            UpdateStatus = Describe(result);
            // Only an available update gets a link. Sending someone who is already current to a download
            // page is an invitation to reinstall what they are running.
            ReleasePageUrl = result.Outcome == UpdateCheckOutcome.UpdateAvailable ? result.ReleasePageUrl : null;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = string.Empty;
        }
        catch (Exception exception)
        {
            // A checker is allowed to be imperfect; this method is not, because one of its callers does
            // not await it and an escaping exception there would take the application down.
            UpdateStatus = $"The update check could not complete: {exception.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>The sentence that goes on screen. Every outcome says what it means in words — a colour or
    /// an icon alone would leave the result unreadable to a screen reader and to anyone who cannot tell
    /// the two apart.</summary>
    private string Describe(UpdateCheckResult result)
    {
        return result.Outcome switch
        {
            UpdateCheckOutcome.UpdateAvailable => string.Format(
                CultureInfo.CurrentCulture,
                "RemoteFlow {0} is available. This build is {1}. RemoteFlow does not update itself: the release page has the installer and the portable zip.",
                result.LatestVersion,
                Version),
            UpdateCheckOutcome.UpToDate => string.Format(
                CultureInfo.CurrentCulture,
                "This build is current. {0} is the newest release.",
                result.LatestVersion),
            UpdateCheckOutcome.NoReleaseYet =>
                "There are no published releases yet, so there is nothing to compare this build against.",
            UpdateCheckOutcome.Failed => result.ErrorMessage ?? "The update check could not complete.",
            _ => result.ErrorMessage ?? "The update check could not complete.",
        };
    }

    // The store raises this from whichever thread failed.
    private void OnLastErrorChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is not { } dispatcher)
        {
            RefreshLastError();
            return;
        }

        // Fire and forget on purpose: the marshalled call only assigns a string, and a diagnostics panel
        // that threw while reporting an error would have nowhere to report it.
        _ = dispatcher.InvokeAsync(RefreshLastError).AsTask();
    }

    private async Task OpenFolderAsync(string path, string description, CancellationToken cancellationToken)
    {
        if (_shell is null || string.IsNullOrEmpty(path))
        {
            StatusText = $"This build does not know where its {description} folder is.";
            return;
        }

        var result = await _shell.OpenFolderAsync(path, cancellationToken).ConfigureAwait(true);
        StatusText = result.Succeeded
            ? string.Empty
            : result.ErrorMessage ?? $"The {description} folder could not be opened.";
    }
}
