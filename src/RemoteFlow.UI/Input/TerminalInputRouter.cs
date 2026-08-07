using Avalonia.Input;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Input;

public sealed class TerminalInputRouter(KeymapService keymap)
{
    public async Task<bool> RouteAsync(
        KeyEventArgs args,
        TerminalsPageViewModel workspace,
        Action toggleFullscreen,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(toggleFullscreen);
        var stroke = TerminalKeyEventAdapter.FromAvalonia(args);
        var selected = workspace.SelectedSession;
        var ctrlCPolicy = await workspace.GetCtrlCPolicyAsync(cancellationToken).ConfigureAwait(true);
        var result = keymap.Resolve(
            stroke,
            OperatingSystem.IsMacOS() ? KeymapPlatform.MacOs : KeymapPlatform.WindowsLinux,
            selected?.ApplicationCursorKeys ?? false,
            ctrlCPolicy,
            selected?.Model.HasSelection ?? false);
        if (result.Kind == KeymapResultKind.PtyBytes)
        {
            if (selected is not null)
            {
                await selected.SendInputAsync(result.Bytes, cancellationToken).ConfigureAwait(true);
            }

            return true;
        }

        if (result.Kind != KeymapResultKind.ApplicationCommand || result.Command is not { } command)
        {
            return false;
        }

        switch (command)
        {
            case KeymapCommand.NewTerminal:
                _ = await workspace.AddLocalSessionAsync(cancellationToken).ConfigureAwait(true);
                break;
            case KeymapCommand.CloseTerminal:
                if (selected is not null)
                {
                    _ = await workspace.CloseSessionAsync(selected, cancellationToken: cancellationToken).ConfigureAwait(true);
                }

                break;
            case KeymapCommand.CycleTerminal:
                workspace.CycleSession();
                break;
            case KeymapCommand.CycleTerminalBackward:
                workspace.CycleSession(backwards: true);
                break;
            case KeymapCommand.SwitchToTerminal1:
            case KeymapCommand.SwitchToTerminal2:
            case KeymapCommand.SwitchToTerminal3:
            case KeymapCommand.SwitchToTerminal4:
            case KeymapCommand.SwitchToTerminal5:
            case KeymapCommand.SwitchToTerminal6:
            case KeymapCommand.SwitchToTerminal7:
            case KeymapCommand.SwitchToTerminal8:
            case KeymapCommand.SwitchToTerminal9:
                workspace.SelectSession((int)command - (int)KeymapCommand.SwitchToTerminal1 + 1);
                break;
            case KeymapCommand.ToggleFullscreen:
                toggleFullscreen();
                break;
            case KeymapCommand.Copy:
                if (selected is not null && workspace.ClipboardController is { } copyController)
                {
                    var copyResult = await copyController.CopyAsync(
                        selected,
                        clearSelection: true,
                        cancellationToken).ConfigureAwait(true);
                    workspace.ReportError(copyResult.ErrorMessage);
                }

                break;
            case KeymapCommand.Paste:
                if (selected is not null && workspace.ClipboardController is { } pasteController)
                {
                    var pasteResult = await pasteController.PasteAsync(selected, cancellationToken).ConfigureAwait(true);
                    workspace.ReportError(pasteResult.ErrorMessage);
                }

                break;
            case KeymapCommand.SelectAll:
                selected?.Model.SelectAll();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(args));
        }

        return true;
    }
}
