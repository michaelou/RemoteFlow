using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.Views.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>Rehosting a session that owns its content, the way an embedded remote desktop does: it hands
/// back the one view that holds its native window, so every host has to share that single control.</summary>
public sealed class WorkspaceSessionContentHostTests
{
    /// <summary>The page host builds the workspace view again for the same view model each time the
    /// user navigates back to it, which is a second host for content the session still owns.</summary>
    [AvaloniaFact]
    public async Task LeavingAndReturningToThePageRehostsTheSameSessionContent()
    {
        await using var workspace = new TerminalWorkspaceViewModel();
        var session = new CachingWorkspaceSession("DC01");
        workspace.AddWorkspaceSession(session);

        var pageHost = new ContentControl
        {
            ContentTemplate = new FuncDataTemplate<TerminalWorkspaceViewModel>(
                (_, _) => new TerminalWorkspace(),
                supportsRecycling: false),
        };
        var window = new Window { Content = pageHost };
        window.Show();

        pageHost.Content = workspace;
        Dispatcher.UIThread.RunJobs();
        pageHost.Content = null;
        Dispatcher.UIThread.RunJobs();
        pageHost.Content = workspace;
        Dispatcher.UIThread.RunJobs();

        var host = pageHost.GetVisualDescendants()
            .OfType<WorkspaceSessionContentHost>()
            .Single(candidate => ReferenceEquals(candidate.Session, session));
        var content = Assert.IsType<Border>(host.Content);
        Assert.Same(session.Content, content);
        Assert.Equal(1, session.CreatedContentCount);
        Assert.True(content.IsAttachedToVisualTree());
        Assert.Same(host, content.Parent);
        window.Close();
    }

    [AvaloniaFact]
    public void ReattachingTheSameHostKeepsItsSessionContent()
    {
        var session = new CachingWorkspaceSession("DC01");
        var host = new WorkspaceSessionContentHost { Session = session };
        var panel = new Panel { Children = { host } };
        var window = new Window { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        _ = panel.Children.Remove(host);
        Dispatcher.UIThread.RunJobs();
        panel.Children.Add(host);
        Dispatcher.UIThread.RunJobs();

        var content = Assert.IsType<Border>(host.Content);
        Assert.Same(session.Content, content);
        Assert.Equal(1, session.CreatedContentCount);
        Assert.True(content.IsAttachedToVisualTree());
        window.Close();
    }

    /// <summary>A session that creates its content once and returns that same control every time.</summary>
    private sealed class CachingWorkspaceSession(string title) : ObservableObject,
        IWorkspaceSessionViewModel,
        IWorkspaceSessionContentProvider
    {
        public string Title { get; } = title;

        public string TabTitle => Title;

        public EnvironmentKind Environment => EnvironmentKind.Production;

        public string AccentColorHex => "#FF7B72";

        public string TabBackgroundHex => "#121821";

        public string ChromeTintHex => "#101418";

        public string EnvironmentCue => "PROD !";

        public string ProtocolCue => "RDP";

        public string StatusText => "Connected";

        public string TabAccessibleName => $"{Title}, RDP, production, Connected";

        public string CloseTabAccessibleName => $"Close RDP session {Title}";

        public bool IsActive { get; private set; }

        public bool IsLive => true;

        public bool IsEnded => false;

        public bool CanOpenInSystemTerminal => false;

        public string? EndedMessage => null;

        public string RecoveryActionLabel => "Reconnect";

        public IAsyncRelayCommand RetryCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

        public int CreatedContentCount { get; private set; }

        public Border? Content { get; private set; }

        public void SetActive(bool isActive)
        {
            IsActive = isActive;
            OnPropertyChanged(nameof(IsActive));
        }

        public Control CreateSessionContent()
        {
            if (Content is null)
            {
                CreatedContentCount++;
                Content = new Border { Background = Brushes.Transparent, DataContext = this };
            }

            return Content;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
