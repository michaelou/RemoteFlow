using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The threshold that separates a click from a drag on the two file lists.
///
/// It is not a nicety: the SFTP remote list stages its whole selection to disk to build a drag's file
/// payload, so a press that started a drag on its own would download a 4 GB file every time someone
/// clicked the row it was on.</summary>
public sealed class DragGestureTests
{
    [AvaloniaFact]
    public void AClickIsNotADragAndMovementPastTheThresholdStartsExactlyOne()
    {
        var gesture = new DragGesture();
        var press = Press(new Point(100, 100));
        gesture.Arm(press, new Point(100, 100));

        // Held still, and jittered by less than the threshold: still a click.
        Assert.Null(gesture.TryStart(Moved(true), new Point(100, 100)));
        Assert.Null(gesture.TryStart(Moved(true), new Point(102, 97)));

        // Far enough to mean it, and it is the original press that starts the drag, because that is the
        // only trigger DoDragDropAsync accepts.
        Assert.Same(press, gesture.TryStart(Moved(true), new Point(100, 112)));

        // One press, one drag: the next move must not start a second.
        Assert.Null(gesture.TryStart(Moved(true), new Point(140, 160)));
    }

    [AvaloniaFact]
    public void AButtonThatCameUpElsewhereEndsTheGestureAndDisarmingBeatsAnyMovement()
    {
        var gesture = new DragGesture();
        gesture.Arm(Press(new Point(10, 10)), new Point(10, 10));

        // The release was never seen by the list — the pointer left the window, or something else
        // swallowed it — so the first move with the button up has to end the gesture rather than drag.
        Assert.Null(gesture.TryStart(Moved(false), new Point(200, 200)));
        Assert.Null(gesture.TryStart(Moved(true), new Point(200, 200)));

        gesture.Arm(Press(new Point(10, 10)), new Point(10, 10));
        gesture.Disarm();
        Assert.Null(gesture.TryStart(Moved(true), new Point(200, 200)));
    }

    [AvaloniaFact]
    public void AGestureThatWasNeverArmedNeverStartsADrag()
    {
        Assert.Null(new DragGesture().TryStart(Moved(true), new Point(500, 500)));
    }

    private static PointerPressedEventArgs Press(Point at)
    {
        return new PointerPressedEventArgs(
            null,
            NewPointer(),
            new Border(),
            at,
            0,
            Properties(pressed: true),
            KeyModifiers.None);
    }

    private static PointerEventArgs Moved(bool pressed)
    {
        return new PointerEventArgs(
            InputElement.PointerMovedEvent,
            null,
            NewPointer(),
            null,
            default,
            0,
            Properties(pressed),
            KeyModifiers.None);
    }

    private static PointerPointProperties Properties(bool pressed)
    {
        return new PointerPointProperties(
            pressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None,
            pressed ? PointerUpdateKind.LeftButtonPressed : PointerUpdateKind.LeftButtonReleased);
    }

    private static Pointer NewPointer()
    {
        return new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
    }
}
