using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class BackupTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private ImportService imports = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library", "relliga.db");
    private string Archive => Path.Combine(files.DirectoryPath, "backup.zip");
    private static EventQuery January => new(From: Calendars.Date(), Until: Calendars.Date(2));
    public async ValueTask InitializeAsync() { store = new(Database, new CalendarExpander()); await store.InitializeAsync(); imports = new(store, [new ExportImporter()]); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task<ImportResult> Play(string title = "Synthetic song") => imports.ImportAsync(files.Write("[" + TestFiles.Row(title) + "]"), "Automatic");
    private async Task SeedBoth()
    {
        await Play(); await imports.ImportAsync(files.Write(Calendars.Document(Calendars.Event("DTSTART;VALUE=DATE:20250103\nDTEND;VALUE=DATE:20250104\nSUMMARY:Date", "date"),
            Calendars.Event("DTSTART:20250104T090000\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Routine", "float")), "calendar.ics"), "Automatic");
    }

    [Fact] public async Task SnapshotIncludesLiveWalAndExactlyManifestAndDatabase()
    {
        await using var walKeeper = new SqliteConnection($"Data Source={Database};Pooling=False"); await walKeeper.OpenAsync();
        using (var cmd = walKeeper.CreateCommand()) { cmd.CommandText = "PRAGMA wal_autocheckpoint=0; SELECT COUNT(*) FROM events;"; await cmd.ExecuteScalarAsync(); }
        await SeedBoth();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Database)!, "unmanaged-original.txt"), "Synthetic unrelated file must stay excluded");
        Assert.True(File.Exists(Database + "-wal"));
        await store.CreateAsync(Archive);
        using var archive = ZipFile.OpenRead(Archive); Assert.Equal(new[] { "library.db", "manifest.json" }, archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray());
        var preview = await store.InspectAsync(Archive); Assert.Equal(1, preview.Manifest.FormatVersion); Assert.Equal(2, preview.Manifest.SchemaVersion);
        Assert.Equal(1, preview.Manifest.PlaybackRecords); Assert.Equal(2, preview.Manifest.CalendarComponents); Assert.Equal(2, preview.Manifest.Imports);
        Assert.Equal(new[] { "Google Calendar", "Spotify" }, preview.Manifest.Sources);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Database)!, ".relliga-stage-*"));
    }

    [Fact] public async Task MixedRoundTripPreservesIdentitiesHistoryTemporalValuesAndSearch()
    {
        await SeedBoth(); var before = await store.QueryAsync(January); var history = await store.GetImportsAsync();
        await store.CreateAsync(Archive); var preview = await store.InspectAsync(Archive);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Google Calendar")));
        await store.RestoreAsync(preview);
        var after = await store.QueryAsync(January);
        Assert.Equal(before.Items, after.Items); Assert.Equal(history, await store.GetImportsAsync()); Assert.True((await store.VerifyAsync()).Healthy);
        Assert.Equal(3, (await store.QueryAsync(January with { Text = "Routine" })).Total);
        Assert.True(File.Exists(Database + ".previous"));
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Equal(before.Items, (await store.QueryAsync(January)).Items);
        await store.DiscardRecoveryAsync(); Assert.False(File.Exists(Database + ".previous"));
    }

    [Fact] public async Task RestoreIntoFreshDirectoryWorksAndPreservesBackup()
    {
        await SeedBoth(); await store.CreateAsync(Archive);
        using var fresh = new SqliteHistoryStore(Path.Combine(files.DirectoryPath, "fresh", "relliga.db"), new CalendarExpander()); await fresh.InitializeAsync();
        await fresh.RestoreAsync(await fresh.InspectAsync(Archive));
        Assert.Equal(5, (await fresh.QueryAsync(January)).Total); Assert.Equal(2, (await fresh.GetSummaryAsync()).Sources);
        Assert.True((await fresh.VerifyAsync()).Healthy); Assert.True(File.Exists(Archive));
    }

    [Fact] public async Task LargerLibraryBackupAndRestorePreserveAllRecordsBeyondUiPages()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 5000).Select(i => TestFiles.Row("Synthetic " + i,
            DateTimeOffset.UnixEpoch.AddMinutes(i).ToString("O")))) + "]";
        await imports.ImportAsync(files.Write(json), "Automatic"); await store.CreateAsync(Archive);
        var preview = await store.InspectAsync(Archive); Assert.Equal(5000, preview.Manifest.PlaybackRecords);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Spotify"))); await store.RestoreAsync(preview);
        Assert.Equal(5000, (await store.GetSummaryAsync()).Events);
        Assert.Equal("Synthetic 0", (await store.QueryAsync(new(Provider: "Spotify", Page: 99, PageSize: 50))).Items[^1].Event.Title);
        Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Theory] [InlineData("RestoreBeforeActivation")] [InlineData("RestoreJournalWritten")] [InlineData("RestoreReplaced")] [InlineData("RestoreValidated")]
    public async Task ActivationFaultAlwaysPreservesPreviousActiveLibrary(string phase)
    {
        await SeedBoth(); await store.CreateAsync(Archive); var preview = await store.InspectAsync(Archive);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Google Calendar")));
        var previous = (await store.QueryAsync(January)).Items;
        store.MaintenanceFault = observed => { if (observed == phase) throw new IOException("Synthetic activation failure"); };
        await Assert.ThrowsAsync<IOException>(() => store.RestoreAsync(preview));
        Assert.Equal(previous, (await store.QueryAsync(January)).Items); Assert.True((await store.VerifyAsync()).Healthy);
        Assert.False(File.Exists(Database + ".restore-journal"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StartupRecoversInterruptedActivationAndCleansAbandonedStaging(bool replaced)
    {
        await Play("Previous"); await store.CreateAsync(Archive);
        var previous = File.ReadAllBytes(Database); // All library connections are closed; tests never copy live user data.
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Spotify")));
        await Play("Candidate");
        store.Dispose();
        var candidate = File.ReadAllBytes(Database);
        if (replaced) await File.WriteAllBytesAsync(Database + ".previous", previous);
        else await File.WriteAllBytesAsync(Database, previous);
        await File.WriteAllTextAsync(Database + ".restore-journal", JsonSerializer.Serialize(new
        { Version = 1, PreviousSha256 = Convert.ToHexString(SHA256.HashData(previous)), CandidateSha256 = Convert.ToHexString(SHA256.HashData(candidate)) }));
        var abandoned = Path.Combine(Path.GetDirectoryName(Database)!, store.StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned); await File.WriteAllTextAsync(Path.Combine(abandoned, "library.db"), "interrupted staging");
        store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Equal("Previous", Assert.Single((await store.QueryAsync(new())).Items).Event.Title);
        Assert.False(Directory.Exists(abandoned)); Assert.False(File.Exists(Database + ".restore-journal")); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task CancelledOrFailedBackupCleansTemporaryOutputAndPreservesDestination()
    {
        await Play(); await File.WriteAllTextAsync(Archive, "existing");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(Archive, true, new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(Archive));
        store.MaintenanceFault = phase => { if (phase == "BackupBeforePublish") throw new IOException("Synthetic publish failure"); };
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(Archive, true));
        Assert.Equal("existing", await File.ReadAllTextAsync(Archive)); Assert.Empty(Directory.GetFiles(files.DirectoryPath, ".relliga-*.tmp"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Database)!, ".relliga-stage-*"));
    }

    [Fact] public async Task FailedRecoveryBlocksFurtherDatabaseActivityUntilStartupCanRecover()
    {
        await SeedBoth(); await store.CreateAsync(Archive); var preview = await store.InspectAsync(Archive);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Google Calendar")));
        var preserved = Path.Combine(files.DirectoryPath, "preserved-previous.db");
        store.MaintenanceFault = phase =>
        {
            if (phase != "RestoreReplaced") return;
            File.Move(Database + ".previous", preserved);
            throw new IOException("Synthetic interrupted rollback: recovery copy temporarily unavailable");
        };
        await Assert.ThrowsAsync<AggregateException>(() => store.RestoreAsync(preview));
        await Assert.ThrowsAsync<IOException>(() => store.QueryAsync(January));
        await Assert.ThrowsAsync<IOException>(() => store.BeginImportAsync("synthetic", "Spotify", "2.0.0", default));
        Assert.True(File.Exists(preserved)); Assert.True(File.Exists(Database + ".restore-journal"));
        store.Dispose(); File.Move(preserved, Database + ".previous");
        store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Single((await store.QueryAsync(January)).Items); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task ChangedBackupAfterPreviewCannotActivate()
    {
        await Play(); await store.CreateAsync(Archive); var preview = await store.InspectAsync(Archive);
        using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Update)) zip.CreateEntry("extra");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(preview)); Assert.Single((await store.QueryAsync(new())).Items);
    }

    [Fact] public async Task CorruptTruncatedPayloadOrManifestIsRejectedWithoutChangingLibrary()
    {
        await Play(); await store.CreateAsync(Archive); var valid = await File.ReadAllBytesAsync(Archive);
        await File.WriteAllBytesAsync(Archive, valid[..(valid.Length / 2)]); await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
        await File.WriteAllBytesAsync(Archive, valid);
        await RewriteEntry("library.db", bytes => { bytes[100] ^= 0xFF; return bytes; });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
        await File.WriteAllBytesAsync(Archive, valid);
        await RewriteEntry("manifest.json", bytes => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("\"playbackRecords\":1", "\"playbackRecords\":2")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive)); Assert.Single((await store.QueryAsync(new())).Items);
    }

    [Theory] [InlineData("\"schemaVersion\":2", "\"schemaVersion\":999")] [InlineData("\"formatVersion\":1", "\"formatVersion\":2")]
    [InlineData("\"appVersion\":\"0.3.0\"", "\"appVersion\":\"9.0.0\"")]
    public async Task NewerCompatibilityVersionsAreRejected(string find, string replacement)
    {
        await Play(); await store.CreateAsync(Archive);
        await RewriteEntry("manifest.json", bytes => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace(find, replacement)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive)); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Theory] [InlineData("../library.db")] [InlineData("/library.db")] [InlineData("C:/library.db")] [InlineData("library.db\\escape")]
    public async Task UnsafeArchivePathsAreNeverExtracted(string path)
    {
        using (var archive = ZipFile.Open(Archive, ZipArchiveMode.Create))
        { await using (var s = archive.CreateEntry(path).Open()) await s.WriteAsync(new byte[] { 1 }); await using var m = archive.CreateEntry("manifest.json").Open(); await m.WriteAsync(new byte[] { 1 }); }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
        Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task LinksDuplicateEntriesAndCompressionBombsAreRejected()
    {
        await Play(); await store.CreateAsync(Archive); var original = await File.ReadAllBytesAsync(Archive);
        using (var archive = ZipFile.Open(Archive, ZipArchiveMode.Update)) archive.GetEntry("library.db")!.ExternalAttributes = 0xA000 << 16;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
        await File.WriteAllBytesAsync(Archive, original);
        using (var archive = ZipFile.Open(Archive, ZipArchiveMode.Update)) archive.CreateEntry("manifest.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
        File.Delete(Archive);
        using (var archive = ZipFile.Open(Archive, ZipArchiveMode.Create))
        { await using (var s = archive.CreateEntry("library.db", CompressionLevel.SmallestSize).Open()) await s.WriteAsync(new byte[1024 * 1024]); await using var m = archive.CreateEntry("manifest.json").Open(); await m.WriteAsync(new byte[] { 1 }); }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InspectAsync(Archive));
    }

    [Fact] public async Task CancelledRestoreNeverActivatesOrLeavesStaging()
    {
        await SeedBoth(); await store.CreateAsync(Archive); var preview = await store.InspectAsync(Archive);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Google Calendar")));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RestoreAsync(preview, new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        Assert.Single((await store.QueryAsync(January)).Items); Assert.False(File.Exists(Database + ".restore-journal"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Database)!, ".relliga-stage-*"));
    }

    [Fact] public async Task VerificationDetectsFtsCorruptionWithoutRepairingIt()
    {
        await Play();
        await using (var c = new SqliteConnection($"Data Source={Database};Pooling=False"))
        { await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO events_fts(events_fts) VALUES('delete-all');"; await cmd.ExecuteNonQueryAsync(); }
        var report = await store.VerifyAsync(); Assert.False(report.Healthy); Assert.Contains("events_fts", report.Description);
        Assert.Empty((await store.QueryAsync(new("Synthetic"))).Items); Assert.Single((await store.QueryAsync(new())).Items);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(Archive)); Assert.False(File.Exists(Archive));
    }

    [Fact] public async Task UnknownDatabaseTriggersAreRejectedRatherThanExecuted()
    {
        await Play();
        await using (var c = new SqliteConnection($"Data Source={Database};Pooling=False"))
        { await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER unexpected AFTER INSERT ON data_sources BEGIN DELETE FROM events; END;"; await cmd.ExecuteNonQueryAsync(); }
        Assert.False((await store.VerifyAsync()).Healthy);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(Archive)); Assert.Single((await store.QueryAsync(new())).Items);
    }

    [Fact] public async Task StartupStagingCleanupDoesNotTouchAnotherLibrarysFiles()
    {
        var otherPrefix = ".relliga-stage-ffffffffffff-" + Guid.NewGuid().ToString("N");
        var other = Path.Combine(Path.GetDirectoryName(Database)!, otherPrefix); Directory.CreateDirectory(other);
        var marker = Path.Combine(other, "library.db"); await File.WriteAllTextAsync(marker, "Synthetic unrelated library staging");
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.True(File.Exists(marker));
    }

    private async Task RewriteEntry(string name, Func<byte[], byte[]> edit)
    {
        using var zip = ZipFile.Open(Archive, ZipArchiveMode.Update); var entry = zip.GetEntry(name)!;
        byte[] bytes; await using (var source = entry.Open()) { using var memory = new MemoryStream(); await source.CopyToAsync(memory); bytes = memory.ToArray(); }
        entry.Delete(); await using var target = zip.CreateEntry(name).Open(); await target.WriteAsync(edit(bytes));
    }
    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => action(value); }
}
