using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions.Docker;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>The tabs of the Docker page, in the order they appear.</summary>
public enum DockerTab
{
    Containers = 0,
    Compose = 1,
    Images = 2,
    Volumes = 3,
}

/// <summary>A row on the Images tab. "In use" comes from the engine's own count where it reports one, and
/// otherwise from the container list: a container naming the image by reference or by ID.</summary>
public sealed partial class DockerImageItemViewModel(DockerImage image, IReadOnlyList<string> usedBy) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Repository))]
    [NotifyPropertyChangedFor(nameof(Tag))]
    [NotifyPropertyChangedFor(nameof(Id))]
    [NotifyPropertyChangedFor(nameof(Size))]
    [NotifyPropertyChangedFor(nameof(Created))]
    [NotifyPropertyChangedFor(nameof(Reference))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial DockerImage Image { get; private set; } = image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInUse))]
    [NotifyPropertyChangedFor(nameof(UsedByText))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial IReadOnlyList<string> UsedBy { get; private set; } = usedBy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsBusy { get; set; }

    /// <summary>Identifies the row across refreshes: an ID can carry several tags, each its own row.</summary>
    public string Key => $"{Image.Id}|{Image.Reference}";

    public string Repository => Image.Repository ?? "<none>";

    public string Tag => Image.Tag ?? "<none>";

    public string Id => Image.Id;

    public string Size => Image.Size;

    public string Created => Image.CreatedSince;

    public string Reference => Image.Reference;

    public bool IsInUse => UsedBy.Count > 0 || Image.Containers > 0;

    public string UsedByText => UsedBy.Count > 0
        ? string.Join(", ", UsedBy)
        : Image.Containers is > 0 and var count ? $"{count} {(count == 1 ? "container" : "containers")}" : "—";

    /// <summary>Docker refuses to remove an image a container uses, and RemoteFlow never forces it.</summary>
    public bool CanRemove => !IsBusy && !IsInUse;

    public string AccessibleName => $"{Repository}:{Tag}, {Size}, {(IsInUse ? "in use" : "unused")}";

    public void Update(DockerImage image, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(usedBy);
        if (image != Image)
        {
            Image = image;
        }

        if (!usedBy.SequenceEqual(UsedBy))
        {
            UsedBy = usedBy;
        }
    }
}

/// <summary>A row on the Volumes tab. Which containers mount it comes from the container list, stopped
/// containers included: Docker refuses to remove a volume any container still names.</summary>
public sealed partial class DockerVolumeItemViewModel(DockerVolume volume, IReadOnlyList<string> usedBy) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Project))]
    [NotifyPropertyChangedFor(nameof(Driver))]
    public partial DockerVolume Volume { get; private set; } = volume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInUse))]
    [NotifyPropertyChangedFor(nameof(UsedByText))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial IReadOnlyList<string> UsedBy { get; private set; } = usedBy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsBusy { get; set; }

    public string Name => Volume.Name;

    public string Project => Volume.ComposeProject ?? "—";

    public string Driver => Volume.Driver;

    public bool IsInUse => UsedBy.Count > 0;

    public string UsedByText => IsInUse ? string.Join(", ", UsedBy) : "—";

    public bool CanRemove => !IsBusy && !IsInUse;

    public string AccessibleName => $"{Name}, {(IsInUse ? $"used by {UsedByText}" : "unused")}";

    public void Update(DockerVolume volume, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(usedBy);
        if (volume != Volume)
        {
            Volume = volume;
        }

        if (!usedBy.SequenceEqual(UsedBy))
        {
            UsedBy = usedBy;
        }
    }
}

/// <summary>A row on the Compose tab: a project <c>docker compose ls</c> reports, or one RemoteFlow
/// remembers from an earlier visit that has since been brought down.</summary>
public sealed partial class DockerComposeProjectItemViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Whether <c>docker compose ls</c> lists it — it has containers, running or stopped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanUp))]
    [NotifyPropertyChangedFor(nameof(CanDown))]
    [NotifyPropertyChangedFor(nameof(CanForget))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsListed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigFilesText))]
    [NotifyPropertyChangedFor(nameof(CanUp))]
    public partial IReadOnlyList<string> ConfigFiles { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUp))]
    [NotifyPropertyChangedFor(nameof(CanDown))]
    [NotifyPropertyChangedFor(nameof(CanForget))]
    public partial bool IsBusy { get; set; }

    /// <summary>Compose's own words — "running(3)", "exited(1)" — or "down" for a remembered project.</summary>
    public string StatusText => IsListed ? Status : "down";

    public bool IsRunning => IsListed && Status.Contains("running", StringComparison.OrdinalIgnoreCase);

    public string ConfigFilesText => ConfigFiles.Count == 0 ? "unknown" : string.Join(", ", ConfigFiles);

    /// <summary>Up needs the compose files. A project listed without them — started from stdin, say — can
    /// only be brought down from here.</summary>
    public bool CanUp => !IsBusy && ConfigFiles.Count > 0;

    public bool CanDown => !IsBusy && IsListed;

    public bool CanForget => !IsBusy && !IsListed;

    public string AccessibleName => $"{Name}, {StatusText}";

    public void Update(bool isListed, string status, IReadOnlyList<string> configFiles)
    {
        ArgumentNullException.ThrowIfNull(configFiles);
        IsListed = isListed;
        Status = status;
        if (!configFiles.SequenceEqual(ConfigFiles))
        {
            ConfigFiles = configFiles;
        }
    }
}
