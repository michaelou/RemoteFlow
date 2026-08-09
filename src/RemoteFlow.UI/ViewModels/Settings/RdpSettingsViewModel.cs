using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Settings;

public sealed partial class RdpSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private bool _initialized;
    private Task _pendingSave = Task.CompletedTask;

    public RdpSettingsViewModel(
        ISettingsStore settings,
        IEmbeddedRdpWorkspaceSessionFactory embeddedFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(embeddedFactory);
        IsAvailable = embeddedFactory.IsAvailableOnCurrentPlatform;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<RdpOpenModeOption> OpenModes { get; } =
    [
        new(WindowsRdpOpenMode.Embedded, "Inside RemoteFlow", "Open a live RDP tab in the terminal workspace."),
        new(WindowsRdpOpenMode.External, "System RDP client", "Launch Remote Desktop Connection as a separate app."),
    ];

    [ObservableProperty]
    public partial RdpOpenModeOption? SelectedOpenMode { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !IsAvailable)
        {
            return;
        }

        var mode = await _settings.Get(SettingKeys.WindowsRdpOpenMode, cancellationToken).ConfigureAwait(true);
        SelectedOpenMode = OpenModes.First(option => option.Value == mode);
        _initialized = true;
    }

    public async Task FlushAsync()
    {
        await _pendingSave.ConfigureAwait(false);
    }

    partial void OnSelectedOpenModeChanged(RdpOpenModeOption? value)
    {
        if (_initialized && value is not null)
        {
            _pendingSave = SaveAsync(_pendingSave, value.Value);
        }
    }

    private async Task SaveAsync(Task previousSave, WindowsRdpOpenMode value)
    {
        await previousSave.ConfigureAwait(false);
        await _settings.Set(SettingKeys.WindowsRdpOpenMode, value).ConfigureAwait(false);
    }
}

public sealed record RdpOpenModeOption(
    WindowsRdpOpenMode Value,
    string DisplayName,
    string Description);
