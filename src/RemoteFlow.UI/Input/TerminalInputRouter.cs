using Avalonia.Input;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Input;

/// <summary>
/// Maps key strokes to application-level terminal commands.
/// </summary>
/// <remarks>
/// The router deliberately never writes to the PTY. <c>TerminalControl</c> already translates key
/// and text input into PTY bytes and raises <c>TerminalControlModel.UserInput</c>; routing the same
/// stroke here as well would send every keystroke twice.
/// </remarks>
public sealed class TerminalInputRouter(KeymapService keymap)
{
    /// <summary>
    /// Resolves the application command for a stroke, or <see langword="null" /> when the stroke
    /// belongs to the terminal.
    /// </summary>
    /// <remarks>
    /// This is synchronous on purpose: the caller must be able to mark the key event handled before
    /// the event reaches <c>TerminalControl</c>.
    /// </remarks>
    public KeymapCommand? Resolve(KeyEventArgs args, TerminalsPageViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(workspace);
        var stroke = TerminalKeyEventAdapter.FromAvalonia(args);
        var selected = workspace.SelectedTerminalSession;
        var result = keymap.Resolve(
            stroke,
            OperatingSystem.IsMacOS() ? KeymapPlatform.MacOs : KeymapPlatform.WindowsLinux,
            selected?.ApplicationCursorKeys ?? false,
            workspace.CtrlCPolicy,
            selected?.Model.HasSelection ?? false);
        return result.Kind == KeymapResultKind.ApplicationCommand ? result.Command : null;
    }

    public static async Task ExecuteAsync(
        KeymapCommand command,
        TerminalsPageViewModel workspace,
        Action toggleFullscreen,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(toggleFullscreen);
        var selected = workspace.SelectedSession;
        var selectedTerminal = workspace.SelectedTerminalSession;
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
                if (selectedTerminal is not null && workspace.ClipboardController is { } copyController)
                {
                    var copyResult = await copyController.CopyAsync(
                        selectedTerminal,
                        clearSelection: true,
                        cancellationToken).ConfigureAwait(true);
                    workspace.ReportError(copyResult.ErrorMessage);
                }

                break;
            case KeymapCommand.Paste:
                if (selectedTerminal is not null && workspace.ClipboardController is { } pasteController)
                {
                    var pasteResult = await pasteController.PasteAsync(selectedTerminal, cancellationToken).ConfigureAwait(true);
                    workspace.ReportError(pasteResult.ErrorMessage);
                }

                break;
            case KeymapCommand.SelectAll:
                selectedTerminal?.Model.SelectAll();
                break;
            case KeymapCommand.FindTerminal:
                selectedTerminal?.OpenFind();
                break;
            case KeymapCommand.CommandLibrary:
                // Opening is all that happens here; the view moves the keyboard into the search box, the
                // same division of labour the find bar above already uses.
                _ = workspace.OpenCommandLibrary();
                break;
            case KeymapCommand.LeaveTerminal:
                // Purely a focus move, which is the view's business; it is handled before the command
                // ever reaches here. Listed so the switch stays exhaustive rather than throwing.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
