using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Sftp;

public sealed record SftpPermissionFailure(string Path, SftpError Error, string Message)
{
    public string DisplayMessage => $"{Path}: {Message}";
}

public sealed partial class SftpPermissionsEditorViewModel : ObservableObject
{
    private static readonly UnixFileMode _executeBits =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private readonly ISftpService _sftp;
    private readonly IConfirmationDialogService _confirmation;
    private readonly Func<CancellationToken, Task> _refresh;
    private UnixFileMode _mode;
    private UnixFileMode _fileMode;
    private bool _syncing;
    private bool _fileModeCustomized;

    public SftpPermissionsEditorViewModel(
        RemoteFileInfo target,
        bool isCurrentDirectory,
        ISftpService sftp,
        IConfirmationDialogService confirmation,
        Func<CancellationToken, Task> refresh)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        IsCurrentDirectory = isCurrentDirectory;
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _mode = target.Mode;
        _fileMode = target.Mode & ~_executeBits;
        OctalText = SftpPath.FormatMode(_mode);
        FileOctalText = SftpPath.FormatMode(_fileMode);
        SyncGridFromMode();
    }

    public RemoteFileInfo Target { get; }

    public bool IsCurrentDirectory { get; }

    public string OwnerAndGroup => $"{Target.Owner} / {Target.Group}";

    public string OwnerAndGroupLabel => $"Owner / group (read-only): {OwnerAndGroup}";

    public bool CanApply => ValidationMessage is null && (!Recursive || FileValidationMessage is null) && !IsApplying;

    public ObservableCollection<SftpPermissionFailure> Failures { get; } = [];

    [ObservableProperty]
    public partial string OctalText { get; set; }

    [ObservableProperty]
    public partial string FileOctalText { get; set; }

    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    [ObservableProperty]
    public partial string? FileValidationMessage { get; private set; }

    [ObservableProperty]
    public partial bool Recursive { get; set; }

    [ObservableProperty]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    public partial string? ResultMessage { get; private set; }

    [ObservableProperty]
    public partial bool UserRead { get; set; }

    [ObservableProperty]
    public partial bool UserWrite { get; set; }

    [ObservableProperty]
    public partial bool UserExecute { get; set; }

    [ObservableProperty]
    public partial bool GroupRead { get; set; }

    [ObservableProperty]
    public partial bool GroupWrite { get; set; }

    [ObservableProperty]
    public partial bool GroupExecute { get; set; }

    [ObservableProperty]
    public partial bool OtherRead { get; set; }

    [ObservableProperty]
    public partial bool OtherWrite { get; set; }

    [ObservableProperty]
    public partial bool OtherExecute { get; set; }

    [ObservableProperty]
    public partial bool SetUserId { get; set; }

    [ObservableProperty]
    public partial bool SetGroupId { get; set; }

    [ObservableProperty]
    public partial bool Sticky { get; set; }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanApply)
        {
            ResultMessage = "Correct the invalid octal value before applying permissions.";
            return false;
        }
        if (IsCurrentDirectory && (_mode & (UnixFileMode)0x1FF) == 0)
        {
            var confirmed = await _confirmation.ConfirmAsync(
                "Apply mode 000 to current directory?",
                "Mode 000 removes all access from the current directory and can lock this workspace out. Continue?",
                "Apply 000",
                cancellationToken).ConfigureAwait(true);
            if (!confirmed)
            {
                ResultMessage = "Permission change cancelled to avoid self-lockout.";
                return false;
            }
        }

        IsApplying = true;
        ResultMessage = null;
        Failures.Clear();
        var plan = new List<RemoteFileInfo>();
        var applied = 0;
        try
        {
            if (Recursive && Target.IsDirectory && !Target.IsSymlink)
            {
                await BuildPlanAsync(Target, plan, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                plan.Add(Target);
            }

            foreach (var item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mode = Recursive && Target.IsDirectory && !item.IsDirectory ? _fileMode : _mode;
                var result = await _sftp.SetPermissionsAsync(item.FullPath, mode, cancellationToken)
                    .ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    applied++;
                }
                else
                {
                    Failures.Add(new SftpPermissionFailure(
                        item.FullPath,
                        result.Failure.Error,
                        result.Failure.Message));
                }
            }

            if (applied > 0)
            {
                await _refresh(CancellationToken.None).ConfigureAwait(true);
            }
            ResultMessage = BuildResultMessage(applied, plan.Count, Failures);
            return Failures.Count == 0;
        }
        catch (OperationCanceledException)
        {
            if (applied > 0)
            {
                await _refresh(CancellationToken.None).ConfigureAwait(true);
            }
            ResultMessage = $"Permission update cancelled after applying {applied} item(s). " +
                $"{Failures.Count} failure(s) were recorded; already-applied changes were kept.";
            return false;
        }
        finally
        {
            IsApplying = false;
        }
    }

    partial void OnOctalTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }
        if (!TryParseMode(value, out var parsed))
        {
            ValidationMessage = "Use three or four octal digits (0–7), for example 0755.";
            OnPropertyChanged(nameof(CanApply));
            return;
        }
        ValidationMessage = null;
        _mode = parsed;
        SyncGridFromMode();
        if (!_fileModeCustomized)
        {
            _fileMode = parsed & ~_executeBits;
            _syncing = true;
            FileOctalText = SftpPath.FormatMode(_fileMode);
            _syncing = false;
        }
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnFileOctalTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }
        if (!TryParseMode(value, out var parsed))
        {
            FileValidationMessage = "Use three or four octal digits (0–7) for files.";
            OnPropertyChanged(nameof(CanApply));
            return;
        }
        FileValidationMessage = null;
        _fileMode = parsed;
        _fileModeCustomized = true;
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnRecursiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnUserReadChanged(bool value) => SyncModeFromGrid();
    partial void OnUserWriteChanged(bool value) => SyncModeFromGrid();
    partial void OnUserExecuteChanged(bool value) => SyncModeFromGrid();
    partial void OnGroupReadChanged(bool value) => SyncModeFromGrid();
    partial void OnGroupWriteChanged(bool value) => SyncModeFromGrid();
    partial void OnGroupExecuteChanged(bool value) => SyncModeFromGrid();
    partial void OnOtherReadChanged(bool value) => SyncModeFromGrid();
    partial void OnOtherWriteChanged(bool value) => SyncModeFromGrid();
    partial void OnOtherExecuteChanged(bool value) => SyncModeFromGrid();
    partial void OnSetUserIdChanged(bool value) => SyncModeFromGrid();
    partial void OnSetGroupIdChanged(bool value) => SyncModeFromGrid();
    partial void OnStickyChanged(bool value) => SyncModeFromGrid();

    private async Task BuildPlanAsync(
        RemoteFileInfo directory,
        List<RemoteFileInfo> plan,
        CancellationToken cancellationToken)
    {
        var listed = await _sftp.ListAsync(directory.FullPath, cancellationToken).ConfigureAwait(true);
        if (listed.IsFailure)
        {
            Failures.Add(new SftpPermissionFailure(
                directory.FullPath,
                listed.Failure.Error,
                $"Could not enumerate children: {listed.Failure.Message}"));
            return;
        }
        foreach (var child in listed.Value)
        {
            if (child.IsDirectory && !child.IsSymlink)
            {
                await BuildPlanAsync(child, plan, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                plan.Add(child);
            }
        }
        plan.Add(directory);
    }

    private void SyncModeFromGrid()
    {
        if (_syncing)
        {
            return;
        }
        var mode = (UnixFileMode)0;
        mode = Set(mode, UnixFileMode.UserRead, UserRead);
        mode = Set(mode, UnixFileMode.UserWrite, UserWrite);
        mode = Set(mode, UnixFileMode.UserExecute, UserExecute);
        mode = Set(mode, UnixFileMode.GroupRead, GroupRead);
        mode = Set(mode, UnixFileMode.GroupWrite, GroupWrite);
        mode = Set(mode, UnixFileMode.GroupExecute, GroupExecute);
        mode = Set(mode, UnixFileMode.OtherRead, OtherRead);
        mode = Set(mode, UnixFileMode.OtherWrite, OtherWrite);
        mode = Set(mode, UnixFileMode.OtherExecute, OtherExecute);
        mode = Set(mode, UnixFileMode.SetUser, SetUserId);
        mode = Set(mode, UnixFileMode.SetGroup, SetGroupId);
        mode = Set(mode, UnixFileMode.StickyBit, Sticky);
        _mode = mode;
        ValidationMessage = null;
        _syncing = true;
        OctalText = SftpPath.FormatMode(mode);
        _syncing = false;
        if (!_fileModeCustomized)
        {
            _fileMode = mode & ~_executeBits;
            _syncing = true;
            FileOctalText = SftpPath.FormatMode(_fileMode);
            _syncing = false;
        }
        OnPropertyChanged(nameof(CanApply));
    }

    private void SyncGridFromMode()
    {
        _syncing = true;
        UserRead = _mode.HasFlag(UnixFileMode.UserRead);
        UserWrite = _mode.HasFlag(UnixFileMode.UserWrite);
        UserExecute = _mode.HasFlag(UnixFileMode.UserExecute);
        GroupRead = _mode.HasFlag(UnixFileMode.GroupRead);
        GroupWrite = _mode.HasFlag(UnixFileMode.GroupWrite);
        GroupExecute = _mode.HasFlag(UnixFileMode.GroupExecute);
        OtherRead = _mode.HasFlag(UnixFileMode.OtherRead);
        OtherWrite = _mode.HasFlag(UnixFileMode.OtherWrite);
        OtherExecute = _mode.HasFlag(UnixFileMode.OtherExecute);
        SetUserId = _mode.HasFlag(UnixFileMode.SetUser);
        SetGroupId = _mode.HasFlag(UnixFileMode.SetGroup);
        Sticky = _mode.HasFlag(UnixFileMode.StickyBit);
        _syncing = false;
    }

    private static UnixFileMode Set(UnixFileMode value, UnixFileMode flag, bool enabled)
    {
        return enabled ? value | flag : value & ~flag;
    }

    private static bool TryParseMode(string? text, out UnixFileMode mode)
    {
        var trimmed = text?.Trim();
        if (trimmed is null || trimmed.Length is < 3 or > 4 || trimmed.Any(character => character is < '0' or > '7'))
        {
            mode = default;
            return false;
        }
        mode = (UnixFileMode)Convert.ToInt32(trimmed, 8);
        return true;
    }

    private static string BuildResultMessage(
        int applied,
        int total,
        IEnumerable<SftpPermissionFailure> failures)
    {
        var failureList = failures.ToArray();
        return failureList.Length == 0
            ? $"Applied permissions to {applied} item(s)."
            : failureList.Any(failure => failure.Error == SftpError.NotSupported)
            ? $"The server does not support chmod for {failureList.Length} item(s). Applied {applied} of {total}."
            : $"Applied {applied} of {total} item(s). Failed: " +
                string.Join(", ", failureList.Select(failure => failure.Path));
    }
}
