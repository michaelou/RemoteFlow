using System.IO.Pipelines;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalSettingsViewModelTests
{
    [AvaloniaFact]
    public async Task MissingSettingsUseDarkSchemeAndARealMonospaceFallback()
    {
        var settings = new InMemorySettingsStore();
        var viewModel = new TerminalSettingsViewModel(settings);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TerminalColorSchemes.Dark, viewModel.SelectedColorScheme);
        Assert.Contains(viewModel.SelectedFontFamily, viewModel.FontFamilies);
        Assert.Equal(13, viewModel.FontSize);
        Assert.Equal(10_000, viewModel.Scrollback);
    }

    [AvaloniaFact]
    public async Task InvalidValuesClampAndAllSettingsRoundTrip()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.TerminalFontFamily, "definitely missing", token);
        await settings.Set(SettingKeys.TerminalFontSize, 0, token);
        await settings.Set(SettingKeys.TerminalScrollback, -40, token);
        var viewModel = new TerminalSettingsViewModel(settings);
        await viewModel.InitializeAsync(token);

        Assert.Equal(TerminalSettingsViewModel.MinimumFontSize, viewModel.FontSize);
        Assert.Equal(0, viewModel.Scrollback);
        Assert.Contains(viewModel.SelectedFontFamily, viewModel.FontFamilies);

        viewModel.FontSize = 18;
        viewModel.Scrollback = 2_000;
        viewModel.SelectedColorScheme = TerminalColorSchemes.HighContrast;
        viewModel.CursorStyle = TerminalCursorStyle.Bar;
        viewModel.CursorBlink = false;
        viewModel.BellMode = TerminalBellMode.Visual;
        await viewModel.FlushAsync();

        var restarted = new TerminalSettingsViewModel(settings);
        await restarted.InitializeAsync(token);
        Assert.Equal(18, restarted.FontSize);
        Assert.Equal(2_000, restarted.Scrollback);
        Assert.Equal("high-contrast", restarted.SelectedColorScheme.Id);
        Assert.Equal(TerminalCursorStyle.Bar, restarted.CursorStyle);
        Assert.False(restarted.CursorBlink);
        Assert.Equal(TerminalBellMode.Visual, restarted.BellMode);
    }

    [Fact]
    public void HighContrastSchemeExceedsSevenToOneForNormalText()
    {
        var ratio = ContrastRatio(
            TerminalColorSchemes.HighContrast.Foreground,
            TerminalColorSchemes.HighContrast.Background);

        Assert.True(ratio >= 7, $"High-contrast scheme ratio was {ratio:F2}:1.");
    }

    [AvaloniaFact]
    public async Task ApplyingSettingsRetrimsAnExistingSessionBuffer()
    {
        await using var session = new TerminalSessionViewModel(new IdleTerminalChannel(), new ImmediateDispatcher());
        session.Model.Feed(string.Join("\r\n", Enumerable.Range(1, 500).Select(index => $"line {index}")));

        session.ApplyAppearance(new TerminalAppearanceSettings(
            "Consolas",
            16,
            10,
            TerminalColorSchemes.Light,
            TerminalCursorStyle.Underline,
            false,
            TerminalBellMode.None));

        Assert.Equal("Consolas", session.FontFamilyName);
        Assert.Equal(16, session.TerminalFontSize);
        Assert.Equal(TerminalColorSchemes.Light.Background, session.TerminalBackground);
        Assert.True(session.Model.Terminal.Buffer.Lines.Length <= session.Model.Terminal.Rows + 10);
        Assert.Equal(10, session.Model.Terminal.Engine.Options.Scrollback);
        Assert.False(session.Model.Terminal.Engine.Options.CursorBlink);
    }

    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string color)
        {
            static double Channel(int value)
            {
                var component = value / 255d;
                return component <= 0.03928
                    ? component / 12.92
                    : Math.Pow((component + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(Convert.ToInt32(color.Substring(1, 2), 16))) +
                (0.7152 * Channel(Convert.ToInt32(color.Substring(3, 2), 16))) +
                (0.0722 * Channel(Convert.ToInt32(color.Substring(5, 2), 16)));
        }

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdleTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PipeReader Output => _pipe.Reader;
        public Task<int?> Exited => _exited.Task;
        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.Writer.CompleteAsync();
            if (_exited.TrySetResult(null))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(null, true));
            }
        }
    }
}
