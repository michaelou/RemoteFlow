using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.Views.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The Shell profiles tab. It is the only settings page with a Save button, so what is on screen
/// and what is stored are two different things here — and the browse buttons are the only place in
/// settings where a dialog writes into a field someone may also have typed into.</summary>
public sealed class ShellProfilesViewModelTests
{
    [Fact]
    public async Task ProfilesLoadCleanAndAnEditMarksThePageUnsaved()
    {
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("Developer shell", profile.DisplayName);
        Assert.Same(profile, viewModel.DefaultProfile);
        // Loading is not editing: a page that opens claiming unsaved changes teaches people to ignore it.
        Assert.False(viewModel.IsDirty);

        profile.ShellPath = "/bin/zsh";

        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task SavingWritesEveryProfileAndTheDefaultAndClearsTheUnsavedMark()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);
        await viewModel.InitializeAsync(token);

        viewModel.AddProfileCommand.Execute(null);
        var added = viewModel.Profiles[1];
        added.DisplayName = "Fish";
        added.ShellPath = "/usr/bin/fish";
        added.ArgumentsText = "--login";
        added.EnvironmentText = "SHELL_MARKER=fish";
        viewModel.DefaultProfile = added;

        await viewModel.SaveProfilesCommand.ExecuteAsync(null);

