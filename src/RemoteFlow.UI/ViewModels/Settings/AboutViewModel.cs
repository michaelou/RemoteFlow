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
/// disk. Nothing is uploaded and nothing is queued for upload.</summary>
public sealed partial class AboutViewModel : ObservableObject, IDisposable
{
    public const string RepositoryUrl = "https://github.com/michaelou/RemoteFlow";

    private const string _unknownCommit = "unknown";

    private readonly IShellOpenService? _shell;
    private readonly ILastErrorStore? _lastErrorStore;
    private readonly IUiDispatcher? _dispatcher;

    public AboutViewModel(
        IAppVersionInfo version,
        IAppPaths? paths = null,
        IShellOpenService? shell = null,
        ILastErrorStore? lastErrorStore = null,
        IUiDispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version.Version;
        Commit = string.IsNullOrEmpty(version.CommitSha) ? _unknownCommit : version.CommitSha;
        _shell = shell;
        _lastErrorStore = lastErrorStore;
        _dispatcher = dispatcher;
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
