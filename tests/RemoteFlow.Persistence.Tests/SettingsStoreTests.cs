using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class SettingsStoreTests
{
    private enum TestMode
    {
        First = 1,
        Second = 2,
    }

    private sealed record TestLayout(int Width, int Height, string Panel);

    [Fact]
    public async Task MissingKeysReturnTheirTypedDefaultsAndThemeDefaultsToDark()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        using var store = new SettingsStore(database.Factory);
        var unknown = new SettingKey<string>("Test.Unknown", "registered default");

        Assert.Equal("registered default", await store.Get(unknown, cancellationToken));
        Assert.Equal(AppTheme.Dark, await store.Get(SettingKeys.Theme, cancellationToken));
        Assert.False(await store.Get(SettingKeys.SuppressPasteWarning, cancellationToken));
        // One key fewer off Windows: WindowsRdpOpenMode joins All only where the embedded RDP control
        // can be hosted, so a Linux or macOS database is never seeded with a setting it cannot honour.
        Assert.Equal(OperatingSystem.IsWindows() ? 38 : 37, SettingKeys.All.Count);
        await using var context = await database.Factory.CreateDbContextAsync(cancellationToken);
        Assert.Equal(SettingKeys.All.Count, await context.Settings.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task BoolIntStringEnumAndRecordRoundTrip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        using var store = new SettingsStore(database.Factory);
        var booleanKey = new SettingKey<bool>("Test.Bool", false);
        var integerKey = new SettingKey<int>("Test.Int", 0);
        var stringKey = new SettingKey<string>("Test.String", string.Empty);
        var enumKey = new SettingKey<TestMode>("Test.Enum", TestMode.First);
        var recordKey = new SettingKey<TestLayout>("Test.Record", new(0, 0, "none"));
        var layout = new TestLayout(1440, 900, "connections");

        await store.Set(booleanKey, true, cancellationToken);
        await store.Set(integerKey, 42, cancellationToken);
        await store.Set(stringKey, "value", cancellationToken);
        await store.Set(enumKey, TestMode.Second, cancellationToken);
        await store.Set(recordKey, layout, cancellationToken);

        Assert.True(await store.Get(booleanKey, cancellationToken));
        Assert.Equal(42, await store.Get(integerKey, cancellationToken));
        Assert.Equal("value", await store.Get(stringKey, cancellationToken));
        Assert.Equal(TestMode.Second, await store.Get(enumKey, cancellationToken));
        Assert.Equal(layout, await store.Get(recordKey, cancellationToken));
    }

    [Fact]
    public async Task ChangingSettingRaisesExactlyOneNotification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        using var store = new SettingsStore(database.Factory);
        var notifications = new List<string>();
        store.SettingChanged += (_, eventArgs) => notifications.Add(eventArgs.Key);

        await store.Set(SettingKeys.TerminalFontSize, 14, cancellationToken);
        await store.Set(SettingKeys.TerminalFontSize, 14, cancellationToken);

        Assert.Equal([SettingKeys.TerminalFontSize.Name], notifications);
    }

    /// <summary>The automatic backup configuration is a record held in one settings row. It has to survive
    /// the store's JSON round trip intact, because a destination that comes back half-formed would point a
    /// background process somewhere unintended.</summary>
    [Fact]
    public async Task TheAutomaticBackupConfigurationRoundTripsThroughTheStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        using var store = new SettingsStore(database.Factory);
        var connectionId = Guid.NewGuid();
        var options = new AutoBackupOptions
        {
            IsEnabled = true,
            RetainedCopies = 7,
            Destination = new AutoBackupDestination
            {
                Kind = AutoBackupDestinationKind.SftpConnection,
                ConnectionId = connectionId,
                RemotePath = "/srv/backups/remoteflow",
            },
        };

        await store.Set(SettingKeys.AutoBackup, options, cancellationToken);
        var restored = await store.Get(SettingKeys.AutoBackup, cancellationToken);

        Assert.Equal(options, restored);
        Assert.Equal(AutoBackupDestinationKind.SftpConnection, restored.Destination.Kind);
        Assert.Equal(connectionId, restored.Destination.ConnectionId);
        Assert.Equal(7, restored.ClampedRetainedCopies);
    }

    [Fact]
    public async Task AutomaticBackupDefaultsToOffWithASensibleRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        using var store = new SettingsStore(database.Factory);

        var options = await store.Get(SettingKeys.AutoBackup, cancellationToken);

        Assert.False(options.IsEnabled);
        Assert.Equal(AutoBackupOptions.DefaultRetainedCopies, options.RetainedCopies);
        Assert.Equal(AutoBackupDestinationKind.LocalFolder, options.Destination.Kind);
    }

    /// <summary>Settings travel inside backup archives, so a retention count can arrive from another
    /// machine — or from a hand-edited database. Zero read literally would mean "keep nothing", which would
    /// delete the archive the run had just made.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10, 10)]
    [InlineData(100000, AutoBackupOptions.MaximumRetainedCopies)]
    public void RetentionIsClampedWhateverIsStored(int stored, int expected)
    {
        var options = new AutoBackupOptions { RetainedCopies = stored };

        Assert.Equal(expected, options.ClampedRetainedCopies);
    }
}
