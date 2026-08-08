using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.ViewModels.Terminal;

public sealed class TerminalsPageViewModel : TerminalWorkspaceViewModel
{
    public TerminalsPageViewModel()
    {
    }

    public TerminalsPageViewModel(IPtyService ptyService, IUiDispatcher dispatcher)
        : base(ptyService, dispatcher)
    {
    }

    public TerminalsPageViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore settings,
        IConfirmationDialogService confirmation)
        : base(ptyService, dispatcher, settings, confirmation, null, null)
    {
    }

    public TerminalsPageViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore settings,
        IConfirmationDialogService confirmation,
        KeymapService keymap,
        TerminalClipboardController clipboardController)
        : base(ptyService, dispatcher, settings, confirmation, keymap, clipboardController)
    {
    }

    public TerminalsPageViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore settings,
        IConfirmationDialogService confirmation,
        KeymapService keymap,
        TerminalClipboardController clipboardController,
        TerminalSettingsViewModel terminalSettings)
        : base(ptyService, dispatcher, settings, confirmation, keymap, clipboardController, terminalSettings)
    {
    }

    public TerminalsPageViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore settings,
        IConfirmationDialogService confirmation,
        KeymapService keymap,
        TerminalClipboardController clipboardController,
        TerminalSettingsViewModel terminalSettings,
        IShellProfileService shellProfileService,
        ISystemTerminalLauncher systemTerminalLauncher)
        : base(
            ptyService,
            dispatcher,
            settings,
            confirmation,
            keymap,
            clipboardController,
            terminalSettings,
            shellProfileService,
            systemTerminalLauncher)
    {
    }

    public TerminalsPageViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore settings,
        IConfirmationDialogService confirmation,
        KeymapService keymap,
        TerminalClipboardController clipboardController,
        TerminalSettingsViewModel terminalSettings,
        IShellProfileService shellProfileService,
        ISystemTerminalLauncher systemTerminalLauncher,
        ISessionManager sessionManager)
        : base(
            ptyService,
            dispatcher,
            settings,
            confirmation,
            keymap,
            clipboardController,
            terminalSettings,
            shellProfileService,
            systemTerminalLauncher,
            sessionManager)
    {
    }
}
