using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using SvcSystems.UI.Terminal;

namespace RemoteFlow.UI.ViewModels.Settings;

public sealed partial class TerminalSettingsViewModel : ObservableObject, IDisposable
{
    public const int MinimumFontSize = 6;
    public const int MaximumFontSize = 72;
    public const int MinimumScrollback = 0;
    public const int MaximumScrollback = 100_000;

    private static readonly string[] _knownMonospaceFonts =
    [
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "JetBrains Mono",
        "SF Mono",
        "Menlo",
        "DejaVu Sans Mono",
        "Liberation Mono",
        "Noto Sans Mono",
    ];

    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _initialized;
    private bool _loading;
    private Task _pendingSave = Task.CompletedTask;
#pragma warning disable IDE0032 // Custom setters clamp and validate before raising change notifications.
    private string _selectedFontFamily = string.Empty;
    private int _fontSize = 13;
    private int _scrollback = 10_000;

    public TerminalSettingsViewModel(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        FontFamilies = DiscoverMonospaceFonts();
        SelectedFontFamily = ResolveFontFamily(null, FontFamilies);
        SelectedColorScheme = TerminalColorSchemes.Dark;
        PreviewModel = new TerminalControlModel(new TerminalOptions
        {
            Cols = 70,
            Rows = 12,
            Scrollback = 200,
            ReflowOnResize = false,
            TermName = "xterm-256color",
        });
        PreviewModel.Feed("RemoteFlow terminal preview\r\n\u001b[32mgreen\u001b[0m  \u001b[33mwarning\u001b[0m  \u001b[31merror\u001b[0m\r\n$ ssh production.example\r\n");
        ApplyPreview();
    }

    public event EventHandler? SettingsChanged;

    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<TerminalColorScheme> ColorSchemes { get; } = TerminalColorSchemes.All;

    public IReadOnlyList<TerminalCursorStyle> CursorStyles { get; } = Enum.GetValues<TerminalCursorStyle>();

    public IReadOnlyList<TerminalBellMode> BellModes { get; } = Enum.GetValues<TerminalBellMode>();

    public TerminalControlModel PreviewModel { get; }

    public string PreviewBackground => SelectedColorScheme.Background;

    public string PreviewForeground => SelectedColorScheme.Foreground;

    public string ApplyNote { get; } = "Font, colours, cursor, bell and scrollback apply to open and new sessions.";

    public TerminalAppearanceSettings Current => new(
        SelectedFontFamily,
        FontSize,
        Scrollback,
        SelectedColorScheme,
        CursorStyle,
        CursorBlink,
        BellMode);

    public string SelectedFontFamily
    {
        get => _selectedFontFamily;
        set
        {
            var resolved = ResolveFontFamily(value, FontFamilies);
            if (SetProperty(ref _selectedFontFamily, resolved))
            {
                OnValueChanged();
            }
        }
    }

    public int FontSize
    {
        get => _fontSize;
        set
        {
            if (SetProperty(ref _fontSize, Math.Clamp(value, MinimumFontSize, MaximumFontSize)))
            {
                OnValueChanged();
            }
        }
    }

    public int Scrollback
    {
        get => _scrollback;
        set
        {
            if (SetProperty(ref _scrollback, Math.Clamp(value, MinimumScrollback, MaximumScrollback)))
            {
                OnValueChanged();
            }
        }
    }
#pragma warning restore IDE0032

    [ObservableProperty]
    public partial TerminalColorScheme SelectedColorScheme { get; set; }

    [ObservableProperty]
    public partial TerminalCursorStyle CursorStyle { get; set; } = TerminalCursorStyle.Block;

    [ObservableProperty]
    public partial bool CursorBlink { get; set; } = true;

    [ObservableProperty]
    public partial TerminalBellMode BellMode { get; set; } = TerminalBellMode.None;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            _loading = true;
            SelectedFontFamily = ResolveFontFamily(
                await _settings.Get(SettingKeys.TerminalFontFamily, cancellationToken).ConfigureAwait(true),
                FontFamilies);
            FontSize = await _settings.Get(SettingKeys.TerminalFontSize, cancellationToken).ConfigureAwait(true);
            Scrollback = await _settings.Get(SettingKeys.TerminalScrollback, cancellationToken).ConfigureAwait(true);
            SelectedColorScheme = TerminalColorSchemes.Resolve(
                await _settings.Get(SettingKeys.TerminalColorScheme, cancellationToken).ConfigureAwait(true));
            CursorStyle = await _settings.Get(SettingKeys.CursorStyle, cancellationToken).ConfigureAwait(true);
            CursorBlink = await _settings.Get(SettingKeys.CursorBlink, cancellationToken).ConfigureAwait(true);
            BellMode = await _settings.Get(SettingKeys.BellMode, cancellationToken).ConfigureAwait(true);
            _loading = false;
            ApplyPreview();
            _initialized = true;
            SettingsChanged?.Invoke(this, EventArgs.Empty);

