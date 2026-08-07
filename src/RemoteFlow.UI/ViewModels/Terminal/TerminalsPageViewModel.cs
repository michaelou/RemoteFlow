using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;

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
        : base(ptyService, dispatcher, settings, confirmation)
    {
    }
}
