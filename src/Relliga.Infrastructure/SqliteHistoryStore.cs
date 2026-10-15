using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed class SqliteHistoryStore(string databasePath) : IHistoryStore, IDisposable
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
                await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", ct);
                var version = Convert.ToInt32(await ScalarAsync(connection, null, "PRAGMA user_version;", ct));
                if (version > 1) throw new InvalidOperationException("This library was created by a newer Relliga version. Update the application.");
                using var tx = connection.BeginTransaction();
                if (version == 0) await ExecuteAsync(connection, tx, SchemaV1, ct);
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

    public async Task<ImportResult> CommitImportAsync(long operationId, string provider,
        IAsyncEnumerable<ImportRecord> records, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        await ExecuteAsync(c, tx, "INSERT INTO data_sources(provider) VALUES($p) ON CONFLICT DO NOTHING;", ct, ("$p", provider));
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        int added = 0, duplicates = 0, warnings = 0;
        var samples = new List<string>();
        await foreach (var record in records.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (record.Event is not { } ev)
            {
                warnings++;
                if (samples.Count < 20) samples.Add(record.Warning ?? "Unsupported record.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(record.Fingerprint)) throw new InvalidDataException("Importer did not provide an event identity.");
            var occurrence = occurrences.GetValueOrDefault(record.Fingerprint) + 1;
            occurrences[record.Fingerprint] = occurrence;
            var id = await ScalarAsync(c, tx, """
                INSERT INTO events(provider,fingerprint,occurrence,ended_at,title,artist,album,duration_ms,kind,uri,metadata_json,first_import_id,source_file,record_index)
                VALUES($p,$fp,$occ,$at,$title,$artist,$album,$ms,$kind,$uri,$meta,$op,$file,$index)
                ON CONFLICT(provider,fingerprint,occurrence) DO NOTHING RETURNING id;
                """, ct, ("$p", provider), ("$fp", record.Fingerprint), ("$occ", occurrence), ("$at", Stamp(ev.EndedAt)),
                ("$title", ev.Title), ("$artist", ev.Artist), ("$album", ev.Album), ("$ms", ev.DurationMs),
                ("$kind", ev.Kind), ("$uri", ev.Uri), ("$meta", ev.MetadataJson), ("$op", operationId),
                ("$file", record.SourceFile), ("$index", record.RecordIndex));
            if (id is not null) added++;
            else
            {
                duplicates++;
                id = await ScalarAsync(c, tx,
                    "SELECT id FROM events WHERE provider=$p AND fingerprint=$fp AND occurrence=$occ;", ct,
                    ("$p", provider), ("$fp", record.Fingerprint), ("$occ", occurrence));
            }
            await ExecuteAsync(c, tx, """
                INSERT INTO event_origins(event_id,operation_id,source_file,record_index) VALUES($id,$op,$file,$index);
                """, ct, ("$id", id), ("$op", operationId), ("$file", record.SourceFile), ("$index", record.RecordIndex));
        }
        var summary = $"{added:N0} events added · {duplicates:N0} already present · {warnings:N0} warnings";
        if (samples.Count > 0) summary += "\n\n" + string.Join("\n", samples) +
            (warnings > samples.Count ? $"\n… {warnings - samples.Count:N0} further warnings." : "");
        await ExecuteAsync(c, tx, """
            UPDATE import_operations SET status=$status,added=$added,duplicates=$duplicates,warnings=$warnings,message=$message,finished_at=$at WHERE id=$id;
            """, ct, ("$status", warnings > 0 ? "Completed with warnings" : "Completed"), ("$added", added),
            ("$duplicates", duplicates), ("$warnings", warnings), ("$message", summary),
            ("$at", Stamp(DateTimeOffset.UtcNow)), ("$id", operationId));
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return new(operationId, added, duplicates, warnings, summary);
    }

    public async Task FinishUnsuccessfulAsync(long operationId, string status, string message)
    {
        await using var c = await OpenAsync(CancellationToken.None);
        await ExecuteAsync(c, null, "UPDATE import_operations SET status=$s,message=$m,finished_at=$at WHERE id=$id;",
            CancellationToken.None, ("$s", status), ("$m", message), ("$at", Stamp(DateTimeOffset.UtcNow)), ("$id", operationId));
    }

    public async Task<LibrarySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var count = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM events;", ct));
        var sources = Convert.ToInt32(await ScalarAsync(c, null, "SELECT COUNT(*) FROM data_sources;", ct));
        var imports = await GetImportsAsync(ct);
        return new(count, sources, imports.FirstOrDefault());
    }

    public async Task<IReadOnlyList<ImportOperation>> GetImportsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, null, "SELECT id,file_name,status,started_at,added,duplicates,warnings,message FROM import_operations ORDER BY id DESC LIMIT 20;");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ImportOperation>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), Text(reader, 7)));
        return result;
    }

    public async Task<EventPage> QueryAsync(EventQuery query, CancellationToken ct = default)
    {
        if (query.Page < 0 || query.PageSize is < 1 or > 200 || query.Text?.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(query), "Use pages of 1–200 events and queries up to 500 characters.");
        var terms = Regex.Matches(query.Text ?? "", @"[\p{L}\p{N}_]+")
            .Select(m => $"\"{m.Value}\"*").Take(30).ToArray();
        var where = "WHERE ($from IS NULL OR e.ended_at >= $from) AND ($until IS NULL OR e.ended_at < $until)";
        if (terms.Length > 0) where += " AND e.id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH $text)";
        var parameters = new List<(string, object?)>
        { ("$from", query.From is { } from ? Stamp(from) : null), ("$until", query.Until is { } until ? Stamp(until) : null) };
        if (terms.Length > 0) parameters.Add(("$text", string.Join(" AND ", terms)));
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction(deferred: true);
        var total = Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM events e " + where, ct, parameters.ToArray()));
        parameters.Add(("$limit", query.PageSize));
        parameters.Add(("$offset", (long)query.Page * query.PageSize));
        using var command = Command(c, tx, """
            SELECT e.id,e.ended_at,e.title,e.artist,e.album,e.duration_ms,e.kind,e.uri,e.metadata_json,
                   e.provider,e.source_file,e.record_index,i.importer_version,i.started_at
            FROM events e JOIN import_operations i ON i.id=e.first_import_id
            """ + " " + where + " ORDER BY e.ended_at DESC,e.id DESC LIMIT $limit OFFSET $offset;", parameters.ToArray());
        await using var r = await command.ExecuteReaderAsync(ct);
        var items = new List<StoredEvent>();
        while (await r.ReadAsync(ct)) items.Add(new(r.GetInt64(0),
            new(DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), Text(r, 2), Text(r, 3), Text(r, 4),
                r.GetInt64(5), r.GetString(6), Text(r, 7), r.GetString(8)), r.GetString(9), r.GetString(10),
            r.GetInt32(11), r.GetString(12), DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture)));
        await r.DisposeAsync();
        tx.Commit();
        return new(items, total);
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
