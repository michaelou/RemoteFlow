using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>The Compose, Images and Volumes tabs. Removing a single image or volume is quick and runs like a
/// container action; anything that can run for minutes — compose up and down, a pull, a prune — runs in the
/// output pane, where its progress can be read as it happens and the lists refresh once it has ended.</summary>
public sealed partial class DockerWorkspaceViewModel
{
    private readonly List<DockerImageItemViewModel> _images = [];
    private readonly List<DockerVolumeItemViewModel> _volumes = [];
    private readonly List<DockerComposeProjectItemViewModel> _projects = [];

    public ObservableCollection<DockerComposeProjectItemViewModel> ComposeProjects { get; } = [];

    public ObservableCollection<DockerImageItemViewModel> Images { get; } = [];

    public ObservableCollection<DockerVolumeItemViewModel> Volumes { get; } = [];

    [ObservableProperty]
    public partial DockerComposeProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    public partial DockerImageItemViewModel? SelectedImage { get; set; }

    [ObservableProperty]
    public partial DockerVolumeItemViewModel? SelectedVolume { get; set; }

    /// <summary>Why the Compose tab is empty when it is not simply empty — the plugin is missing, say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoComposeProjects))]
    public partial string? ComposeMessage { get; private set; }

    public bool HasNoComposeProjects => IsConnected && ComposeMessage is null && ComposeProjects.Count == 0;

    public bool HasNoImages => IsConnected && Images.Count == 0;

    public bool HasNoVolumes => IsConnected && Volumes.Count == 0;

