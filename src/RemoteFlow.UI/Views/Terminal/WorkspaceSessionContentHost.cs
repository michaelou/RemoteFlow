using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            Content = content;
            _builtSession = Session;
        }
    }
}