            if (FontSize != await _settings.Get(SettingKeys.TerminalFontSize, cancellationToken).ConfigureAwait(true) ||
                Scrollback != await _settings.Get(SettingKeys.TerminalScrollback, cancellationToken).ConfigureAwait(true))
            {
                _pendingSave = PersistAsync(Current, cancellationToken);
                await _pendingSave.ConfigureAwait(true);
            }
        }
        finally
        {
            _loading = false;
            _ = _initializationGate.Release();
        }
    }

    public async Task FlushAsync()
    {
        await _pendingSave.ConfigureAwait(false);
    }

    partial void OnSelectedColorSchemeChanged(TerminalColorScheme value)
    {
        OnPropertyChanged(nameof(PreviewBackground));
        OnPropertyChanged(nameof(PreviewForeground));
        OnValueChanged();
    }

    partial void OnCursorStyleChanged(TerminalCursorStyle value) => OnValueChanged();

    partial void OnCursorBlinkChanged(bool value) => OnValueChanged();

    partial void OnBellModeChanged(TerminalBellMode value) => OnValueChanged();

    private void OnValueChanged()
    {
        if (_loading || PreviewModel is null)
        {
            return;
        }

        ApplyPreview();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (_initialized)
        {
            _pendingSave = PersistAsync(Current, CancellationToken.None);
        }
    }

    private void ApplyPreview()
    {
        var current = Current;
        var terminal = PreviewModel.Terminal;
        terminal.Options.Scrollback = current.Scrollback;
        var engine = terminal.Engine;
        engine.Options.FontFamily = current.FontFamily;
        engine.Options.FontSize = current.FontSize;
        engine.Options.Scrollback = current.Scrollback;
        engine.Options.Theme = current.ColorScheme.ToThemeOptions();
        engine.Options.BellStyle = current.BellMode switch
        {
            TerminalBellMode.None => XTerm.Options.BellStyle.None,
            TerminalBellMode.Audible => XTerm.Options.BellStyle.Sound,
            TerminalBellMode.Visual => XTerm.Options.BellStyle.Visual,
            _ => throw new ArgumentOutOfRangeException(),
        };
        engine.SetCursorStyle(ToXTermCursorStyle(current.CursorStyle), current.CursorBlink);
        PreviewModel.FullBufferUpdate();
    }

    private async Task PersistAsync(TerminalAppearanceSettings current, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settings.Set(SettingKeys.TerminalFontFamily, current.FontFamily, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.TerminalFontSize, current.FontSize, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.TerminalScrollback, current.Scrollback, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.TerminalColorScheme, current.ColorScheme.Id, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.CursorStyle, current.CursorStyle, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.CursorBlink, current.CursorBlink, cancellationToken).ConfigureAwait(false);
            await _settings.Set(SettingKeys.BellMode, current.BellMode, cancellationToken).ConfigureAwait(false);
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Terminal settings could not be saved: {exception.Message}";
        }
        finally
        {
            _ = _saveGate.Release();
        }
    }

    private static List<string> DiscoverMonospaceFonts()
    {
        try
        {
            var installed = FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = _knownMonospaceFonts.Where(installed.Contains).ToList();
            if (candidates.Count > 0)
            {
                return candidates;
            }
        }
        catch (InvalidOperationException)
        {
            // Headless construction can happen before Avalonia initializes its font manager.
        }

        return OperatingSystem.IsWindows()
            ? ["Cascadia Mono", "Consolas"]
            : OperatingSystem.IsMacOS()
                ? ["SF Mono", "Menlo"]
                : ["DejaVu Sans Mono", "Liberation Mono"];
    }

    internal static string ResolveFontFamily(string? configured, IReadOnlyList<string> available)
    {
        var match = available.FirstOrDefault(font => string.Equals(font, configured, StringComparison.OrdinalIgnoreCase));
        return match ?? available[0];
    }

    public void Dispose()
    {
        _initializationGate.Dispose();
        _saveGate.Dispose();
    }

    internal static XTerm.Common.CursorStyle ToXTermCursorStyle(TerminalCursorStyle style)
    {
        return style switch
        {
            TerminalCursorStyle.Block => XTerm.Common.CursorStyle.Block,
            TerminalCursorStyle.Underline => XTerm.Common.CursorStyle.Underline,
            TerminalCursorStyle.Bar => XTerm.Common.CursorStyle.Bar,
            _ => XTerm.Common.CursorStyle.Block,
        };
    }
}
