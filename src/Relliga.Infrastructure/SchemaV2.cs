using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private static async Task MigrateV2Async(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            ALTER TABLE events ADD COLUMN stable_fingerprint TEXT;
            ALTER TABLE events ADD COLUMN stable_occurrence INTEGER;
            ALTER TABLE events ADD COLUMN importer_version TEXT;
            ALTER TABLE event_origins ADD COLUMN metadata_json TEXT;
            ALTER TABLE import_operations ADD COLUMN processed INTEGER;
            ALTER TABLE import_operations ADD COLUMN rejected INTEGER;
            ALTER TABLE import_operations ADD COLUMN updated INTEGER NOT NULL DEFAULT 0;
            CREATE UNIQUE INDEX ux_events_stable ON events(provider,stable_fingerprint,stable_occurrence);
            UPDATE events SET importer_version=(SELECT importer_version FROM import_operations WHERE id=events.first_import_id);
            CREATE TABLE calendar_components(
              id INTEGER PRIMARY KEY, uid TEXT NOT NULL, recurrence_id TEXT NOT NULL, definition_json TEXT NOT NULL,
              content_hash TEXT NOT NULL, title TEXT, description TEXT, location TEXT, start_sort TEXT NOT NULL,
              is_recurring INTEGER NOT NULL, is_cancelled INTEGER NOT NULL, sequence INTEGER NOT NULL, modified_at TEXT,
              first_import_id INTEGER NOT NULL REFERENCES import_operations(id),latest_import_id INTEGER NOT NULL REFERENCES import_operations(id),
              source_file TEXT NOT NULL,record_index INTEGER NOT NULL,importer_version TEXT NOT NULL,UNIQUE(uid,recurrence_id));
            CREATE INDEX ix_calendar_start ON calendar_components(start_sort);
            CREATE INDEX ix_calendar_series ON calendar_components(is_recurring,uid);
            CREATE TABLE calendar_origins(component_id INTEGER NOT NULL REFERENCES calendar_components(id),
              operation_id INTEGER NOT NULL REFERENCES import_operations(id),source_file TEXT NOT NULL,record_index INTEGER NOT NULL,
              definition_json TEXT NOT NULL,disposition TEXT NOT NULL, PRIMARY KEY(operation_id,source_file,record_index));
            CREATE VIRTUAL TABLE calendar_fts USING fts5(title,description,location,content='calendar_components',content_rowid='id',tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER calendar_ai AFTER INSERT ON calendar_components BEGIN
              INSERT INTO calendar_fts(rowid,title,description,location) VALUES(new.id,new.title,new.description,new.location); END;
            CREATE TRIGGER calendar_ad AFTER DELETE ON calendar_components BEGIN
              INSERT INTO calendar_fts(calendar_fts,rowid,title,description,location) VALUES('delete',old.id,old.title,old.description,old.location); END;
            CREATE TRIGGER calendar_au AFTER UPDATE ON calendar_components BEGIN
              INSERT INTO calendar_fts(calendar_fts,rowid,title,description,location) VALUES('delete',old.id,old.title,old.description,old.location);
              INSERT INTO calendar_fts(rowid,title,description,location) VALUES(new.id,new.title,new.description,new.location); END;
            """, ct);
        // Backfill in bounded batches. Existing rows are retained even if older imports already inflated multiplicity.
        long lastId = 0;
        while (true)
        {
            var batch = new List<(long Id, string Provider, string Fingerprint, string Metadata)>();
            using (var command = Command(c, tx, "SELECT id,provider,fingerprint,metadata_json FROM events WHERE id>$id ORDER BY id LIMIT 500;", ("$id", lastId)))
            await using (var r = await command.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) batch.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                var key = row.Fingerprint;
                if (row.Provider == "Spotify")
                {
                    using var json = JsonDocument.Parse(row.Metadata);
                    var root = json.RootElement;
                    string? Value(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    var uri = Value("spotify_track_uri") ?? Value("spotify_episode_uri");
                    if (!string.IsNullOrWhiteSpace(uri) && DateTimeOffset.TryParse(Value("ts"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var timestamp) && root.TryGetProperty("ms_played", out var ms) && ms.TryGetInt64(out var duration))
                        key = PlaybackIdentity.Create(uri, timestamp, duration, Value("username"), Value("platform"));
                }
                var ordinal = Convert.ToInt64(await ScalarAsync(c, tx,
                    "SELECT COALESCE(MAX(stable_occurrence),0)+1 FROM events WHERE provider=$p AND stable_fingerprint=$key;", ct,
                    ("$p", row.Provider), ("$key", key)));
                await ExecuteAsync(c, tx, "UPDATE events SET stable_fingerprint=$key,stable_occurrence=$n WHERE id=$id;", ct,
                    ("$key", key), ("$n", ordinal), ("$id", row.Id));
            }
            lastId = batch[^1].Id;
        }
        await ExecuteAsync(c, tx, "PRAGMA user_version=2;", ct);
    }
}
