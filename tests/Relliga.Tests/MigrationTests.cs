using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly TestFiles files = new();
    public void Dispose() => files.Dispose();

    [Fact] public async Task VersionOneBackupMigratesOnlyItsStagedCopyBeforeActivation()
    {
        var legacy = Path.Combine(files.DirectoryPath, "legacy-backup.db");
        await using (var c = new SqliteConnection($"Data Source={legacy};Pooling=False"))
        {
            await c.OpenAsync(); using var command = c.CreateCommand(); command.CommandText = V1 + """
                INSERT INTO import_operations(id,file_name,provider,importer_version,started_at,status) VALUES(7,'synthetic.json','Automatic','1.0.0','2025-01-01T00:00:00Z','Failed');
                """; await command.ExecuteNonQueryAsync();
        }
        var bytes = await File.ReadAllBytesAsync(legacy);
        var manifest = new BackupManifest(1, 1, "0.2.0", DateTimeOffset.UtcNow, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, 0, 0, 1, []);
        var path = Path.Combine(files.DirectoryPath, "legacy.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            await using (var entry = zip.CreateEntry("library.db").Open()) await entry.WriteAsync(bytes);
            await using var metadata = zip.CreateEntry("manifest.json").Open();
            await System.Text.Json.JsonSerializer.SerializeAsync(metadata, manifest, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        }
        using var store = new SqliteHistoryStore(Path.Combine(files.DirectoryPath, "target", "relliga.db"), new CalendarExpander());
        await store.InitializeAsync(); var preview = await store.InspectAsync(path); Assert.Equal(1, preview.Manifest.SchemaVersion);
        await store.RestoreAsync(preview); Assert.Equal(7, Assert.Single(await store.GetImportsAsync()).Id); Assert.True((await store.VerifyAsync()).Healthy);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(legacy));
    }

    [Fact] public async Task VersionOneLibraryUpgradesWithoutLosingRowsProvenanceOrOriginalVersions()
    {
        var path = Path.Combine(files.DirectoryPath, "legacy.db");
        var parsed = Assert.Single(await TestFiles.Read(files.Write($"[{TestFiles.Row()}]")));
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await c.OpenAsync(); using var schema = c.CreateCommand(); schema.CommandText = V1; await schema.ExecuteNonQueryAsync();
            using var seed = c.CreateCommand(); seed.CommandText = """
                INSERT INTO data_sources VALUES('Spotify');
                INSERT INTO import_operations(id,file_name,provider,importer_version,started_at,status,added)
                  VALUES(1,'original.json','Spotify','1.0.0','2025-01-02T00:00:00.0000000Z','Completed',1);
                INSERT INTO events(id,provider,fingerprint,occurrence,ended_at,title,artist,album,duration_ms,kind,uri,metadata_json,first_import_id,source_file,record_index)
                  VALUES(1,'Spotify',$fp,1,'2025-01-01T12:00:00.0000000Z','Synthetic song','Synthetic artist','Synthetic album',123000,'Listening','spotify:track:synthetic',$meta,1,'original.json',1);
                INSERT INTO event_origins VALUES(1,1,'original.json',1);
                """;
            seed.Parameters.AddWithValue("$fp", parsed.Fingerprint); seed.Parameters.AddWithValue("$meta", parsed.Event!.MetadataJson);
            await seed.ExecuteNonQueryAsync();
        }
        using var store = new SqliteHistoryStore(path, new CalendarExpander()); await store.InitializeAsync();
        var original = Assert.Single((await store.QueryAsync(new(Provider: "Spotify"))).Items);
        Assert.Equal(1, original.Id); Assert.Equal("1.0.0", original.ImporterVersion); Assert.Equal("original.json", original.SourceFile);
        var history = Assert.Single(await store.GetImportsAsync()); Assert.Null(history.Processed); Assert.Null(history.Rejected);
        var importer = new ExportImporter(); var service = new ImportService(store, [importer]);
        var changed = TestFiles.Row().Replace("Synthetic song", "Updated synthetic metadata");
        var repeated = await service.ImportAsync(files.Write($"[{changed}]"), "Automatic");
        Assert.Equal(0, repeated.Added); Assert.Equal(1, repeated.Duplicates);
        await service.ImportAsync(files.Write(Calendars.Document(Calendars.Event("DTSTART:20250103T100000Z")), "calendar.ics"), "Automatic");
        Assert.Equal(2, (await store.QueryAsync(new(From: Calendars.Date(), Until: Calendars.Date(2)))).Total);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False"); await check.OpenAsync();
        using var cmd = check.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM event_origins;"; Assert.Equal(2L, await cmd.ExecuteScalarAsync());
        cmd.CommandText = "PRAGMA user_version;"; Assert.Equal(3L, await cmd.ExecuteScalarAsync());
    }

    // Frozen Iteration 1 schema. Keep independent of the current migration implementation.
    private const string V1 = """
        CREATE TABLE data_sources(provider TEXT PRIMARY KEY);
        CREATE TABLE import_operations(id INTEGER PRIMARY KEY,file_name TEXT NOT NULL,provider TEXT NOT NULL,importer_version TEXT NOT NULL,
          started_at TEXT NOT NULL,finished_at TEXT,status TEXT NOT NULL,added INTEGER NOT NULL DEFAULT 0,duplicates INTEGER NOT NULL DEFAULT 0,
          warnings INTEGER NOT NULL DEFAULT 0,message TEXT);
        CREATE TABLE events(id INTEGER PRIMARY KEY,provider TEXT NOT NULL REFERENCES data_sources(provider),fingerprint TEXT NOT NULL,occurrence INTEGER NOT NULL,
          ended_at TEXT NOT NULL,title TEXT,artist TEXT,album TEXT,duration_ms INTEGER NOT NULL CHECK(duration_ms>=0),kind TEXT NOT NULL,uri TEXT,metadata_json TEXT NOT NULL,
          first_import_id INTEGER NOT NULL REFERENCES import_operations(id),source_file TEXT NOT NULL,record_index INTEGER NOT NULL,UNIQUE(provider,fingerprint,occurrence));
        CREATE INDEX ix_events_date ON events(ended_at DESC,id DESC);
        CREATE TABLE event_origins(event_id INTEGER NOT NULL REFERENCES events(id),operation_id INTEGER NOT NULL REFERENCES import_operations(id),
          source_file TEXT NOT NULL,record_index INTEGER NOT NULL,PRIMARY KEY(operation_id,source_file,record_index));
        CREATE VIRTUAL TABLE events_fts USING fts5(title,artist,album,content='events',content_rowid='id',tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER events_ai AFTER INSERT ON events BEGIN INSERT INTO events_fts(rowid,title,artist,album) VALUES(new.id,new.title,new.artist,new.album); END;
        CREATE TRIGGER events_ad AFTER DELETE ON events BEGIN INSERT INTO events_fts(events_fts,rowid,title,artist,album) VALUES('delete',old.id,old.title,old.artist,old.album); END;
        CREATE TRIGGER events_au AFTER UPDATE ON events BEGIN
          INSERT INTO events_fts(events_fts,rowid,title,artist,album) VALUES('delete',old.id,old.title,old.artist,old.album);
          INSERT INTO events_fts(rowid,title,artist,album) VALUES(new.id,new.title,new.artist,new.album); END;
        PRAGMA user_version=1;
        """;
}
