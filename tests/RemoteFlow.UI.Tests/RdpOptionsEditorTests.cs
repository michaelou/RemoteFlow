using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.Views.Connections;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class RdpOptionsEditorTests
{
    [Fact]
    public async Task TheSectionAppearsOnlyForRdpAndAsksAboutTheClientWhenItDoes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        Assert.False(editor.IsRdpSectionVisible);
        Assert.Equal(0, fixture.Launcher.DetectionCount);

        editor.Protocol = ProtocolType.Rdp;
        await editor.RdpClientDetectionSettled;

        Assert.True(editor.IsRdpSectionVisible);
        Assert.Equal(1, fixture.Launcher.DetectionCount);
        Assert.False(editor.IsRdpClientMissing);
        Assert.Contains("Remote Desktop Connection 10.0", editor.RdpClientStatusText, StringComparison.Ordinal);

        // Away and back again: a client can be installed while the editor is open.
        editor.Protocol = ProtocolType.Ssh;
        editor.Protocol = ProtocolType.Rdp;
        await editor.RdpClientDetectionSettled;

        Assert.Equal(2, fixture.Launcher.DetectionCount);
    }

    [Fact]
    public async Task TheGuidancePanelIsShownOnlyWhenNothingIsInstalled()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        fixture.Launcher.Clients = [];
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.Rdp;
        await editor.RdpClientDetectionSettled;

        Assert.True(editor.IsRdpClientMissing);
        Assert.Equal("No RDP client found on this machine.", editor.RdpClientStatusText);
        Assert.Equal(FakeRdpLauncher.Guidance, editor.RdpInstallGuidance);
    }

    [Fact]
    public async Task EveryOptionRoundTripsThroughSaveAndReopen()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Jump box";
        editor.Host = "rdp.example.test";
        editor.Protocol = ProtocolType.Rdp;
        editor.Username = "operator";
        editor.RdpDomain = "CORP";
        editor.RdpFullScreen = true;
        editor.RdpMultimon = true;
        editor.RdpRedirectDrives = true;
        editor.RdpRedirectClipboard = false;
        editor.SelectedRdpResolution = Resolution("1920 × 1080");

        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        Assert.Equal("CORP", connection.Rdp.Domain);
        Assert.True(connection.Rdp.FullScreen);
        Assert.Equal(1_920, connection.Rdp.Width);
        Assert.Equal(1_080, connection.Rdp.Height);
        Assert.True(connection.Rdp.Multimon);
        Assert.False(connection.Rdp.RedirectClipboard);
        Assert.True(connection.Rdp.RedirectDrives);

        var reopened = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal("CORP", reopened.RdpDomain);
        Assert.True(reopened.RdpFullScreen);
        Assert.True(reopened.RdpMultimon);
        Assert.False(reopened.RdpRedirectClipboard);
        Assert.True(reopened.RdpRedirectDrives);
        Assert.Equal("1920 × 1080", reopened.SelectedRdpResolution.Label);
        Assert.Equal("1920", reopened.RdpWidthText);
        Assert.Equal("1080", reopened.RdpHeightText);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task ASizeThatIsNotAPresetReopensOnCustom()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Odd size";
        editor.Host = "rdp.example.test";
        editor.Protocol = ProtocolType.Rdp;
        editor.SelectedRdpResolution = Resolution("Custom…");
        editor.RdpWidthText = "1728";
        editor.RdpHeightText = "1117";

        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        var reopened = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal("Custom…", reopened.SelectedRdpResolution.Label);
        Assert.True(reopened.IsCustomRdpResolutionVisible);
        Assert.Equal("1728", reopened.RdpWidthText);
        Assert.Equal("1117", reopened.RdpHeightText);
    }

    [Theory]
    [InlineData("nineteen twenty", "1080", "whole numbers")]
    [InlineData("1920", "", "both a width and a height")]
    [InlineData("", "1080", "both a width and a height")]
    [InlineData("100", "1080", "between 640 and 7680")]
    [InlineData("1920", "99999", "between 480 and 4320")]
    [InlineData("-1920", "1080", "whole numbers")]
    public async Task GarbageAndOutOfRangeSizesAreRejectedWhereTheyAreTypedAndBlockTheSave(
        string width,
        string height,
        string expected)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Jump box";
        editor.Host = "rdp.example.test";
        editor.Protocol = ProtocolType.Rdp;
        editor.SelectedRdpResolution = Resolution("Custom…");

        editor.RdpWidthText = width;
        editor.RdpHeightText = height;

        Assert.NotNull(editor.RdpResolutionError);
        Assert.Contains(expected, editor.RdpResolutionError, StringComparison.Ordinal);
        Assert.False(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));
        Assert.Empty(await fixture.Connections.ListAsync(token));
        Assert.NotNull(editor.RdpResolutionError);
    }

    [Fact]
    public async Task APresetOwnsBothBoxesAndFitToClientClearsThem()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await RdpEditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.SelectedRdpResolution = Resolution("2560 × 1440");

        Assert.Equal("2560", editor.RdpWidthText);
        Assert.Equal("1440", editor.RdpHeightText);
        Assert.False(editor.IsCustomRdpResolutionVisible);
        Assert.Null(editor.RdpResolutionError);

        editor.SelectedRdpResolution = Resolution("Custom…");
        Assert.True(editor.IsCustomRdpResolutionVisible);
        Assert.Equal("2560", editor.RdpWidthText);

        editor.SelectedRdpResolution = Resolution("Fit to the client window");

        Assert.Null(editor.RdpWidthText);
        Assert.Null(editor.RdpHeightText);
        Assert.Null(editor.RdpResolutionError);
    }

    /// <summary>The section is a nested control, so its fields take part in the editor's tab order only
    /// if their indices slot into it. Without this they would all land after the Cancel button.</summary>
    [AvaloniaFact]
    public void TheSectionTakesItsPlaceInTheEditorTabOrder()
    {
        var view = new ConnectionEditorView();
        var indexed = TabIndexes(view);
        var section = view.GetLogicalDescendants().OfType<RdpOptionsSection>().Single();
        var sectionIndices = TabIndexes(section);

        Assert.Equal(8, sectionIndices.Length);
        Assert.Equal(sectionIndices, sectionIndices.Order());
        Assert.Equal(sectionIndices, sectionIndices.Distinct());

        // Every RDP field falls between the credential box that precedes the section and the folder
        // picker that follows it, and nothing outside the section reuses those numbers.
        Assert.All(sectionIndices, index => Assert.InRange(index, 9, 16));
        Assert.DoesNotContain(indexed.Except(sectionIndices), index => index is >= 9 and <= 16);
    }

    /// <summary>The section defines no styles of its own — it inherits the editor's label, hint and error
    /// classes. If that inheritance ever stops working the whole section silently renders at default
    /// sizes and colours, which no binding test would notice.</summary>
    [AvaloniaFact]
    public void TheSectionInheritsTheEditorLabelStyles()
    {
        var view = new ConnectionEditorView();
        var window = new Window { Content = view, Width = 700, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var section = view.GetLogicalDescendants().OfType<RdpOptionsSection>().Single();
        var label = section.GetLogicalDescendants()
            .OfType<TextBlock>()
            .First(block => block.Classes.Contains("label"));

        Assert.Equal(12d, label.FontSize);
        window.Close();
    }

    private static int[] TabIndexes(Control root)
    {
        return [.. root.GetLogicalDescendants()
            .OfType<InputElement>()
            // Avalonia defaults TabIndex to int.MaxValue, which means "wherever it falls".
            .Where(element => element.TabIndex != int.MaxValue)
            .Select(element => element.TabIndex)];
    }

    private static RdpResolutionChoiceViewModel Resolution(string label)
    {
        return ConnectionEditorViewModel.RdpResolutionChoices.Single(choice => choice.Label == label);
    }

    private sealed class RdpEditorFixture : IAsyncDisposable
    {
        private readonly SqliteTempDbFixture _database;
        private readonly UnitOfWork _unitOfWork;
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;
        private readonly ConnectionService _connectionService;
        private readonly ConnectionCredentialService _credentialService;
        private readonly TagService _tagService;
        private readonly FolderRepository _folders;
        private readonly TagRepository _tags;

        private RdpEditorFixture(SqliteTempDbFixture database)
        {
            _database = database;
            var clock = new FakeClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
            Connections = new ConnectionRepository(database.Factory);
            _folders = new FolderRepository(database.Factory);
            _tags = new TagRepository(database.Factory);
            _unitOfWork = new UnitOfWork(database.Factory);
            var provider = new NullCredentialProvider();
            _connectionService = new ConnectionService(
                Connections,
                new RecentConnectionStore(database.Factory),
                [provider],
                _unitOfWork,
                _guids,
                clock);
            _tagService = new TagService(_tags, Connections, _unitOfWork, _guids, clock);
            _credentialService = new ConnectionCredentialService(
                Connections,
                new FixedSelector(provider),
                [provider],
                _unitOfWork,
                _guids,
                clock);
        }

        public ConnectionRepository Connections { get; }

        public FakeRdpLauncher Launcher { get; } = new();

        public static async Task<RdpEditorFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new RdpEditorFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public async Task<ConnectionEditorViewModel> CreateEditorAsync(
            Guid? connectionId,
            CancellationToken cancellationToken)
        {
            var editor = new ConnectionEditorViewModel(
                _connectionService,
                Connections,
                _credentialService,
                _folders,
                _tags,
                _tagService,
                rdpLauncher: Launcher);
            await editor.InitializeAsync(connectionId, cancellationToken);
            return editor;
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
        }
    }

    private sealed class FakeRdpLauncher : IRdpLauncher
    {
        public const string Guidance = "Install Remote Desktop Connection from Windows Tools.";

        public IReadOnlyList<RdpClientInfo> Clients { get; set; } =
            [new("Remote Desktop Connection", "C:\\Windows\\System32\\mstsc.exe", "10.0.26100.1")];

        public int DetectionCount { get; private set; }

        public string MissingClientGuidance => Guidance;

        public Task<RdpLaunchResult> LaunchAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RdpLaunchResult.Launched);
        }

        public Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken cancellationToken = default)
        {
            DetectionCount++;
            return Task.FromResult(Clients);
        }

        public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    private sealed class NullCredentialProvider : ICredentialProvider
    {
        public string Name => "test-store";

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
