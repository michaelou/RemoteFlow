using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Views.Terminal;

/// <summary>Creates platform-owned session content once and keeps it attached for the item's lifetime.</summary>
public sealed class WorkspaceSessionContentHost : ContentControl
{
    public static readonly RoutedEvent<RoutedEventArgs> FocusEscapeRequestedEvent = RoutedEvent.Register<
        WorkspaceSessionContentHost,
        RoutedEventArgs>(
        "FocusEscapeRequested",
        RoutingStrategies.Bubble);

    public static readonly RoutedEvent<RoutedEventArgs> SelectionRequestedEvent = RoutedEvent.Register<
        WorkspaceSessionContentHost,
        RoutedEventArgs>(
        "SelectionRequested",
        RoutingStrategies.Bubble);

    public static readonly StyledProperty<IWorkspaceSessionViewModel?> SessionProperty =
        AvaloniaProperty.Register<WorkspaceSessionContentHost, IWorkspaceSessionViewModel?>(nameof(Session));

    private IWorkspaceSessionViewModel? _builtSession;

    public IWorkspaceSessionViewModel? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>Lets a native child HWND request the same focus escape as an Avalonia text surface.</summary>
    public static bool RequestFocusEscape(Interactive source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var request = new RoutedEventArgs(FocusEscapeRequestedEvent, source);
        source.RaiseEvent(request);
        return request.Handled;
    }

    /// <summary>Lets a native child HWND ask to become the selected session. Clicking one never reaches
    /// Avalonia as a pointer event, and in a grid every session is on screen at once — so without this the
    /// keyboard would be in one remote desktop while close, copy and find acted on another.</summary>
    public static bool RequestSelection(Interactive source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var request = new RoutedEventArgs(SelectionRequestedEvent, source);
        source.RaiseEvent(request);
        return request.Handled;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            BuildContent();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Session is TerminalSessionViewModel)
        {
            Content = null;
            _builtSession = null;
        }
        BuildContent();
    }

    private void BuildContent()
    {
        if (ReferenceEquals(_builtSession, Session) && Content is not null)
        {
            return;
        }

        object? content = Session switch
        {
            IWorkspaceSessionContentProvider provider => provider.CreateSessionContent(),
            TerminalSessionViewModel terminal => terminal,
            _ => null,
        };
        if (content is not null)
        {
            if (content is Control control)
            {
                ReleaseFromPreviousHost(control);
            }

            Content = content;
            _builtSession = Session;
        }
    }

    /// <summary>A session that owns its content hands back the same control every time, so that an
    /// embedded remote desktop keeps its native window. Leaving the page and coming back builds a
    /// second host for that one control: without handing it over, the next layout pass throws inside
    /// the layout manager and the window stops rendering until the application is restarted.</summary>
    private void ReleaseFromPreviousHost(Control content)
    {
        var previous = content.Parent as ContentControl ??
            (content.GetVisualParent() as ContentPresenter)?.TemplatedParent as ContentControl;
        if (previous is not null && !ReferenceEquals(previous, this))
        {
            previous.Content = null;
        }
    }
}
