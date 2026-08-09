using RemoteFlow.Application.Abstractions;
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
        Assert.Equal(30, SettingKeys.All.Count);
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
}
