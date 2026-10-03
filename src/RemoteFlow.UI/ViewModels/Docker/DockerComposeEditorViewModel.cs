using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>The compose file open on the Compose tab: an existing project's file, or a new one that does not
/// exist on the server until it is saved. It only holds the text; reading, saving and bringing the project
/// up are the workspace's, which owns the connection.</summary>
public sealed partial class DockerComposeEditorViewModel : ObservableObject
{
    /// <summary>What a new file starts as: the smallest project that does something, to be edited rather
    /// than typed from nothing.</summary>
    public const string NewFileTemplate = """
        services:
          app:
            image: nginx:alpine
            restart: unless-stopped
            ports:
              - "8080:80"

        """;

    /// <summary>The file's own line endings, kept on save: the editor types "\n", and a file written on
    /// Windows should not come back with every line changed.</summary>
    private bool _usesCrlf;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(ShowsPlainPath))]
    public partial bool IsOpen { get; private set; }

    /// <summary>The file does not exist on the server yet, and its path is still being chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(HasFileChoice))]
    [NotifyPropertyChangedFor(nameof(ShowsPlainPath))]
    public partial bool IsNew { get; private set; }

    /// <summary>The project the file belongs to, when it belongs to one RemoteFlow lists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial DockerComposeProjectItemViewModel? Project { get; private set; }

    /// <summary>Every file of the project, for one that is made of several — a base file and an override.</summary>
    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    /// <summary>The file picked in the file list. Picking one asks the workspace to open it; until it has,
    /// this and <see cref="Path"/> differ.</summary>
    [ObservableProperty]
    public partial string? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>What the file held when it was opened or last saved — what a save expects still to find.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string OriginalText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>A new file is unsaved however little has been typed: nothing is on the server yet.</summary>
    public bool IsDirty => IsOpen && (IsNew || !string.Equals(Text, OriginalText, StringComparison.Ordinal));

    public bool HasFileChoice => !IsNew && Files.Count > 1;

    /// <summary>One existing file: its path is shown, not offered for choosing or typing.</summary>
    public bool ShowsPlainPath => IsOpen && !IsNew && Files.Count <= 1;

    public string Heading => IsNew
        ? "New compose file"
        : Project is { } project ? $"Compose file of {project.Name}" : "Compose file";

    /// <summary>The text as it is written to the server, in the file's own line endings.</summary>
    public string TextForSaving => Text.ReplaceLineEndings(_usesCrlf ? "\r\n" : "\n");

    public void Open(DockerComposeProjectItemViewModel? project, IReadOnlyList<string> files, string path, string text)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(text);
        Project = project;
        ResetFiles(files.Count > 0 ? files : [path]);
        IsNew = false;
        Path = path;
        SelectedFile = path;
        _usesCrlf = text.Contains("\r\n", StringComparison.Ordinal);
        OriginalText = text;
        Text = text;
        IsOpen = true;
    }

    public void BeginNew(string path)
    {
        Project = null;
        ResetFiles([]);
        IsNew = true;
        Path = path;
        SelectedFile = null;
        _usesCrlf = false;
        OriginalText = string.Empty;
        Text = NewFileTemplate;
        IsOpen = true;
    }

    /// <summary>What a save leaves behind: the file exists, and holds what was just written.</summary>
    public void MarkSaved(string path, string saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (IsNew)
        {
            ResetFiles([path]);
            IsNew = false;
        }

        Path = path;
        SelectedFile = path;
        OriginalText = saved;
        if (!string.Equals(Text, saved, StringComparison.Ordinal))
        {
            Text = saved;
        }
    }

    /// <summary>Ties a file saved as new to the project it became once it was brought up.</summary>
    public void AttachProject(DockerComposeProjectItemViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
        ResetFiles(project.ConfigFiles.Count > 0 ? project.ConfigFiles : [Path]);
        SelectedFile = Path;
    }

    public void Close()
    {
        IsOpen = false;
        IsNew = false;
        Project = null;
        ResetFiles([]);
        Path = string.Empty;
        SelectedFile = null;
        OriginalText = string.Empty;
        Text = string.Empty;
        _usesCrlf = false;
    }

    private void ResetFiles(IReadOnlyList<string> files)
    {
        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(file);
        }

        OnPropertyChanged(nameof(HasFileChoice));
        OnPropertyChanged(nameof(ShowsPlainPath));
    }
}
