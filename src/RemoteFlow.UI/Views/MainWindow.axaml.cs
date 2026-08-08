using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly WindowGeometryService _geometryService;
    private WindowGeometry _normalGeometry = WindowGeometry.Default;
    private bool _initialized;
    private bool _terminalCloseApproved;
    private bool _terminalCloseCheckRunning;
    private bool _restoringNavigationSelection;
    private IInputElement? _focusBeforePalette;

    public MainWindow()
        : this(
            new MainWindowViewModel(NavigationService.CreateDefault()),
            new WindowGeometryService(new PreviewSettingsStore()))
    {
    }

    public MainWindow(MainWindowViewModel viewModel, WindowGeometryService geometryService)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _geometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        NavigationList.Loaded += (_, _) => SyncNavigationSelection();
        Opened += OnOpened;
        PositionChanged += OnPositionChanged;
        Resized += OnResized;
        Closed += OnClosed;
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_viewModel.CurrentPage is ConnectionsPageViewModel connectionsPage)
        {
            await connectionsPage.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }

        var geometry = await _geometryService.RestoreAsync(
            WindowGeometryService.FromScreens(Screens),
            cancellationToken).ConfigureAwait(true);
        _normalGeometry = geometry;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = geometry.Width;
        Height = geometry.Height;
        Position = new PixelPoint(geometry.X, geometry.Y);
        WindowState = geometry.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        _initialized = true;
        SyncNavigationSelection();
        _ = NavigationList.Focus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentNavigationItem))
        {
            SyncNavigationSelection();
        }
    }

    /// <summary>Moves the sidebar highlight onto the page that is actually on screen. Opening a
    /// terminal or SFTP session navigates from the explorer, not from the sidebar, so without this
    /// the highlight would stay on Connections.</summary>
    private void SyncNavigationSelection()
    {
        if (_viewModel.CurrentNavigationItem is not { } item ||
            ReferenceEquals(NavigationList.SelectedItem, item))
        {
            return;
        }

        _restoringNavigationSelection = true;
        NavigationList.SelectedItem = item;
        _restoringNavigationSelection = false;
    }

    /// <summary>The native handle only exists once the window is open, and the taskbar icon is set
    /// through it. Avalonia posts its own icon to the window rather than setting it inline, so the
    /// correction has to be queued behind that message or it would simply be overwritten.</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Dispatcher.UIThread.Post(() => WindowsTaskbarIcon.Apply(this), DispatcherPriority.Background);
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _normalGeometry = _normalGeometry with { X = e.Point.X, Y = e.Point.Y };
        }
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _normalGeometry = _normalGeometry with { Width = e.ClientSize.Width, Height = e.ClientSize.Height };
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (!_initialized)
        {
            return;
        }

        var geometry = _normalGeometry with { IsMaximized = WindowState == WindowState.Maximized };
        _geometryService.SaveAsync(geometry).GetAwaiter().GetResult();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_terminalCloseApproved || _viewModel.Terminals is null)
        {
            return;
        }

        if (_terminalCloseCheckRunning)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _terminalCloseCheckRunning = true;
        try
        {
            if (await _viewModel.Terminals.RequestCloseAllAsync().ConfigureAwait(true))
            {
                _terminalCloseApproved = true;
                Close();
            }
        }
        finally
        {
            _terminalCloseCheckRunning = false;
        }
    }

    private async void NavigationList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringNavigationSelection || sender is not ListBox { SelectedItem: NavigationItemViewModel item } list)
        {
            return;
        }

        if (_viewModel.CurrentPage is ConnectionsPageViewModel connections &&
            !string.Equals(item.Title, connections.Title, StringComparison.Ordinal) &&
            !await connections.CanNavigateAwayAsync().ConfigureAwait(true))
        {
            _restoringNavigationSelection = true;
            list.SelectedItem = _viewModel.CurrentNavigationItem;
            _restoringNavigationSelection = false;
            return;
        }

        _viewModel.Navigate(item);
    }

    private void NavigationList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.ItemCount - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && list.SelectedItem is NavigationItemViewModel item)
        {
            _viewModel.Navigate(item);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_viewModel.Palette.IsOpen)
            {
                _focusBeforePalette = FocusManager?.GetFocusedElement();
                _viewModel.Palette.Open();
                Dispatcher.UIThread.Post(CommandPalette.FocusSearch);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.Palette.IsOpen)
        {
            _viewModel.Palette.Close();
            RestorePaletteFocus();
            e.Handled = true;
        }
    }

    private void CommandPalette_OnCloseRequested(object? sender, EventArgs e)
    {
        RestorePaletteFocus();
    }

    private void PaletteOverlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, PaletteOverlay))
        {
            _viewModel.Palette.Close();
            RestorePaletteFocus();
            e.Handled = true;
        }
    }

    private void RestorePaletteFocus()
    {
        var target = _focusBeforePalette;
        _focusBeforePalette = null;
        if (target is not null)
        {
            Dispatcher.UIThread.Post(() => _ = target.Focus());
        }
    }

    private sealed class PreviewSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        public Task<T> Get<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key.Name, out var value) ? (T)value! : key.DefaultValue);
        }

        public Task Set<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key.Name] = value;
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(key.Name));
            return Task.CompletedTask;
        }

        public Task SeedDefaults(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
