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
        var result = keymap.Resolve(
            stroke,
            OperatingSystem.IsMacOS() ? KeymapPlatform.MacOs : KeymapPlatform.WindowsLinux,
            selected?.ApplicationCursorKeys ?? false);
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
            case KeymapCommand.Paste:
            case KeymapCommand.SelectAll:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(args));
        }

        return true;
    }
}
