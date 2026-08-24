using RemoteFlow.Application.Services.Backup;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Retention deletes files out of a folder the user picked, and this parser is the only thing
/// standing between it and everything else living there. The rejection cases matter more than the
/// acceptance ones: each is a file that must survive.</summary>
public sealed class AutoBackupNamingTests
{
    [Fact]
    public void CreateProducesASortableUtcNameThatRoundTrips()
    {
        var created = new DateTimeOffset(2026, 8, 24, 13, 15, 0, TimeSpan.Zero);

        var name = AutoBackupNaming.Create(created, Guid.Parse("9f3a01bc-0000-0000-0000-000000000000"));

        Assert.Equal("remoteflow-auto-20260824T131500Z-9f3a01bc.rfbak.zip", name);
        Assert.True(AutoBackupNaming.TryParse(name, out var parsed));
        Assert.Equal(created, parsed);
    }

    [Fact]
    public void CreateNormalisesALocalTimeToUtcBeforeStampingIt()
    {
        var created = new DateTimeOffset(2026, 8, 24, 15, 15, 0, TimeSpan.FromHours(2));

        var name = AutoBackupNaming.Create(created, Guid.NewGuid());

        Assert.True(AutoBackupNaming.TryParse(name, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(created.UtcDateTime, parsed.UtcDateTime);
    }

    [Fact]
    public void NamesSortChronologicallyUnderOrdinalComparison()
    {
        var nonce = Guid.Parse("00000000-0000-0000-0000-000000000000");
        var older = AutoBackupNaming.Create(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), nonce);
        var newer = AutoBackupNaming.Create(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero), nonce);
        var newest = AutoBackupNaming.Create(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), nonce);

        Assert.True(string.CompareOrdinal(older, newer) < 0);
        Assert.True(string.CompareOrdinal(newer, newest) < 0);
    }

    [Fact]
    public void TryParseRejectsAManualExportName()
    {
        // What BackupExportViewModel writes today. It lands in the same folder often enough that pruning
        // it would be a routine way to destroy the backup somebody made deliberately.
        Assert.False(AutoBackupNaming.TryParse("RemoteFlow-backup-20260824-120000.zip", out _));
    }

    [Fact]
    public void TryParseRejectsAPartialUpload()
    {
        var partial = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid()) + AutoBackupNaming.PartialSuffix;

        Assert.False(AutoBackupNaming.TryParse(partial, out _));
    }

    [Theory]
    [InlineData("notes.zip")]
    [InlineData("remoteflow-auto-20260824T131500Z-9f3a01bc.zip")]          // right prefix, wrong suffix
    [InlineData("remoteflow-20260824T131500Z-9f3a01bc.rfbak.zip")]         // right suffix, wrong prefix
    [InlineData("RemoteFlow-Auto-20260824T131500Z-9f3a01bc.rfbak.zip")]    // prefix case differs
    [InlineData("remoteflow-auto-20260824T131500Z-9f3a01b.rfbak.zip")]     // nonce too short
    [InlineData("remoteflow-auto-20260824T131500Z-9f3a01bcd.rfbak.zip")]   // nonce too long
    [InlineData("remoteflow-auto-20260824T131500Z-9F3A01BC.rfbak.zip")]    // nonce not lowercase
    [InlineData("remoteflow-auto-20260824T131500Z-9f3a01gz.rfbak.zip")]    // nonce not hex
    [InlineData("remoteflow-auto-20260824T131500Z_9f3a01bc.rfbak.zip")]    // wrong separator
    [InlineData("remoteflow-auto-notatimestamp0-9f3a01bc.rfbak.zip")]      // unparseable timestamp
    [InlineData("remoteflow-auto-20261324T131500Z-9f3a01bc.rfbak.zip")]    // month 13
    [InlineData("remoteflow-auto-.rfbak.zip")]
    [InlineData("remoteflow-auto-")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseRejectsAnythingThatIsNotExactlyOurName(string? name)
    {
        Assert.False(AutoBackupNaming.TryParse(name, out _));
        Assert.False(AutoBackupNaming.IsAutoBackupName(name));
    }

    [Fact]
    public void TwoArchivesInTheSameSecondGetDistinctNames()
    {
        var moment = new DateTimeOffset(2026, 8, 24, 13, 15, 0, TimeSpan.Zero);

        var first = AutoBackupNaming.Create(moment, Guid.NewGuid());
        var second = AutoBackupNaming.Create(moment, Guid.NewGuid());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EveryGeneratedNameMatchesTheSearchPatternUsedToListThem()
    {
        var name = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid());

        Assert.StartsWith(AutoBackupNaming.Prefix, name, StringComparison.Ordinal);
        Assert.EndsWith(AutoBackupNaming.Suffix, name, StringComparison.Ordinal);
        Assert.Equal(AutoBackupNaming.Prefix + "*" + AutoBackupNaming.Suffix, AutoBackupNaming.SearchPattern);
    }
}