        Assert.Equal("Shell profiles saved.", viewModel.Status);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(added.Id, service.SavedDefaultId);
        var saved = Assert.Single(service.Saved!, profile => profile.Id == added.Id);
        Assert.Equal("/usr/bin/fish", saved.ShellPath);
        Assert.Equal(["--login"], saved.Arguments);
        Assert.Equal("fish", saved.EnvironmentVariables["SHELL_MARKER"]);
    }

    // A malformed environment line is caught before anything is written, so a bad NAME=value cannot leave
    // half a list of profiles stored.
    [Fact]
    public async Task AProfileThatWillNotParseIsReportedAndNothingIsWritten()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);
        await viewModel.InitializeAsync(token);
        viewModel.Profiles[0].EnvironmentText = "NOT_A_PAIR";

        await viewModel.SaveProfilesCommand.ExecuteAsync(null);

        Assert.Null(service.Saved);
        Assert.True(viewModel.IsDirty);
        Assert.Contains("NAME=value", viewModel.Status!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastProfileCannotBeRemoved()
    {
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.CanRemoveProfile);
        viewModel.RemoveProfileCommand.Execute(viewModel.Profiles[0]);

        _ = Assert.Single(viewModel.Profiles);
        Assert.Equal("At least one shell profile is required.", viewModel.Status);
    }

    [Fact]
    public async Task RemovingTheDefaultProfileHandsTheRoleToWhatIsLeft()
    {
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        viewModel.AddProfileCommand.Execute(null);
        var original = viewModel.Profiles[0];

        Assert.True(viewModel.CanRemoveProfile);
        viewModel.RemoveProfileCommand.Execute(original);

        Assert.DoesNotContain(original, viewModel.Profiles);
        Assert.Same(viewModel.Profiles[0], viewModel.DefaultProfile);
    }

    [Fact]
    public async Task BrowsingFillsTheExecutableAndTheWorkingDirectoryFromTheDialog()
    {
        var token = TestContext.Current.CancellationToken;
        var picker = new RecordingFilePicker
        {
            File = "/opt/tools/fish",
            Folder = "/srv/projects",
        };
        var viewModel = new ShellProfilesViewModel(new RecordingProfileService(Profile("dev", "Developer shell")), picker);
        await viewModel.InitializeAsync(token);
        var profile = viewModel.Profiles[0];

        await profile.BrowseExecutableCommand.ExecuteAsync(null);
        await profile.BrowseWorkingDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("/opt/tools/fish", profile.ShellPath);
        Assert.Equal("/srv/projects", profile.WorkingDirectory);
        // The dialog opens where the field already points, so browsing from a filled-in profile does not
        // start over at the home directory.
        Assert.Equal("C:\\Tools\\shell.exe", picker.FileSuggestions[0]);
        Assert.Equal("C:\\projects\\remote-flow", picker.FolderSuggestions[0]);
    }

    // A dismissed dialog returns null, and the typed value has to survive it: browsing is a way to fill a
    // box in, never a way to empty one.
    [Fact]
    public async Task ADismissedDialogLeavesWhatWasAlreadyTyped()
    {
        var token = TestContext.Current.CancellationToken;
        var viewModel = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell")),
            new RecordingFilePicker());
        await viewModel.InitializeAsync(token);
        var profile = viewModel.Profiles[0];

        await profile.BrowseExecutableCommand.ExecuteAsync(null);
        await profile.BrowseWorkingDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("C:\\Tools\\shell.exe", profile.ShellPath);
        Assert.Equal("C:\\projects\\remote-flow", profile.WorkingDirectory);
    }

    /// <summary>Without a picker there is no dialog to open, and a button that cannot do anything is worse
    /// than no button beside a field that still takes a typed path.</summary>
    [Fact]
    public async Task WithoutAPickerTheBrowseButtonsAreNotOffered()
    {
        var viewModel = new ShellProfilesViewModel(new RecordingProfileService(Profile("dev", "Developer shell")));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.Profiles[0].CanBrowse);
    }

    /// <summary>Through the control tree rather than the view model, because a view that binds none of
    /// these commands passes every other test in this file.</summary>
    [AvaloniaFact]
    public async Task TheViewBindsAddSaveAndTheTwoBrowseButtons()
    {
        var picker = new RecordingFilePicker { File = "/opt/tools/fish", Folder = "/srv/projects" };
        var viewModel = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell")),
            picker);
        var view = new ShellProfilesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Show();
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        window.UpdateLayout();

        var buttons = view.GetLogicalDescendants().OfType<Button>().ToArray();
        var browseExecutable = Named(buttons, "Browse for the executable");
        var browseWorkingDirectory = Named(buttons, "Browse for the working directory");

        Execute(Named(buttons, "Add profile"));
        Assert.Equal(2, viewModel.Profiles.Count);

        window.UpdateLayout();
        Execute(browseExecutable);
        Execute(browseWorkingDirectory);
        Assert.Equal("/opt/tools/fish", viewModel.Profiles[0].ShellPath);
        Assert.Equal("/srv/projects", viewModel.Profiles[0].WorkingDirectory);

        window.Close();
    }

    private static Button Named(IReadOnlyList<Button> buttons, string name)
    {
        return Assert.Single(buttons, button =>
            string.Equals(Avalonia.Automation.AutomationProperties.GetName(button), name, StringComparison.Ordinal) ||
            string.Equals(button.Content as string, name, StringComparison.Ordinal));
    }

    // Invoking the command rather than synthesising a pointer press: the assertion is that the button is
    // bound to the right command, and a headless click adds hit-testing to the list of things that could
    // make this fail for reasons that are not about this page.
    private static void Execute(Button button)
    {
        Assert.NotNull(button.Command);
        button.Command.Execute(button.CommandParameter);
    }

    /// <summary>A page of eight profiles that each take a third of a screen is a page you scroll rather
    /// than read, so a card opens only when there is a reason for it to be open.</summary>
    [Fact]
    public async Task SeveralProfilesLoadCollapsedAndASingleOneLoadsOpen()
    {
        var token = TestContext.Current.CancellationToken;
        var many = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell"), Profile("ops", "Ops shell")));
        await many.InitializeAsync(token);

        Assert.All(many.Profiles, profile => Assert.False(profile.IsExpanded));

        var one = new ShellProfilesViewModel(new RecordingProfileService(Profile("dev", "Developer shell")));
        await one.InitializeAsync(token);

        Assert.True(Assert.Single(one.Profiles).IsExpanded);
    }

    // Opening a card is reading, not editing. If it marked the page unsaved, the warning would fire on
    // every visit and stop meaning anything.
    [Fact]
    public async Task ExpandingACardIsNotAnEdit()
    {
        var token = TestContext.Current.CancellationToken;
        var viewModel = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell"), Profile("ops", "Ops shell")));
        await viewModel.InitializeAsync(token);

        viewModel.Profiles[1].IsExpanded = true;

        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task AProfileIsAlwaysMarkedWithWhetherANewTabOpensWithIt()
    {
        var token = TestContext.Current.CancellationToken;
        var viewModel = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell"), Profile("ops", "Ops shell")));
        await viewModel.InitializeAsync(token);

        Assert.True(viewModel.Profiles[0].IsDefault);
        Assert.False(viewModel.Profiles[1].IsDefault);

        viewModel.DefaultProfile = viewModel.Profiles[1];

        Assert.False(viewModel.Profiles[0].IsDefault);
        Assert.True(viewModel.Profiles[1].IsDefault);
        // The default id is written alongside the profiles, so choosing a different one is an unsaved
        // change like any other.
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task DuplicatingCopiesEveryFieldNextToTheOriginalUnderANewIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        var viewModel = new ShellProfilesViewModel(
            new RecordingProfileService(Profile("dev", "Developer shell"), Profile("ops", "Ops shell")));
        await viewModel.InitializeAsync(token);
        var original = viewModel.Profiles[0];
        original.ArgumentsText = "--interactive";
        original.EnvironmentText = "PROFILE_MARKER=developer";

        viewModel.DuplicateProfileCommand.Execute(original);

        Assert.Equal(3, viewModel.Profiles.Count);
        // Beside what it was copied from, not at the end of the list.
        var copy = viewModel.Profiles[1];
        Assert.Equal("Developer shell (copy)", copy.DisplayName);
        Assert.Equal(original.ShellPath, copy.ShellPath);
        Assert.Equal(original.WorkingDirectory, copy.WorkingDirectory);
        Assert.Equal(original.ArgumentsText, copy.ArgumentsText);
        Assert.Equal(original.EnvironmentText, copy.EnvironmentText);
        Assert.Equal(original.Icon, copy.Icon);
        // A copy is opened, because it exists to be changed.
        Assert.True(copy.IsExpanded);
        // A distinct identity, or saving would write one profile over the other.
        Assert.NotEqual(original.Id, copy.Id);
        // And it does not quietly take over what a new tab opens with.
        Assert.Same(original, viewModel.DefaultProfile);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task ADuplicateIsSavedAsAProfileOfItsOwn()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new RecordingProfileService(Profile("dev", "Developer shell"));
        var viewModel = new ShellProfilesViewModel(service);
        await viewModel.InitializeAsync(token);
        viewModel.DuplicateProfileCommand.Execute(viewModel.Profiles[0]);

        await viewModel.SaveProfilesCommand.ExecuteAsync(null);

        Assert.Equal(2, service.Saved!.Count);
        Assert.Equal(2, service.Saved.Select(profile => profile.Id).Distinct().Count());
        Assert.Contains(service.Saved, profile => profile.DisplayName == "Developer shell (copy)");
    }

    // Editing a copy must not reach back into what it was copied from: the two share every value at the
    // moment of duplication and nothing afterwards.
    [Fact]
    public async Task EditingACopyLeavesTheOriginalAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var viewModel = new ShellProfilesViewModel(new RecordingProfileService(Profile("dev", "Developer shell")));
        await viewModel.InitializeAsync(token);
        var original = viewModel.Profiles[0];
        viewModel.DuplicateProfileCommand.Execute(original);

        viewModel.Profiles[1].ShellPath = "/usr/bin/fish";

        Assert.Equal("C:\\Tools\\shell.exe", original.ShellPath);
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

    private sealed class RecordingProfileService(params ShellProfile[] profiles) : IShellProfileService
    {
        public event EventHandler? ProfilesChanged;

        public IReadOnlyList<ShellProfile>? Saved { get; private set; }

        public string? SavedDefaultId { get; private set; }

        public Task<IReadOnlyList<ShellProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ShellProfile>>(profiles);
        }

        public Task<ShellProfile> GetDefaultProfileAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(profiles[0]);
        }

        public Task SaveProfilesAsync(
            IReadOnlyList<ShellProfile> profiles,
            string defaultProfileId,
            CancellationToken cancellationToken = default)
        {
            Saved = profiles;
            SavedDefaultId = defaultProfileId;
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public PtySpawnOptions CreateSpawnOptions(ShellProfile value)
        {
            return new PtySpawnOptions
            {
                ShellPath = value.ShellPath,
                Arguments = value.Arguments,
                WorkingDirectory = value.WorkingDirectory,
                EnvironmentVariables = value.EnvironmentVariables,
            };
        }
    }

    /// <summary>Answers with whatever it was given, and null — a dismissed dialog — when it was given
    /// nothing.</summary>
    private sealed class RecordingFilePicker : IFilePickerService
    {
        public string? File { get; init; }

        public string? Folder { get; init; }

        public List<string?> FileSuggestions { get; } = [];

        public List<string?> FolderSuggestions { get; } = [];

        public Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickDownloadFolderAsync(
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            return PickFolderAsync("Choose download folder", suggestedPath, cancellationToken);
        }

        public Task<string?> PickFolderAsync(
            string title,
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            FolderSuggestions.Add(suggestedPath);
            return Task.FromResult(Folder);
        }

        public Task<string?> PickFileAsync(
            string title,
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            FileSuggestions.Add(suggestedPath);
            return Task.FromResult(File);
        }
    }
}
