using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Settings;

/// <summary>The Shell profiles tab: which shells the Terminals page can start, and what each one starts
/// with. It is a page of its own rather than the tail of the terminal's appearance settings because it is
/// the only part of settings with a Save button — editing a profile is editing a list of records, not
/// flipping a switch — and one page where some controls write immediately and others wait for a button is
/// a page nobody can predict.</summary>
public sealed partial class ShellProfilesViewModel : ObservableObject
{
    private readonly IShellProfileService? _profiles;
    private readonly IFilePickerService? _filePicker;
    private bool _initialized;
    private bool _loading;

    public ShellProfilesViewModel(
        IShellProfileService? profiles = null,
        IFilePickerService? filePicker = null)
    {
        _profiles = profiles;
        _filePicker = filePicker;
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanRemoveProfile));
    }

    public ObservableCollection<ShellProfileEditorViewModel> Profiles { get; } = [];

    /// <summary>The last profile cannot be removed: the Terminals page has to have something to start, so
    /// the Remove buttons disappear rather than failing when only one is left.</summary>
    public bool CanRemoveProfile => Profiles.Count > 1;

    [ObservableProperty]
    public partial ShellProfileEditorViewModel? DefaultProfile { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || _profiles is null)
        {
            return;
        }

        var profiles = await _profiles.GetProfilesAsync(cancellationToken).ConfigureAwait(true);
        var defaultProfile = await _profiles.GetDefaultProfileAsync(cancellationToken).ConfigureAwait(true);

        _loading = true;
        foreach (var existing in Profiles)
        {
            existing.PropertyChanged -= OnProfileEdited;
        }

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Add(ShellProfileEditorViewModel.FromProfile(profile, _filePicker));
        }

        // One profile is its own summary, so there is nothing to scan and collapsing it would only hide
        // the page's content behind a click. Past that, a list of collapsed headers is the point.
        if (Profiles.Count == 1)
        {
            Profiles[0].IsExpanded = true;
        }

        DefaultProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, defaultProfile.Id, StringComparison.Ordinal)) ?? Profiles.FirstOrDefault();
        _loading = false;
        IsDirty = false;
        _initialized = true;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ShellProfileEditorViewModel(_filePicker)
        {
            Id = $"profile-{Guid.NewGuid():N}",
            DisplayName = "New shell",
            ShellPath = string.Empty,
            WorkingDirectory = Environment.CurrentDirectory,
            Icon = ">_",
            // A profile you just asked for is one you are about to fill in.
            IsExpanded = true,
        };
        Add(profile);
        DefaultProfile ??= profile;
        MarkDirty();
    }

    /// <summary>Copies a profile in place — a new identity, everything else the same — because a second
    /// shell is almost always the first one with one argument changed, and retyping four fields to get
    /// there is how a typo enters an environment block.</summary>
    [RelayCommand]
    private void DuplicateProfile(ShellProfileEditorViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = new ShellProfileEditorViewModel(_filePicker)
        {
            Id = $"profile-{Guid.NewGuid():N}",
            DisplayName = $"{profile.DisplayName} (copy)",
            ShellPath = profile.ShellPath,
            ArgumentsText = profile.ArgumentsText,
            WorkingDirectory = profile.WorkingDirectory,
            EnvironmentText = profile.EnvironmentText,
            Icon = profile.Icon,
            IsExpanded = true,
        };

        // Beside what it was copied from, not at the end of the list: a copy that appears out of sight
        // below six other cards reads as nothing having happened.
        copy.PropertyChanged += OnProfileEdited;
        Profiles.Insert(Profiles.IndexOf(profile) + 1, copy);
        // Deliberately not the default. Duplicating is how a profile is tried out, and taking over what a
        // new tab opens with is not something a copy should do quietly.
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveProfile(ShellProfileEditorViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Profiles.Count == 1)
        {
            Status = "At least one shell profile is required.";
            return;
        }

        profile.PropertyChanged -= OnProfileEdited;
        _ = Profiles.Remove(profile);
        if (ReferenceEquals(DefaultProfile, profile))
        {
            DefaultProfile = Profiles[0];
        }

        MarkDirty();
    }

    [RelayCommand]
    private async Task SaveProfilesAsync(CancellationToken cancellationToken)
    {
        if (_profiles is null || DefaultProfile is null)
        {
            return;
        }

        try
        {
            // ToProfile parses the arguments and environment blocks, so a malformed NAME=value line is
            // caught here — before anything is written — rather than at the next attempt to start a shell.
            var profiles = Profiles.Select(profile => profile.ToProfile()).ToArray();
            await _profiles.SaveProfilesAsync(profiles, DefaultProfile.Id, cancellationToken).ConfigureAwait(true);
            IsDirty = false;
            Status = "Shell profiles saved.";
        }
        catch (Exception exception)
        {
            Status = $"Shell profiles could not be saved: {exception.Message}";
        }
    }

    // The header of a collapsed card has to say which profile a new tab opens with, so the flag lives on
    // each profile rather than only in the picker above the list.
    //
    // Choosing a different default is an edit: the id is written alongside the profiles, so a page that
    // did not say so would let someone leave with the choice unsaved and no warning.
    partial void OnDefaultProfileChanged(ShellProfileEditorViewModel? value)
    {
        foreach (var profile in Profiles)
        {
            profile.IsDefault = ReferenceEquals(profile, value);
        }

        if (!_loading && _initialized)
        {
            MarkDirty();
        }
    }

    private void Add(ShellProfileEditorViewModel profile)
    {
        profile.PropertyChanged += OnProfileEdited;
        Profiles.Add(profile);
    }

    // Expanding a card and being told which profile is the default are things the page does to itself.
    // Neither is an edit, and either one marking the page unsaved would make the warning meaningless.
    private void OnProfileEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading ||
            e.PropertyName is nameof(ShellProfileEditorViewModel.IsExpanded)
                or nameof(ShellProfileEditorViewModel.IsDefault))
        {
            return;
        }

        MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        Status = null;
    }
}

