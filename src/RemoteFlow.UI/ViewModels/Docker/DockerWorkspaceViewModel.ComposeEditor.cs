using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>The compose editor on the Compose tab: open a project's file, change it, save it, and bring the
/// project up with it — or start a new project from a file that does not exist yet. A save is checked with
/// <c>docker compose config</c> straight after, so a typo is found before an up would trip on it.</summary>
public sealed partial class DockerWorkspaceViewModel
{
    public DockerComposeEditorViewModel ComposeEditor { get; } = new();

    /// <summary>The session can read and write files; without that the editor stays shut.</summary>
    public bool CanEditComposeFiles => _session?.ComposeFiles is not null;

    [RelayCommand]
    public async Task EditComposeFileAsync(DockerComposeProjectItemViewModel? project)
    {
        project ??= SelectedProject;
        if (project is null || project.ConfigFiles.Count == 0 || !CanEditComposeFiles)
        {
            return;
        }

        if (await ConfirmDiscardComposeEditsAsync().ConfigureAwait(true))
        {
            await OpenComposeFileAsync(project, project.ConfigFiles, project.ConfigFiles[0]).ConfigureAwait(true);
        }
    }

    /// <summary>Starts a new compose file. A path already typed in the bring-up box is taken as where the
    /// new file goes.</summary>
    [RelayCommand]
    public async Task NewComposeFileAsync()
    {
        if (!CanEditComposeFiles || !await ConfirmDiscardComposeEditsAsync().ConfigureAwait(true))
        {
            return;
        }

        ComposeEditor.BeginNew(ComposeFilePath.Trim());
        ErrorMessage = null;
    }

    /// <summary>Another file of the same project was picked in the editor's file list.</summary>
    [RelayCommand]
    public async Task SwitchComposeFileAsync(string? path)
    {
        var editor = ComposeEditor;
        if (path is null || !editor.IsOpen || editor.IsNew || path == editor.Path)
        {
            return;
        }

        if (!await ConfirmDiscardComposeEditsAsync().ConfigureAwait(true))
        {
            editor.SelectedFile = editor.Path;
            return;
        }

        await OpenComposeFileAsync(editor.Project, [.. editor.Files], path).ConfigureAwait(true);
    }

    /// <summary>Reads the file from the server again, dropping what was typed — after another edit there
    /// has made a save refuse.</summary>
    [RelayCommand]
    public async Task ReloadComposeFileAsync()
    {
        var editor = ComposeEditor;
        if (editor.IsOpen && !editor.IsNew && await ConfirmDiscardComposeEditsAsync().ConfigureAwait(true))
        {
            await OpenComposeFileAsync(editor.Project, [.. editor.Files], editor.Path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public Task SaveComposeFileAsync()
    {
        return SaveComposeFileCoreAsync(bringUp: false);
    }

    [RelayCommand]
    public Task SaveComposeFileAndUpAsync()
    {
        return SaveComposeFileCoreAsync(bringUp: true);
    }

    [RelayCommand]
    public async Task CloseComposeEditorAsync()
    {
        if (ComposeEditor.IsOpen && await ConfirmDiscardComposeEditsAsync().ConfigureAwait(true))
        {
            ComposeEditor.Close();
        }
    }

    /// <summary>Asks before unsaved text is thrown away. True when there is nothing to lose, or it may go.</summary>
    private Task<bool> ConfirmDiscardComposeEditsAsync()
    {
        var editor = ComposeEditor;
        return !editor.IsDirty
            ? Task.FromResult(true)
            : _confirmation.ConfirmAsync(
                "Discard changes",
                editor.IsNew
                    ? "The new compose file has not been saved. Discard it?"
                    : $"'{editor.Path}' has changes that have not been saved. Discard them?",
                "Discard");
    }

    private async Task OpenComposeFileAsync(
        DockerComposeProjectItemViewModel? project,
        IReadOnlyList<string> files,
        string path)
    {
        var session = _session;
        if (session?.ComposeFiles is not { } store)
        {
            return;
        }

        ComposeEditor.IsBusy = true;
        ErrorMessage = null;
        try
        {
            var read = await store.ReadAsync(path).ConfigureAwait(true);
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            if (read.IsFailure)
            {
                ErrorMessage = read.Failure.Message;
                ComposeEditor.SelectedFile = ComposeEditor.IsOpen ? ComposeEditor.Path : null;
                return;
            }

            ComposeEditor.Open(project, files, path, read.Value);
            FeedbackMessage = $"Opened {path}.";
        }
        finally
        {
            ComposeEditor.IsBusy = false;
        }
    }

    private async Task SaveComposeFileCoreAsync(bool bringUp)
    {
        var session = _session;
        var editor = ComposeEditor;
        if (session?.ComposeFiles is not { } store || !editor.IsOpen || editor.IsBusy)
        {
            return;
        }

        var path = editor.Path.Trim();
        var saved = false;
        editor.IsBusy = true;
        ErrorMessage = null;
        FeedbackMessage = null;
        try
        {
            if (editor.IsDirty)
            {
                var text = editor.TextForSaving;
                var written = await store.WriteAsync(path, text, editor.IsNew ? null : editor.OriginalText).ConfigureAwait(true);
                if (written.IsFailure)
                {
                    ErrorMessage = written.Failure.Error == DockerError.FileChanged
                        ? $"Not saved: {written.Failure.Message} Reload to see what is there now — copy your changes first."
                        : $"Not saved: {written.Failure.Message}";
                    return;
                }

                editor.MarkSaved(path, text);
                saved = true;
            }

            // A file that is part of a project is checked with the rest of the project: an override on its
            // own is rarely a valid project.
            var files = editor.Project?.ConfigFiles is { Count: > 0 } projectFiles && projectFiles.Contains(path)
                ? projectFiles
                : [path];
            var valid = await session.Docker.ValidateComposeAsync(files).ConfigureAwait(true);
            if (valid.IsFailure)
            {
                ErrorMessage = saved
                    ? $"Saved, but compose finds a problem: {valid.Failure.Message}"
                    : $"Compose finds a problem: {valid.Failure.Message}";
                return;
            }

            FeedbackMessage = saved ? $"Saved {path}; compose accepts it." : $"No changes to save; compose accepts {path}.";
        }
        finally
        {
            editor.IsBusy = false;
        }

        if (bringUp)
        {
            await BringUpEditedProjectAsync(session, path).ConfigureAwait(true);
        }
    }

    /// <summary>A project RemoteFlow lists goes up the way its Up button would take it, with every one of its
    /// files. A file of no known project goes up on its own, and compose names the project — after which
    /// the editor belongs to the project it became.</summary>
    private async Task BringUpEditedProjectAsync(DockerWorkspaceSession session, string path)
    {
        if (ComposeEditor.Project is { } project)
        {
            await ComposeUpAsync(project).ConfigureAwait(true);
            return;
        }

        if (IsProduction(session) && !await _confirmation.ConfirmAsync(
                "Bring project up",
                $"{session.Definition.Name} is marked as production. Bring '{path}' up?",
                "Up").ConfigureAwait(true))
        {
            return;
        }

        if (await RunOperationAsync($"Compose up — {path}", new DockerOperation.ComposeUp(null, [path]), null)
                .ConfigureAwait(true)
            && ComposeEditor.IsOpen
            && ComposeEditor.Path == path
            && _projects.FirstOrDefault(known => known.ConfigFiles.Contains(path, StringComparer.Ordinal)) is { } became)
        {
            ComposeEditor.AttachProject(became);
        }
    }
}