    /// <summary>A compose file on the server to bring up — for a project RemoteFlow has never seen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComposeUpFromFileCommand))]
    public partial string ComposeFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullImageCommand))]
    public partial string PullReference { get; set; } = string.Empty;

    [RelayCommand]
    public async Task ComposeUpAsync(DockerComposeProjectItemViewModel? project)
    {
        project ??= SelectedProject;
        var session = _session;
        if (project is null || session is null || !project.CanUp)
        {
            return;
        }

        if (IsProduction(session) && !await _confirmation.ConfirmAsync(
                "Bring project up",
                $"{session.Definition.Name} is marked as production. Bring '{project.Name}' up? Containers whose " +
                "configuration changed are recreated.",
                "Up").ConfigureAwait(true))
        {
            return;
        }

        _ = await RunOperationAsync(
            $"Compose up — {project.Name}",
            new DockerOperation.ComposeUp(project.Name, project.ConfigFiles),
            project).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanComposeUpFromFile))]
    public async Task ComposeUpFromFileAsync()
    {
        var path = ComposeFilePath.Trim();
        if (_session is null || !DockerCli.IsValidComposeFile(path))
        {
            return;
        }

        if (await RunOperationAsync($"Compose up — {path}", new DockerOperation.ComposeUp(null, [path]), null)
            .ConfigureAwait(true))
        {
            ComposeFilePath = string.Empty;
        }
    }

    [RelayCommand]
    public Task ComposeDownAsync(DockerComposeProjectItemViewModel? project)
    {
        return ComposeDownCoreAsync(project ?? SelectedProject, removeVolumes: false);
    }

    [RelayCommand]
    public Task ComposeDownWithVolumesAsync(DockerComposeProjectItemViewModel? project)
    {
        return ComposeDownCoreAsync(project ?? SelectedProject, removeVolumes: true);
    }

    /// <summary>Drops a project that is down from the list, and from what RemoteFlow remembers. Nothing on
    /// the server changes.</summary>
    [RelayCommand]
    public async Task ForgetProjectAsync(DockerComposeProjectItemViewModel? project)
    {
        project ??= SelectedProject;
        var session = _session;
        if (project is null || session is null || !project.CanForget)
        {
            return;
        }

        if (_composeMemory is not null)
        {
            await _composeMemory.ForgetAsync(session.Definition.Id, project.Name).ConfigureAwait(true);
        }

        _ = _projects.Remove(project);
        ApplyResourceFilters();
        FeedbackMessage = $"Forgot {project.Name}.";
    }

    [RelayCommand]
    public async Task RemoveImageAsync(DockerImageItemViewModel? image)
    {
        image ??= SelectedImage;
        var session = _session;
        if (image is null || session is null || !image.CanRemove)
        {
            return;
        }

        if (!await _confirmation.ConfirmAsync(
                "Remove image",
                $"Remove the image '{image.Reference}' from {session.Definition.Name}? If the image carries other " +
                "tags, only this one is removed.",
                "Remove").ConfigureAwait(true))
        {
            return;
        }

        image.IsBusy = true;
        try
        {
            await RunQuickAsync(
                () => session.Docker.RemoveImageAsync(image.Reference),
                $"Removed {image.Reference}.",
                $"'{image.Reference}' could not be removed").ConfigureAwait(true);
        }
        finally
        {
            image.IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPullImage))]
    public async Task PullImageAsync()
    {
        var reference = PullReference.Trim();
        if (_session is null || !DockerCli.IsValidImageReference(reference))
        {
            ErrorMessage = $"'{reference}' is not an image reference Docker would accept.";
            return;
        }

        if (await RunOperationAsync($"Pull — {reference}", new DockerOperation.PullImage(reference), null)
            .ConfigureAwait(true))
        {
            PullReference = string.Empty;
        }
    }

    [RelayCommand]
    public async Task PruneImagesAsync()
    {
        var session = _session;
        if (session is null || !await _confirmation.ConfirmAsync(
                "Prune dangling images",
                $"Remove every dangling image on {session.Definition.Name} — the untagged layers left behind when a " +
                "tag moves to a newer build? Tagged images are kept.",
                "Prune").ConfigureAwait(true))
        {
            return;
        }

        _ = await RunOperationAsync("Prune dangling images", new DockerOperation.PruneImages(), null).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task RemoveVolumeAsync(DockerVolumeItemViewModel? volume)
    {
        volume ??= SelectedVolume;
        var session = _session;
        if (volume is null || session is null || !volume.CanRemove)
        {
            return;
        }

        if (!await _confirmation.ConfirmAsync(
                "Delete volume",
                $"Delete the volume '{volume.Name}' on {session.Definition.Name}, and everything stored in it? This " +
                "cannot be undone.",
                "Delete").ConfigureAwait(true))
        {
            return;
        }

        volume.IsBusy = true;
        try
        {
            await RunQuickAsync(
                () => session.Docker.RemoveVolumeAsync(volume.Name),
                $"Deleted {volume.Name}.",
                $"'{volume.Name}' could not be deleted").ConfigureAwait(true);
        }
        finally
        {
            volume.IsBusy = false;
        }
    }

    /// <summary>What <c>docker volume prune</c> removes depends on the engine: from Docker 23, anonymous
    /// volumes only; before that, every unused volume, named ones included. The confirmation says so rather
    /// than promising the newer behaviour.</summary>
    [RelayCommand]
    public async Task PruneVolumesAsync()
    {
        var session = _session;
        if (session is null || !await _confirmation.ConfirmAsync(
                "Prune unused volumes",
                $"Delete the volumes no container uses on {session.Definition.Name}, and their data? Docker 23 and " +
                "later remove only anonymous volumes; older engines remove named ones too. This cannot be undone.",
                "Prune").ConfigureAwait(true))
        {
            return;
        }

        _ = await RunOperationAsync("Prune unused volumes", new DockerOperation.PruneVolumes(), null).ConfigureAwait(true);
    }

    private bool CanComposeUpFromFile()
    {
        return DockerCli.IsValidComposeFile(ComposeFilePath.Trim());
    }

    private bool CanPullImage()
    {
        return !string.IsNullOrWhiteSpace(PullReference);
    }

    private async Task ComposeDownCoreAsync(DockerComposeProjectItemViewModel? project, bool removeVolumes)
    {
        var session = _session;
        if (project is null || session is null || !project.CanDown)
        {
            return;
        }

        var production = IsProduction(session) ? $"{session.Definition.Name} is marked as production. " : string.Empty;
        var confirmed = removeVolumes
            ? await _confirmation.ConfirmAsync(
                "Bring project down and delete its volumes",
                $"{production}Stop and remove the containers and networks of '{project.Name}', and delete its " +
                "named volumes with everything stored in them? This cannot be undone.",
                "Delete volumes").ConfigureAwait(true)
            : await _confirmation.ConfirmAsync(
                "Bring project down",
                $"{production}Stop and remove the containers and networks of '{project.Name}'? Volumes and images " +
                "are kept, and the project can be brought up again from here.",
                "Down").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        _ = await RunOperationAsync(
            removeVolumes ? $"Compose down and delete volumes — {project.Name}" : $"Compose down — {project.Name}",
            new DockerOperation.ComposeDown(project.Name, removeVolumes),
            project).ConfigureAwait(true);
    }

    /// <summary>Runs an operation in the output pane and refreshes every list once it ends, since an up, a
    /// down or a prune changes containers, images and volumes at once. True when it finished cleanly.</summary>
    private async Task<bool> RunOperationAsync(
        string heading,
        DockerOperation operation,
        DockerComposeProjectItemViewModel? project)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        project?.IsBusy = true;

        ErrorMessage = null;
        FeedbackMessage = $"{heading}…";
        try
        {
            var outcome = await Logs.RunOperationAsync(
                heading,
                cancellationToken => session.Docker.StartOperationAsync(operation, cancellationToken)).ConfigureAwait(true);
            if (outcome is null)
            {
                FeedbackMessage = null;
                ErrorMessage = $"Wait for '{Logs.Heading}' to finish first.";
                return false;
            }

            FeedbackMessage = outcome.Succeeded ? $"{heading}: finished." : null;
            if (!outcome.Succeeded)
            {
                ErrorMessage = $"{heading}: {outcome.Message} The output is in the pane below.";
            }

            return outcome.Succeeded;
        }
        finally
        {
            project?.IsBusy = false;

            await RefreshEverythingAsync(session).ConfigureAwait(true);
        }
    }

    private async Task RunQuickAsync(Func<Task<DockerResult>> action, string done, string failed)
    {
        var session = _session;
        ErrorMessage = null;
        var result = await action().ConfigureAwait(true);
        if (result.IsFailure)
        {
            ErrorMessage = $"{failed}: {result.Failure.Message}";
        }
        else
        {
            FeedbackMessage = done;
        }

        if (session is not null)
        {
            await RefreshTabAsync(session, SelectedTab, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Containers first, because the other lists work out what is in use from them.</summary>
    private async Task RefreshEverythingAsync(DockerWorkspaceSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        var listed = await session.Docker.ListContainersAsync().ConfigureAwait(true);
        if (listed.IsSuccess && ReferenceEquals(session, _session))
        {
            Merge(listed.Value);
        }

        foreach (var tab in new[] { DockerTab.Compose, DockerTab.Images, DockerTab.Volumes })
        {
            await RefreshTabAsync(session, tab, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task RefreshTabAsync(DockerWorkspaceSession session, DockerTab tab, CancellationToken cancellationToken)
    {
        switch (tab)
        {
            case DockerTab.Compose:
                await RefreshComposeAsync(session, cancellationToken).ConfigureAwait(true);
                break;
            case DockerTab.Images:
                var images = await session.Docker.ListImagesAsync(cancellationToken).ConfigureAwait(true);
                if (ReferenceEquals(session, _session))
                {
                    if (images.IsSuccess)
                    {
                        MergeImages(images.Value);
                    }
                    else
                    {
                        ErrorMessage = images.Failure.Message;
                    }
                }

                break;
            case DockerTab.Volumes:
                var volumes = await session.Docker.ListVolumesAsync(cancellationToken).ConfigureAwait(true);
                if (ReferenceEquals(session, _session))
                {
                    if (volumes.IsSuccess)
                    {
                        MergeVolumes(volumes.Value);
                    }
                    else
                    {
                        ErrorMessage = volumes.Failure.Message;
                    }
                }

                break;
            case DockerTab.Containers:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tab));
        }
    }

    /// <summary>The projects compose lists, plus the ones RemoteFlow remembers that it no longer does. A
    /// missing plugin is the tab's message, not the page's error: the other three tabs still work.</summary>
    private async Task RefreshComposeAsync(DockerWorkspaceSession session, CancellationToken cancellationToken)
    {
        var listed = await session.Docker.ListComposeProjectsAsync(cancellationToken).ConfigureAwait(true);
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        if (listed.IsFailure)
        {
            ComposeMessage = listed.Failure.Message;
            return;
        }

        ComposeMessage = null;
        var connectionId = session.Definition.Id;
        IReadOnlyList<RememberedComposeProject> remembered = [];
        if (_composeMemory is not null)
        {
            await _composeMemory.RememberAsync(connectionId, listed.Value, cancellationToken).ConfigureAwait(true);
            remembered = await _composeMemory.RecallAsync(connectionId, cancellationToken).ConfigureAwait(true);
        }

        var byName = _projects.ToDictionary(project => project.Name, StringComparer.Ordinal);
        _projects.Clear();
        foreach (var project in listed.Value)
        {
            var files = project.ConfigFiles.Count > 0
                ? project.ConfigFiles
                : remembered.FirstOrDefault(known => known.Name == project.Name)?.ConfigFiles ?? [];
            _projects.Add(Reuse(byName, project.Name, isListed: true, project.Status, files));
        }

        foreach (var known in remembered.Where(known => listed.Value.All(project => project.Name != known.Name)))
        {
            _projects.Add(Reuse(byName, known.Name, isListed: false, string.Empty, known.ConfigFiles));
        }

        ApplyResourceFilters();
    }

    private static DockerComposeProjectItemViewModel Reuse(
        Dictionary<string, DockerComposeProjectItemViewModel> existing,
        string name,
        bool isListed,
        string status,
        IReadOnlyList<string> files)
    {
        if (!existing.TryGetValue(name, out var item))
        {
            item = new DockerComposeProjectItemViewModel(name);
        }

        item.Update(isListed, status, files);
        return item;
    }

    private void MergeImages(IReadOnlyList<DockerImage> images)
    {
        var byKey = _images.ToDictionary(image => image.Key, StringComparer.Ordinal);
        _images.Clear();
        foreach (var image in images)
        {
            var usedBy = ContainersUsing(image);
            var key = $"{image.Id}|{image.Reference}";
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Update(image, usedBy);
                _images.Add(existing);
            }
            else
            {
                _images.Add(new DockerImageItemViewModel(image, usedBy));
            }
        }

        ApplyResourceFilters();
    }

    private void MergeVolumes(IReadOnlyList<DockerVolume> volumes)
    {
        var byName = _volumes.ToDictionary(volume => volume.Name, StringComparer.Ordinal);
        _volumes.Clear();
        foreach (var volume in volumes)
        {
            var usedBy = _all
                .Where(container => container.Container.VolumeNames.Contains(volume.Name, StringComparer.Ordinal))
                .Select(container => container.Name)
                .ToList();
            if (byName.TryGetValue(volume.Name, out var existing))
            {
                existing.Update(volume, usedBy);
                _volumes.Add(existing);
            }
            else
            {
                _volumes.Add(new DockerVolumeItemViewModel(volume, usedBy));
            }
        }

        ApplyResourceFilters();
    }

    /// <summary>A container names its image the way it was started: by reference, by a reference without its
    /// <c>:latest</c>, or by ID.</summary>
    private List<string> ContainersUsing(DockerImage image)
    {
        return [.. _all
            .Where(container =>
                string.Equals(container.Image, image.Reference, StringComparison.Ordinal)
                || (image.Tag == "latest" && string.Equals(container.Image, image.Repository, StringComparison.Ordinal))
                || (container.Image.Length >= 12 && image.Id.StartsWith(container.Image.Replace("sha256:", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
                || (container.Image.Length >= 12 && container.Image.Replace("sha256:", string.Empty, StringComparison.Ordinal).StartsWith(image.Id, StringComparison.Ordinal)))
            .Select(container => container.Name)];
    }

    private void ApplyResourceFilters()
    {
        var filter = FilterText.Trim();
        Reconcile(
            ComposeProjects,
            [.. _projects
                .Where(project => filter.Length == 0 || project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(project => project.IsListed ? 0 : 1)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)]);
        Reconcile(
            Images,
            [.. _images
                .Where(image => filter.Length == 0
                    || image.Repository.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || image.Tag.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || image.Id.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(image => image.Image.Repository is null ? 1 : 0)
                .ThenBy(image => image.Repository, StringComparer.OrdinalIgnoreCase)
                .ThenBy(image => image.Tag, StringComparer.OrdinalIgnoreCase)]);
        Reconcile(
            Volumes,
            [.. _volumes
                .Where(volume => filter.Length == 0
                    || volume.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (volume.Volume.ComposeProject?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(volume => volume.Volume.ComposeProject is null ? 1 : 0)
                .ThenBy(volume => volume.Volume.ComposeProject, StringComparer.OrdinalIgnoreCase)
                .ThenBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase)]);
        UpdateSummary();
        OnPropertyChanged(nameof(HasNoComposeProjects));
        OnPropertyChanged(nameof(HasNoImages));
        OnPropertyChanged(nameof(HasNoVolumes));
    }

    /// <summary>Moves rows rather than rebuilding the list: a Clear would drop the selection on every refresh.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, List<T> desired)
        where T : class
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var current = target.IndexOf(desired[index]);
            if (current < 0)
            {
                target.Insert(index, desired[index]);
            }
            else if (current != index)
            {
                target.Move(current, index);
            }
        }
    }

    private void UpdateSummary()
    {
        Summary = SelectedTab switch
        {
            DockerTab.Containers => _all.Count == 0
                ? string.Empty
                : $"{_all.Count} {(_all.Count == 1 ? "container" : "containers")}, {_all.Count(item => item.IsRunning)} running",
            DockerTab.Compose => _projects.Count == 0
                ? string.Empty
                : $"{_projects.Count} {(_projects.Count == 1 ? "project" : "projects")}, {_projects.Count(project => project.IsRunning)} running",
            DockerTab.Images => _images.Count == 0
                ? string.Empty
                : $"{_images.Count} {(_images.Count == 1 ? "image" : "images")}, {_images.Count(image => !image.IsInUse)} unused",
            DockerTab.Volumes => _volumes.Count == 0
                ? string.Empty
                : $"{_volumes.Count} {(_volumes.Count == 1 ? "volume" : "volumes")}, {_volumes.Count(volume => !volume.IsInUse)} unused",
            _ => string.Empty,
        };
    }

    private void ClearResources()
    {
        _projects.Clear();
        _images.Clear();
        _volumes.Clear();
        ComposeProjects.Clear();
        Images.Clear();
        Volumes.Clear();
        ComposeMessage = null;
        OnPropertyChanged(nameof(HasNoComposeProjects));
        OnPropertyChanged(nameof(HasNoImages));
        OnPropertyChanged(nameof(HasNoVolumes));
    }

    private static bool IsProduction(DockerWorkspaceSession session)
    {
        return session.Definition.Environment == EnvironmentKind.Production;
    }
}
