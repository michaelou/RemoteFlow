using System.Text.Json;
using Avalonia.Controls;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Services;

public sealed record MonitorWorkArea(int X, int Y, int Width, int Height, double Scaling, bool IsPrimary);

public sealed record WindowGeometry(int X, int Y, double Width, double Height, bool IsMaximized)
{
    public static WindowGeometry Default { get; } = new(120, 80, 1200, 760, false);

    public WindowGeometry ClampToVisibleMonitor(IReadOnlyList<MonitorWorkArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return this;
        }

        var monitor = monitors
            .OrderByDescending(IntersectionArea)
            .ThenByDescending(item => item.IsPrimary)
            .First();
        if (IntersectionArea(monitor) == 0)
        {
            monitor = monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors[0];
        }

        var scaling = Math.Max(monitor.Scaling, 0.1);
        var width = Math.Clamp(Width, Math.Min(640, monitor.Width / scaling), monitor.Width / scaling);
        var height = Math.Clamp(Height, Math.Min(420, monitor.Height / scaling), monitor.Height / scaling);
        var pixelWidth = (int)Math.Ceiling(width * scaling);
        var pixelHeight = (int)Math.Ceiling(height * scaling);
        var x = Math.Clamp(X, monitor.X, monitor.X + monitor.Width - pixelWidth);
        var y = Math.Clamp(Y, monitor.Y, monitor.Y + monitor.Height - pixelHeight);
        return this with { X = x, Y = y, Width = width, Height = height };
    }

    private long IntersectionArea(MonitorWorkArea monitor)
    {
        var scaling = Math.Max(monitor.Scaling, 0.1);
        var right = Math.Min(X + (long)Math.Ceiling(Width * scaling), (long)monitor.X + monitor.Width);
        var bottom = Math.Min(Y + (long)Math.Ceiling(Height * scaling), (long)monitor.Y + monitor.Height);
        var left = Math.Max(X, monitor.X);
        var top = Math.Max(Y, monitor.Y);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }
}

public sealed class WindowGeometryService(ISettingsStore settingsStore)
{
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.General);
    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

    public async Task<WindowGeometry> RestoreAsync(
        IReadOnlyList<MonitorWorkArea> monitors,
        CancellationToken cancellationToken = default)
    {
        var json = await _settingsStore.Get(SettingKeys.WindowLayout, cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(json))
        {
            return WindowGeometry.Default.ClampToVisibleMonitor(monitors);
        }

        try
        {
            return (JsonSerializer.Deserialize<WindowGeometry>(json, _serializerOptions) ?? WindowGeometry.Default)
                .ClampToVisibleMonitor(monitors);
        }
        catch (JsonException)
        {
            return WindowGeometry.Default.ClampToVisibleMonitor(monitors);
        }
    }

    public Task SaveAsync(WindowGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return _settingsStore.Set(
            SettingKeys.WindowLayout,
            JsonSerializer.Serialize(geometry, _serializerOptions),
            cancellationToken);
    }

    public static IReadOnlyList<MonitorWorkArea> FromScreens(Screens screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return [.. screens.All.Select(screen => new MonitorWorkArea(
            screen.WorkingArea.X,
            screen.WorkingArea.Y,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height,
            screen.Scaling,
            screen.IsPrimary))];
    }
}
