using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.Persistence.Queries;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.Views.Connections;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ConnectionEditorTests
{
    /// <summary>
    /// The editor opens beside the button that opened it. Someone working by keyboard has already
    /// pressed Enter on "New connection"; if focus stays on that button they have to tab through the
    /// whole page to reach the first field, which is the difference between a usable form and a
    /// theoretical one.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningTheEditorPutsTheKeyboardInTheFirstField()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var view = new ConnectionEditorView { DataContext = await fixture.CreateEditorAsync(null, token) };
        var window = new Window { Content = view };

        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var nameBox = view.FindControl<TextBox>("NameBox");
        Assert.NotNull(nameBox);
        Assert.True(nameBox.IsFocused, "The editor opened without putting the keyboard in the Name field.");
        window.Close();
    }

    [Fact]
    public async Task ProtocolSwitchesSectionsAndOnlyReplacesThePreviousDefaultPort()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.Rdp;

        Assert.Equal(3389, editor.Port);
        Assert.True(editor.IsRdpSectionVisible);
        Assert.False(editor.IsSshSectionVisible);

        editor.Port = 3390;
        editor.Protocol = ProtocolType.Ssh;

        Assert.Equal(3390, editor.Port);
        Assert.True(editor.IsSshSectionVisible);
    }

    [Fact]
    public async Task InlineValidationBlocksSaveAndDirtyStateIsObservable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Name = "";
        editor.Host = "";
        editor.Port = 0;
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.False(saved);
        Assert.NotNull(editor.NameError);
        Assert.NotNull(editor.HostError);
        Assert.NotNull(editor.PortError);
        Assert.True(editor.IsDirty);
        Assert.Empty(await fixture.Connections.ListAsync(token));
    }

    [Fact]
    public async Task SavingSecretWritesProviderAndPersistsOnlyOpaqueReference()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Production";
        editor.Host = "prod.test";
        editor.Username = "operator";
        editor.AuthMethod = AuthMethod.Password;
        var secret = "correct horse battery staple".ToCharArray();

        var saved = await editor.SaveAsync(secret.AsMemory(), token);

        Assert.True(saved);
        Assert.Equal("correct horse battery staple", fixture.Provider.LastSecret);
        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        Assert.Equal(CredentialKind.Password, connection.Credential.Kind);
        Assert.Equal(fixture.Provider.Name, connection.Credential.StoreProvider);
        Assert.StartsWith("remoteflow/connection/", connection.Credential.StoreKey, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", connection.Credential.StoreKey, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", await ReadAllTextColumnsAsync(fixture.Database, token), StringComparison.Ordinal);
        Assert.All(secret, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task MissingProviderSecretShowsUnavailableAndSupportsReentryState()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Missing secret",
            "missing.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        _ = connection.SetCredential(
            CredentialRef.Create(CredentialKind.Password, "missing-key", fixture.Provider.Name, fixture.Clock.UtcNow).Value,
            SystemGuidProvider.Instance,
            fixture.Clock.UtcNow);
        await fixture.Connections.AddAsync(connection, token);

        var editor = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal(CredentialStorageStatus.UnavailableOnThisMachine, editor.CredentialStatus);
        Assert.Equal("unavailable on this machine", editor.CredentialStatusText);
        Assert.Equal("Re-enter credential", editor.CredentialActionLabel);
    }

    /// <summary>
    /// Strict never prompts, so a connection created with it can never store a host key and can
    /// never connect. New connections have to pick up the configured default instead.
    /// </summary>
    [Fact]
    public async Task NewConnectionsTakeTheConfiguredHostKeyPolicyRatherThanStrict()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Trust on first use";
        editor.Host = "tofu.test";

        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, editor.HostKeyPolicy);
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, connection.Ssh.HostKeyPolicy);
    }

    [Fact]
    public async Task HostKeyPolicyIsEditableAndRoundTripsThroughTheEditor()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        await fixture.Settings.Set(SettingKeys.DefaultHostKeyPolicy, HostKeyPolicy.Strict, token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Pinned";
        editor.Host = "pinned.test";

        Assert.Equal(HostKeyPolicy.Strict, editor.HostKeyPolicy);

        editor.HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse;
        Assert.True(editor.IsDirty);
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        var reopened = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, reopened.HostKeyPolicy);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task ColorOverridePresetsSetTheHexAndMatchEnvironmentClearsIt()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        var teal = ConnectionEditorViewModel.ColorOverrideChoices.Single(choice => choice.Label == "Teal");

        Assert.Equal("Match environment", editor.SelectedColorOverride.Label);
        Assert.False(editor.IsCustomColorVisible);

        editor.SelectedColorOverride = teal;

        Assert.Equal(teal.Hex, editor.ColorOverrideHex);
        Assert.False(editor.IsCustomColorVisible);
        Assert.Equal(
            Color.Parse(teal.Hex!),
            Assert.IsType<SolidColorBrush>(editor.EnvironmentPreviewBrush).Color);

        editor.Name = "Prod";
        editor.Host = "prod.test";
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.True(saved);
        Assert.Equal(teal.Hex, Assert.Single(await fixture.Connections.ListAsync(token)).ColorOverrideHex);

        editor.SelectedColorOverride = ConnectionEditorViewModel.ColorOverrideChoices[0];

        Assert.Null(editor.ColorOverrideHex);
    }

    [Fact]
    public async Task ColorOutsideThePresetsOpensTheCustomHexBoxAndReportsBadValues()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Bespoke",
            "bespoke.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        _ = connection.SetDetails(
            null,
            AuthMethod.None,
            null,
            EnvironmentKind.Unspecified,
            "#123456",
            SystemGuidProvider.Instance,
            fixture.Clock.UtcNow);
        await fixture.Connections.AddAsync(connection, token);

        var editor = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.True(editor.SelectedColorOverride.IsCustom);
        Assert.True(editor.IsCustomColorVisible);
        Assert.Equal("#123456", editor.ColorOverrideHex);

        editor.ColorOverrideHex = "not-a-colour";
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.False(saved);
        Assert.NotNull(editor.ColorOverrideError);
        Assert.True(editor.IsCustomColorVisible);
    }

    [Fact]
    public void PublicEditorSurfaceHasNoReadableSecretProperty()
    {
        var riskyNames = new[] { "password", "secret", "passphrase" };
        var publicReadableProperties = typeof(ConnectionEditorViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead)
            .ToArray();

        Assert.DoesNotContain(publicReadableProperties, property =>
            property.PropertyType == typeof(string) &&
            riskyNames.Any(name => property.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NavigatingAwayFromDirtyEditorRequiresExplicitDiscard()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        page.RequestCreateConnection();
        await page.WorkspaceChangesSettled;
        page.Editor!.Name = "Draft server";

        Assert.False(await page.CanNavigateAwayAsync(token));
        Assert.NotNull(page.Editor);
        Assert.Contains("Draft server", confirmation.LastMessage, StringComparison.Ordinal);

        Assert.True(await page.CanNavigateAwayAsync(token));
        Assert.Null(page.Editor);
    }

    [Fact]
    public async Task SelectingAnotherConnectionMovesTheOpenEditorUnlessChangesAreKept()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var first = Connection.Create(
            SystemGuidProvider.Instance,
            "First server",
            "first.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        var second = Connection.Create(
            SystemGuidProvider.Instance,
            "Second server",
            "second.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(first, token);
        await fixture.Connections.AddAsync(second, token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);

        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;
        await page.Details!.EditCommand.ExecuteAsync(null);
        Assert.Equal(first.Id, page.Editor!.ConnectionId);

        page.SelectNode(NodeFor(page, second.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(second.Id, page.Editor!.ConnectionId);
        Assert.Equal("Second server", page.Editor.Name);

        // Unsaved work still wins: the declined prompt leaves the editor on the connection it was on.
        page.Editor.Name = "Renamed but not saved";
        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(second.Id, page.Editor!.ConnectionId);
        Assert.Contains("Renamed but not saved", confirmation.LastMessage, StringComparison.Ordinal);

        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(first.Id, page.Editor!.ConnectionId);
    }

    [Fact]
    public async Task DetailsDeleteConfirmationNamesConnectionAndCanCancel()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Do not delete silently",
            "safe.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);
        var node = page.RootNodes.Single(item =>
            item.Kind == ExplorerNodeKind.Connection && item.Id == connection.Id);
        page.SelectNode(node, false);
        await page.WorkspaceChangesSettled;

        await page.Details!.DeleteCommand.ExecuteAsync(null);

        Assert.NotNull(await fixture.Connections.GetByIdAsync(connection.Id, token));
        Assert.Contains(connection.Name, confirmation.LastMessage, StringComparison.Ordinal);

        await page.Details.DeleteCommand.ExecuteAsync(null);

        Assert.Null(await fixture.Connections.GetByIdAsync(connection.Id, token));
    }

    /// <summary>A backup import rewrites the rows the editor and the details pane were built from, so both
    /// close instead of offering to save a draft over data that may no longer exist.</summary>
    [Fact]
    public async Task AReloadClosesTheEditorAndDetailsWithoutPrompting()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Imported over",
            "gone.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        var confirmation = new RecordingConfirmation();
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);
        page.SelectNode(NodeFor(page, connection.Id), false);
        await page.WorkspaceChangesSettled;
        await page.Details!.EditCommand.ExecuteAsync(null);
        page.Editor!.Name = "Draft the import invalidates";

        await fixture.Connections.DeleteAsync(connection.Id, token);
        fixture.Notifier.NotifyReloaded();
        await page.ConnectionChangesSettled;

        Assert.Null(page.Editor);
        Assert.Null(page.Details);
        Assert.False(page.IsEditorOpen);
        Assert.Empty(confirmation.LastMessage);
        Assert.DoesNotContain(page.RootNodes, node => !node.IsVirtual);
    }

    /// <summary>The pane costs the list half the page. Closing it has to leave nothing behind — not the
    /// details under a closed editor either — and clicking the row it was opened from has to bring it back,
    /// which is the case a selection change cannot cover because the row is already the selected one.</summary>
    [Fact]
    public async Task ClosingThePaneLeavesNothingOpenAndClickingTheSameRowBringsItBack()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Room for the list",
            "wide.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        using var page = fixture.CreatePage(new RecordingConfirmation());
        await page.InitializeAsync(token);
        var node = NodeFor(page, connection.Id);
        page.SelectNode(node, false);
        await page.WorkspaceChangesSettled;
        Assert.True(page.IsWorkspaceOpen);

        Assert.True(await page.CloseWorkspaceAsync(token));

        Assert.Null(page.Details);
        Assert.Null(page.Editor);
        Assert.False(page.IsWorkspaceOpen);

        page.RequestReopenWorkspace(node);
        await page.WorkspaceChangesSettled;

        Assert.Equal(connection.Id, page.Details!.Connection.Id);
        Assert.True(page.IsWorkspaceOpen);
    }

    /// <summary>Reopening is only for a pane that is shut. A click on the row an open editor belongs to
    /// must not throw that editor away and drop back to the details behind it.</summary>
    [Fact]
    public async Task ClickingTheRowOfAnOpenEditorLeavesTheEditorAlone()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Still editing",
            "busy.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        using var page = fixture.CreatePage(new RecordingConfirmation());
        await page.InitializeAsync(token);
        var node = NodeFor(page, connection.Id);
        page.SelectNode(node, false);
        await page.WorkspaceChangesSettled;
        await page.Details!.EditCommand.ExecuteAsync(null);
        page.Editor!.Name = "Half typed";

        page.RequestReopenWorkspace(node);
        await page.WorkspaceChangesSettled;

        Assert.Equal("Half typed", page.Editor!.Name);
    }

    /// <summary>Closing the pane discards an open editor, so it asks on the same terms cancelling does:
    /// a declined prompt leaves the pane exactly where it was.</summary>
    [Fact]
    public async Task ClosingThePaneOverAnUnsavedEditorAsksBeforeDiscarding()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Unsaved work",
            "draft.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);
        page.SelectNode(NodeFor(page, connection.Id), false);
        await page.WorkspaceChangesSettled;
        await page.Details!.EditCommand.ExecuteAsync(null);
        page.Editor!.Name = "Renamed but not saved";

        Assert.False(await page.CloseWorkspaceAsync(token));

        Assert.NotNull(page.Editor);
        Assert.True(page.IsWorkspaceOpen);
        Assert.Contains("Renamed but not saved", confirmation.LastMessage, StringComparison.Ordinal);

        Assert.True(await page.CloseWorkspaceAsync(token));

        Assert.Null(page.Editor);
        Assert.Null(page.Details);
        Assert.False(page.IsWorkspaceOpen);
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, true, false)]
    [InlineData(ProtocolType.Sftp, true, false)]
    [InlineData(ProtocolType.Rdp, false, true)]
    public void DetailsActionsFollowCapabilities(
        ProtocolType protocol,
        bool canOpenSftp,
        bool canLaunchRdp)
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Server",
            "server.test",
            protocol,
            DateTimeOffset.UtcNow).Value;
        var details = new ConnectionDetailsViewModel(
            connection,
            "No folder",
            [],
            null,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask);

        Assert.Equal(canOpenSftp, details.OpenSftpCommand.CanExecute(null));
        Assert.Equal(canLaunchRdp, details.LaunchRdpCommand.CanExecute(null));
    }

    private static ExplorerNodeViewModel NodeFor(ConnectionsPageViewModel page, Guid connectionId)
    {
        return page.RootNodes.Single(node =>
            node.Kind == ExplorerNodeKind.Connection && node.Id == connectionId);
    }

    private static async Task<string> ReadAllTextColumnsAsync(
        SqliteTempDbFixture database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.Factory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Connections";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!await reader.IsDBNullAsync(index, cancellationToken) && reader.GetValue(index) is string value)
                {
                    values.Add(value);
                }
            }
        }

        return string.Join('|', values);
    }

    [Fact]
    public async Task StorageProtocolsShowTheirOwnSectionAndHideTheAuthenticationCombo()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.S3;

        Assert.True(editor.IsStorageSectionVisible);
        Assert.True(editor.IsStorageRegionVisible);
        Assert.True(editor.IsStorageEndpointVisible);
        Assert.False(editor.IsAuthMethodVisible);
        Assert.False(editor.IsSshSectionVisible);
        Assert.False(editor.IsRdpSectionVisible);
        Assert.Equal("Access key ID", editor.UsernameLabel);
        Assert.Equal("Secret access key", editor.CredentialCaptureLabel);
        Assert.Equal(443, editor.Port);

        editor.Protocol = ProtocolType.AzureBlob;

        // Azure carries its region in the account and reaches a sovereign cloud through the host box, so
        // neither the region nor the custom-endpoint field applies.
        Assert.True(editor.IsStorageSectionVisible);
        Assert.False(editor.IsStorageRegionVisible);
        Assert.False(editor.IsStorageEndpointVisible);
        Assert.Equal("Storage account name", editor.UsernameLabel);
        Assert.Equal("Account key", editor.CredentialCaptureLabel);
    }

    [Fact]
    public async Task SwitchingAnSshConnectionToStorageReplacesItsHost()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Host = "shell.example.test";

        editor.Protocol = ProtocolType.S3;

        // A host that was right for SSH cannot be right for a storage account.
        Assert.Equal("s3.amazonaws.com", editor.Host);

        editor.StorageRegion = "eu-west-2";

        Assert.Equal("s3.eu-west-2.amazonaws.com", editor.Host);
    }

    [Fact]
    public async Task TheHostIsDerivedUntilItIsHandEdited()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.S3;
        editor.StorageRegion = "eu-west-2";

        Assert.Equal("s3.eu-west-2.amazonaws.com", editor.Host);

        editor.StorageRegion = "us-east-1";

        Assert.Equal("s3.us-east-1.amazonaws.com", editor.Host);

        // The same rule the port box follows: once the user has typed over it, it is theirs. This is how a
        // sovereign-cloud account is reached without another field.
        editor.Host = "s3.cn-north-1.amazonaws.com.cn";
        editor.StorageRegion = "eu-central-1";

        Assert.Equal("s3.cn-north-1.amazonaws.com.cn", editor.Host);
    }

    [Fact]
    public async Task AnAzureHostFollowsTheAccountNameAndACustomEndpointWinsOutright()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.AzureBlob;
        editor.Username = "contoso";

        Assert.Equal("contoso.blob.core.windows.net", editor.Host);

        editor.Protocol = ProtocolType.S3;
        editor.StorageRegion = "eu-west-2";
        editor.StorageServiceUrl = "http://minio.example.test:9000";

        Assert.Equal("minio.example.test:9000", editor.Host);
    }

    [Fact]
    public async Task AnUnknownS3RegionIsWarnedAboutWithoutBlockingTheSave()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.S3;
        editor.Username = "AKIAEXAMPLE";

        Assert.NotEmpty(editor.StorageRegionChoices);
        Assert.Null(editor.StorageRegionWarning);

        editor.StorageRegion = "eu-west";

        // Named, with the fix: the alternative is finding out from a DNS failure at connect time.
        Assert.Contains("not an AWS region", editor.StorageRegionWarning, StringComparison.Ordinal);
        Assert.Contains("eu-west-1", editor.StorageRegionWarning, StringComparison.Ordinal);

        editor.StorageRegion = "eu-west-1";

        Assert.Null(editor.StorageRegionWarning);

        // A warning, not a validation error: the same field serves S3-compatible services, where the
        // region is whatever that deployment calls it, so an unknown value must never block a save.
        editor.StorageRegion = "auto";
        Assert.NotNull(editor.StorageRegionWarning);
        Assert.Null(editor.StorageRegionError);
        Assert.True(await editor.SaveAsync("a-secret-key".ToCharArray().AsMemory(), token));

        var stored = await fixture.Connections.GetByIdAsync(editor.ConnectionId!.Value, token);
        Assert.Equal("auto", stored!.ObjectStorage.Region);
    }

    [Fact]
    public async Task ACustomEndpointOrAzureSilencesTheRegionWarningEntirely()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Protocol = ProtocolType.S3;
        editor.StorageRegion = "not-a-region";
        Assert.NotNull(editor.StorageRegionWarning);

        // MinIO and friends: the region is only ever sent to the endpoint the user named, so AWS's list
        // has nothing to say about it.
        editor.StorageServiceUrl = "http://minio.example.test:9000";
        Assert.Null(editor.StorageRegionWarning);

        editor.StorageServiceUrl = null;
        Assert.NotNull(editor.StorageRegionWarning);

        editor.Protocol = ProtocolType.AzureBlob;
        Assert.Null(editor.StorageRegionWarning);
        Assert.False(editor.IsStorageRegionVisible);
    }

    /// <summary>The reported sequence, verbatim: save a region, reopen, correct it, save again, and see
    /// whether the correction survives a round trip through the database.</summary>
    [Fact]
    public async Task CorrectingASavedRegionSurvivesAReopen()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.S3;
        editor.Username = "AKIAEXAMPLE";
        editor.StorageRegion = "eu-west";
        editor.StorageContainer = "archive";
        Assert.True(await editor.SaveAsync("a-secret-key".ToCharArray().AsMemory(), token));
        var id = editor.ConnectionId!.Value;

        var reopened = await fixture.CreateEditorAsync(id, token);
        Assert.Equal("eu-west", reopened.StorageRegion);
        Assert.Equal("s3.eu-west.amazonaws.com", reopened.Host);

        reopened.StorageRegion = "eu-west-1";
        Assert.Equal("s3.eu-west-1.amazonaws.com", reopened.Host);
        Assert.True(await reopened.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var stored = await fixture.Connections.GetByIdAsync(id, token);
        Assert.Equal(
            ("s3.eu-west-1.amazonaws.com", "eu-west-1", "archive"),
            (stored!.Host, stored.ObjectStorage.Region, stored.ObjectStorage.Container));

        var again = await fixture.CreateEditorAsync(id, token);
        Assert.Equal("eu-west-1", again.StorageRegion);
    }

    /// <summary>Named after the bug it exists to catch: the editor clears the stored credential whenever
    /// <see cref="AuthMethod.None"/> is saved with no new secret typed, and object storage connections sit
    /// on <see cref="AuthMethod.None"/> by design. Without the guard, re-saving one deletes the secret key
    /// it was just given.</summary>
    [Fact]
    public async Task ReSavingAStorageConnectionDoesNotClearTheStoredSecretKey()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.S3;
        editor.Username = "AKIAEXAMPLE";
        editor.StorageRegion = "eu-west-2";
        editor.StorageContainer = "archive";

        Assert.True(await editor.SaveAsync("a-secret-key".ToCharArray().AsMemory(), token));
        var id = editor.ConnectionId!.Value;
        var stored = await fixture.Connections.GetByIdAsync(id, token);
        Assert.Equal(CredentialKind.StorageSecretKey, stored!.Credential.Kind);
        Assert.Equal(AuthMethod.None, stored.AuthMethod);

        // No new secret typed: exactly the shape that used to wipe it.
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var reloaded = await fixture.Connections.GetByIdAsync(id, token);
        Assert.Equal(CredentialKind.StorageSecretKey, reloaded!.Credential.Kind);
        Assert.False(reloaded.Credential.IsEmpty);
        Assert.Equal(CredentialStorageStatus.Stored, editor.CredentialStatus);
        Assert.Equal("archive", reloaded.ObjectStorage.Container);
        Assert.Equal("eu-west-2", reloaded.ObjectStorage.Region);
    }

    [Fact]
    public async Task AnSshConnectionOnAuthMethodNoneStillHasItsCredentialCleared()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Shell";
        editor.Host = "shell.example.test";
        editor.Username = "operator";
        editor.AuthMethod = AuthMethod.Password;

        Assert.True(await editor.SaveAsync("a-password".ToCharArray().AsMemory(), token));
        var id = editor.ConnectionId!.Value;
        editor.AuthMethod = AuthMethod.None;

        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        // The guard is narrow on purpose: it exempts object storage, not every AuthMethod.None save.
        var reloaded = await fixture.Connections.GetByIdAsync(id, token);
        Assert.True(reloaded!.Credential.IsEmpty);
    }

    [Fact]
    public async Task ReopeningAStorageConnectionLoadsItsOptionsBack()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.AzureBlob;
        editor.Username = "contoso";
        editor.StorageContainer = "archive";
        editor.StorageRootPrefix = "logs/2026";
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var reopened = await fixture.CreateEditorAsync(editor.ConnectionId, token);

        Assert.Equal(ProtocolType.AzureBlob, reopened.Protocol);
        Assert.Equal("contoso", reopened.Username);
        Assert.Equal("archive", reopened.StorageContainer);
        Assert.Equal("logs/2026", reopened.StorageRootPrefix);
        Assert.Equal("contoso.blob.core.windows.net", reopened.Host);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task StorageValidationErrorsLandOnTheirOwnFields()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.S3;
        editor.Username = "AKIAEXAMPLE";
        editor.StorageServiceUrl = "not-a-url";
        editor.StorageContainer = "Not Valid";
        editor.Host = "s3.example.test";

        Assert.False(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        Assert.NotNull(editor.StorageServiceUrlError);
        Assert.NotNull(editor.StorageContainerError);
        // A custom endpoint stands in for the region, so that field is not in error here.
        Assert.Null(editor.StorageRegionError);
    }

    [Fact]
    public async Task AnS3ConnectionWithNeitherARegionNorAnEndpointReportsItOnTheRegionField()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Objects";
        editor.Protocol = ProtocolType.S3;
        editor.Username = "AKIAEXAMPLE";

        Assert.False(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        Assert.NotNull(editor.StorageRegionError);
        Assert.Null(editor.StorageServiceUrlError);
    }

    private sealed class EditorFixture : IAsyncDisposable
    {
        private readonly UnitOfWork _unitOfWork;
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;

        private EditorFixture(SqliteTempDbFixture database)
        {
            Database = database;
            Clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
            Connections = new ConnectionRepository(database.Factory);
            Folders = new FolderRepository(database.Factory);
            Tags = new TagRepository(database.Factory);
            Recent = new RecentConnectionStore(database.Factory);
            _unitOfWork = new UnitOfWork(database.Factory);
            Provider = new RecordingProvider();
            Notifier = new ConnectionChangeNotifier();
            ConnectionService = new ConnectionService(
                Connections,
                Recent,
                [Provider],
                _unitOfWork,
                _guids,
                Clock,
                Notifier);
            TagService = new TagService(Tags, Connections, _unitOfWork, _guids, Clock);
            CredentialService = new ConnectionCredentialService(
                Connections,
                new FixedSelector(Provider),
                [Provider],
                _unitOfWork,
                _guids,
                Clock,
                Notifier);
            FolderService = new FolderService(
                Folders,
                Connections,
                ConnectionService,
                _unitOfWork,
                _guids,
                Clock);
            Queries = new ConnectionQueryService(database.Factory);
            Settings = new InMemorySettingsStore();
        }

        public SqliteTempDbFixture Database { get; }
        public FakeClock Clock { get; }
        public ConnectionRepository Connections { get; }
        public FolderRepository Folders { get; }
        public TagRepository Tags { get; }
        public RecentConnectionStore Recent { get; }
        public RecordingProvider Provider { get; }
        public ConnectionService ConnectionService { get; }
        public TagService TagService { get; }
        public ConnectionCredentialService CredentialService { get; }
        public FolderService FolderService { get; }
        public ConnectionQueryService Queries { get; }
        public InMemorySettingsStore Settings { get; }
        public ConnectionChangeNotifier Notifier { get; }

        public static async Task<EditorFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new EditorFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public async Task<ConnectionEditorViewModel> CreateEditorAsync(
            Guid? connectionId,
            CancellationToken cancellationToken)
        {
            var editor = new ConnectionEditorViewModel(
                ConnectionService,
                Connections,
                CredentialService,
                Folders,
                Tags,
                TagService,
                settings: Settings);
            await editor.InitializeAsync(connectionId, cancellationToken);
            return editor;
        }

        public ConnectionsPageViewModel CreatePage(IConfirmationDialogService confirmation)
        {
            var factory = new ConnectionEditorViewModelFactory(
                ConnectionService,
                Connections,
                CredentialService,
                Folders,
                Tags,
                TagService,
                Recent);
            return new ConnectionsPageViewModel(
                Queries,
                Folders,
                Tags,
                ConnectionService,
                FolderService,
                Recent,
                Settings,
                new SuccessfulOpener(),
                Notifier,
                _guids,
                Clock,
                factory,
                confirmation);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class FixedSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(provider);
        }
    }

    private sealed class RecordingProvider : ICredentialProvider
    {
        private readonly Dictionary<string, char[]> _secrets = new(StringComparer.Ordinal);

        public string Name => "test-store";
        public bool IsAvailable => true;
        public string? LastSecret { get; private set; }

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.TryGetValue(storeKey, out var value) ? new SecretHandle(value) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSecret = new string(secret.Span);
            _secrets[storeKey] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _secrets.Remove(storeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConfirmation(params bool[] results) : IConfirmationDialogService
    {
        private readonly Queue<bool> _results = new(results);

        public string LastMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMessage = message;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class SuccessfulOpener : IConnectionSessionOpener
    {
        public Task<ConnectionOpenResult> OpenAsync(
            Guid connectionId,
            ConnectionOpenMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConnectionOpenResult.Success());
        }
    }
}
