using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly WindowGeometryService _geometryService;
    private WindowGeometry _normalGeometry = WindowGeometry.Default;

    public MainWindow()
        : this(new MainWindowViewModel(NavigationService.CreateDefault()), new PreviewSettingsStore())
    {
    }

    public MainWindow(MainWindowViewModel viewModel, ISettingsStore settingsStore)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _geometryService = new WindowGeometryService(settingsStore ?? throw new ArgumentNullException(nameof(settingsStore)));
        InitializeComponent();
        DataContext = viewModel;
        Opened += OnOpened;
        PositionChanged += OnPositionChanged;
        Resized += OnResized;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        var geometry = await _geometryService.RestoreAsync(
            WindowGeometryService.FromScreens(Screens)).ConfigureAwait(true);
        _normalGeometry = geometry;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = geometry.Width;
        Height = geometry.Height;
        Position = new PixelPoint(geometry.X, geometry.Y);
        WindowState = geometry.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        _ = NavigationList.Focus();
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
        var geometry = _normalGeometry with { IsMaximized = WindowState == WindowState.Maximized };
        _geometryService.SaveAsync(geometry).GetAwaiter().GetResult();
    }

    private void NavigationList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NavigationItemViewModel item })
        {
            _viewModel.Navigate(item);
        }
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