/// <summary>One row of the shell profile list, as text boxes. The picker is carried here rather than on
/// the page so the Browse buttons bind to the profile they sit beside; where there is no picker — a host
/// with no window, which is what the tests are — the buttons are hidden and the paths are still typed.
/// </summary>
public sealed partial class ShellProfileEditorViewModel(IFilePickerService? filePicker = null) : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShellPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArgumentsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkingDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EnvironmentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Icon { get; set; } = ">_";

    /// <summary>Whether this card is open. Per profile rather than one setting for the page, so opening one
    /// to change an argument does not unfold the other five.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Set by the page from its default picker. It is here so a collapsed header can say it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    public bool CanBrowse => filePicker is not null;

    /// <summary>What the header shows when the card is shut: enough to tell two profiles apart without
    /// opening either.</summary>
    public string Summary => string.IsNullOrWhiteSpace(ShellPath) ? "No executable set" : ShellPath;

    partial void OnShellPathChanged(string value) => OnPropertyChanged(nameof(Summary));

    public ShellProfile ToProfile()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in EnvironmentText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"Environment entry '{line}' must use NAME=value format.");
            }

            environment[line[..separator].Trim()] = line[(separator + 1)..];
        }

        return new ShellProfile
        {
            Id = Id,
            DisplayName = DisplayName,
            ShellPath = ShellPath,
            Arguments = ArgumentsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            WorkingDirectory = WorkingDirectory,
            EnvironmentVariables = environment,
            Icon = Icon,
        };
    }

    public static ShellProfileEditorViewModel FromProfile(
        ShellProfile profile,
        IFilePickerService? filePicker = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ShellProfileEditorViewModel(filePicker)
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            ShellPath = profile.ShellPath,
            ArgumentsText = string.Join(Environment.NewLine, profile.Arguments),
            WorkingDirectory = profile.WorkingDirectory,
            EnvironmentText = string.Join(Environment.NewLine, profile.EnvironmentVariables.Select(variable => $"{variable.Key}={variable.Value}")),
            Icon = profile.Icon,
        };
    }

    /// <summary>A dismissed dialog returns null, and the typed value is kept: browsing is a way to fill the
    /// box in, never a way to clear it.</summary>
    [RelayCommand]
    private async Task BrowseExecutableAsync()
    {
        if (filePicker is null)
        {
            return;
        }

        var picked = await filePicker
            .PickFileAsync($"Choose the executable for {Describe()}", ShellPath)
            .ConfigureAwait(true);
        if (picked is not null)
        {
            ShellPath = picked;
        }
    }

    [RelayCommand]
    private async Task BrowseWorkingDirectoryAsync()
    {
        if (filePicker is null)
        {
            return;
        }

        var picked = await filePicker
            .PickFolderAsync($"Choose the working directory for {Describe()}", WorkingDirectory)
            .ConfigureAwait(true);
        if (picked is not null)
        {
            WorkingDirectory = picked;
        }
    }

    private string Describe()
    {
        return string.IsNullOrWhiteSpace(DisplayName) ? "this profile" : DisplayName;
    }
}
