using System.Globalization;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore(string databasePath, ICalendarExpander? calendarExpander = null) : IHistoryStore, IDisposable
{
    private FileStream? ownership;
    private readonly SemaphoreSlim initialization = new(1, 1);
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relliga", "relliga.db");

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 15, Pooling = false }.ToString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await initialization.WaitAsync(ct);
        try
        {
            if (ownership is not null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            ownership = new FileStream(DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                await using var connection = await OpenAsync(ct);
                var version = Convert.ToInt32(await ScalarAsync(connection, null, "PRAGMA user_version;", ct));
                if (version > 2) throw new InvalidOperationException("This library was created by a newer Relliga version. Update the application.");
                await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", ct);
                using var tx = connection.BeginTransaction();
                if (version == 0) await ExecuteAsync(connection, tx, SchemaV1, ct);
                if (version < 2) await MigrateV2Async(connection, tx, ct);
                await ExecuteAsync(connection, tx,
                    "UPDATE import_operations SET status='Interrupted', message='The previous session ended before this import completed. No partial events were saved.' WHERE status='Running';", ct);
                tx.Commit();
            }
            catch { ownership.Dispose(); ownership = null; throw; }
        }
        finally { initialization.Release(); }
    }

    public async Task<long> BeginImportAsync(string fileName, string provider, string version, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        return Convert.ToInt64(await ScalarAsync(c, null, """
            INSERT INTO import_operations(file_name, provider, importer_version, started_at, status)
            VALUES($file,$provider,$version,$at,'Running') RETURNING id;
            """, ct, ("$file", fileName), ("$provider", provider), ("$version", version), ("$at", Stamp(DateTimeOffset.UtcNow))));
    }

    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<object?> ScalarAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); return await cmd.ExecuteScalarAsync(ct); }
    public void Dispose() { ownership?.Dispose(); initialization.Dispose(); }

    private const string SchemaV1 = """
        CREATE TABLE data_sources(provider TEXT PRIMARY KEY);
        CREATE TABLE import_operations(
          id INTEGER PRIMARY KEY, file_name TEXT NOT NULL, provider TEXT NOT NULL, importer_version TEXT NOT NULL,
          started_at TEXT NOT NULL, finished_at TEXT, status TEXT NOT NULL,
          added INTEGER NOT NULL DEFAULT 0, duplicates INTEGER NOT NULL DEFAULT 0, warnings INTEGER NOT NULL DEFAULT 0,message TEXT);
        CREATE TABLE events(
          id INTEGER PRIMARY KEY,provider TEXT NOT NULL REFERENCES data_sources(provider),fingerprint TEXT NOT NULL,
          occurrence INTEGER NOT NULL,ended_at TEXT NOT NULL,title TEXT,artist TEXT,album TEXT,
          duration_ms INTEGER NOT NULL CHECK(duration_ms>=0),kind TEXT NOT NULL,uri TEXT,metadata_json TEXT NOT NULL,
          first_import_id INTEGER NOT NULL REFERENCES import_operations(id),source_file TEXT NOT NULL,record_index INTEGER NOT NULL,
          UNIQUE(provider,fingerprint,occurrence));
        CREATE INDEX ix_events_date ON events(ended_at DESC,id DESC);
        CREATE TABLE event_origins(event_id INTEGER NOT NULL REFERENCES events(id),
          operation_id INTEGER NOT NULL REFERENCES import_operations(id),source_file TEXT NOT NULL,record_index INTEGER NOT NULL,
          PRIMARY KEY(operation_id,source_file,record_index));
        CREATE VIRTUAL TABLE events_fts USING fts5(title,artist,album,content='events',content_rowid='id',tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER events_ai AFTER INSERT ON events BEGIN
          INSERT INTO events_fts(rowid,title,artist,album) VALUES(new.id,new.title,new.artist,new.album); END;
        CREATE TRIGGER events_ad AFTER DELETE ON events BEGIN
          INSERT INTO events_fts(events_fts,rowid,title,artist,album) VALUES('delete',old.id,old.title,old.artist,old.album); END;
        CREATE TRIGGER events_au AFTER UPDATE ON events BEGIN
          INSERT INTO events_fts(events_fts,rowid,title,artist,album) VALUES('delete',old.id,old.title,old.artist,old.album);
          INSERT INTO events_fts(rowid,title,artist,album) VALUES(new.id,new.title,new.artist,new.album); END;
        PRAGMA user_version=1;
        """;
}
