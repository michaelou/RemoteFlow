using Avalonia;
using Avalonia.Input;

namespace RemoteFlow.UI.Views;

/// <summary>A press plus a few pixels of movement, which is what a drag is and a click is not.
///
/// The file lists cannot start a drag on the press itself. On the SFTP remote list that press is the
/// difference between a click and a download: building the drag's operating-system file payload means
/// staging the whole selection to disk first — see ADR-0013 — so clicking a selected 4 GB file would fetch
/// it. It also keeps a plain click and a double-click out of the way of a pointer grab.
///
/// The press is retained and handed to <c>DragDrop.DoDragDropAsync</c> later, once the pointer has
/// actually moved, because that is the only trigger the API accepts. Both the X11 and Win32 drag sources
/// read nothing from it but its source visual, its pointer and its key modifiers, none of which the wait
/// invalidates.</summary>
internal sealed class DragGesture
{
    /// <summary>Enough movement to mean it, and little enough that a deliberate drag is not a shove. The
    /// platforms do not expose their own threshold, so this is a constant rather than a system value.
    /// </summary>
    private const double _thresholdPixels = 4;

    private PointerPressedEventArgs? _press;
    private Point _origin;

    /// <summary>Records a press that a drag may grow out of. Call it only for a press that would be
    /// allowed to drag — on a selected row, with the left button.</summary>
    public void Arm(PointerPressedEventArgs press, Point origin)
    {
        _press = press;
        _origin = origin;
    }

    /// <summary>Forgets the press: the button came up, the row was not draggable, or the drag has begun.
    /// </summary>
    public void Disarm()
    {
        _press = null;
    }

    /// <summary>The press to start the drag with, or null while the gesture is still a click. Disarms
    /// itself when it hands one back, so one press starts at most one drag.</summary>
    public PointerPressedEventArgs? TryStart(PointerEventArgs moved, Point position)
    {
        ArgumentNullException.ThrowIfNull(moved);
        if (_press is null)
        {
            return null;
        }

        if (!moved.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            // The button came up somewhere this gesture never heard about.
            _press = null;
            return null;
        }

        if (Math.Abs(position.X - _origin.X) < _thresholdPixels &&
            Math.Abs(position.Y - _origin.Y) < _thresholdPixels)
        {
            return null;
        }

        var press = _press;
        _press = null;
        return press;
    }
}
