using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class RecoveryHardeningTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library", "relliga.db");
    public async ValueTask InitializeAsync() { store = new(Database, new CalendarExpander()); await store.InitializeAsync(); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task Import(string title) => new ImportService(store, [new ExportImporter()]).ImportAsync(files.Write("[" + TestFiles.Row(title) + "]"), "Automatic");

    [Fact] public async Task VersionTwoBackupMigratesAdditiveIndexesAndRetainsAllIdentityAndProvenance()
    {
        await Import("Synthetic legacy"); var original = Assert.Single((await store.QueryAsync(new())).Items); store.Dispose();
        await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP INDEX ix_event_origins_event; DROP INDEX ix_calendar_origins_component; DROP INDEX ix_events_provider_date; PRAGMA user_version=2;";
            await cmd.ExecuteNonQueryAsync();
        }
        var bytes = await File.ReadAllBytesAsync(Database); var archivePath = Path.Combine(files.DirectoryPath, "v2.zip");
        var manifest = new BackupManifest(1, 2, "0.3.0", DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, 1, 0, 1, ["Spotify"]);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using (var entry = archive.CreateEntry("library.db").Open()) await entry.WriteAsync(bytes);
            await using var entryManifest = archive.CreateEntry("manifest.json").Open(); await JsonSerializer.SerializeAsync(entryManifest, manifest,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Equal(original, Assert.Single((await store.QueryAsync(new())).Items)); Assert.True((await store.VerifyAsync()).Healthy);
        var preview = await store.InspectAsync(archivePath); Assert.Equal(2, preview.Manifest.SchemaVersion); await store.RestoreAsync(preview);
        Assert.Equal(original, Assert.Single((await store.QueryAsync(new())).Items)); Assert.Single(await store.GetImportsAsync()); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task UnexpectedSchemaZeroDatabaseIsPreservedBeforeWalOrMigration()
    {
        var path = Path.Combine(files.DirectoryPath, "unrecognized.db");
        await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        { await c.OpenAsync(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE valuable_synthetic_data(value TEXT); INSERT INTO valuable_synthetic_data VALUES('preserve');"; await command.ExecuteNonQueryAsync(); }
        var before = await File.ReadAllBytesAsync(path);
        using var unrecognized = new SqliteHistoryStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => unrecognized.InitializeAsync()); Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact] public async Task AmbiguousRecoveryPreservesBothDatabasesAndJournal()
    {
        await Import("Previous"); store.Dispose(); var old = await File.ReadAllBytesAsync(Database);
        await File.WriteAllBytesAsync(Database + ".previous", old);
        store = new(Database); await store.InitializeAsync(); await Import("Unrelated active modification"); store.Dispose();
        var active = await File.ReadAllBytesAsync(Database);
        var journalPath = Database + ".restore-journal";
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new
        { Version = 1, PreviousSha256 = Convert.ToHexString(SHA256.HashData(old)), CandidateSha256 = new string('A', 64) }));
        store = new(Database); await Assert.ThrowsAsync<IOException>(() => store.InitializeAsync());
        Assert.Equal(active, await File.ReadAllBytesAsync(Database)); Assert.Equal(old, await File.ReadAllBytesAsync(Database + ".previous")); Assert.True(File.Exists(journalPath));
    }

    [Fact] public async Task InvalidJournalHashReportsValidationFailureWithoutChangingDatabase()
    {
        store.Dispose(); var before = await File.ReadAllBytesAsync(Database);
        await File.WriteAllTextAsync(Database + ".restore-journal", "{\"Version\":1,\"PreviousSha256\":null,\"CandidateSha256\":\"\"}");
        store = new(Database); await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync()); Assert.Equal(before, await File.ReadAllBytesAsync(Database));
    }

    [Fact] public void OutputChangedDuringProcessingIsNeverOverwritten()
    {
        var path = files.Write("original", "destination.json"); string temporary;
        using (var output = new AtomicOutput(path, true))
        {
            temporary = output.Temporary; File.WriteAllText(temporary, "completed new export"); File.WriteAllText(path, "new external content");
            Assert.Throws<IOException>(() => output.Publish(default)); Assert.Equal("new external content", File.ReadAllText(path));
        }
        Assert.False(File.Exists(temporary));
    }

    [Fact] public void ANewDestinationCreatedDuringProcessingIsPreservedEvenWithOverwriteFlag()
    {
        var path = Path.Combine(files.DirectoryPath, "new-destination.json");
        using var output = new AtomicOutput(path, true); File.WriteAllText(output.Temporary, "new export"); File.WriteAllText(path, "external file");
        Assert.Throws<IOException>(() => output.Publish(default)); Assert.Equal("external file", File.ReadAllText(path));
    }

    [Fact] public async Task StartupCleansAbandonedCandidateWalSidecarsWithoutBlockingTheHealthyLibrary()
    {
        await Import("Preserved");
        var staging = Path.Combine(Path.GetDirectoryName(Database)!, store.StagingPrefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        foreach (var name in new[] { "candidate.db", "candidate.db-wal", "candidate.db-shm" }) await File.WriteAllTextAsync(Path.Combine(staging, name), "Synthetic interrupted candidate");
        store.Dispose(); store = new(Database); await store.InitializeAsync();
        Assert.False(Directory.Exists(staging)); Assert.Equal("Preserved", Assert.Single((await store.QueryAsync(new())).Items).Event.Title);
    }
}
