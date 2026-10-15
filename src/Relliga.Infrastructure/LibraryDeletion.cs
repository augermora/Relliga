using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore : ILibraryManagement
{
    public Task<DeletionPreview> PreviewDeletionAsync(DeletionTarget target, CancellationToken ct = default) =>
        Activity.RunAsync(async () =>
        {
            await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction(deferred: true);
            var preview = await PreviewDeletionCoreAsync(c, tx, target, ct); tx.Commit(); return preview;
        }, ct);

    private static async Task PrepareDeletionAsync(SqliteConnection c, SqliteTransaction tx, DeletionTarget target, CancellationToken ct)
    {
        if ((target.Provider is null) == (target.ImportId is null)) throw new ArgumentException("Choose exactly one source or import batch.");
        if (target.Provider is not null && target.Provider is not ("Spotify" or "Google Calendar")) throw new ArgumentException("Unknown source.");
        await ExecuteAsync(c, tx, """
            CREATE TEMP TABLE removed_event_origins AS SELECT o.* FROM event_origins o JOIN events e ON e.id=o.event_id
              WHERE ($op IS NOT NULL AND o.operation_id=$op) OR ($source IS NOT NULL AND e.provider=$source);
            CREATE TEMP TABLE removed_calendar_origins AS SELECT * FROM calendar_origins
              WHERE ($op IS NOT NULL AND operation_id=$op) OR $source='Google Calendar';
            CREATE TEMP TABLE affected_events AS SELECT DISTINCT event_id AS id FROM removed_event_origins;
            CREATE TEMP TABLE affected_components AS SELECT DISTINCT component_id AS id FROM removed_calendar_origins;
            CREATE TEMP TABLE affected_operations AS
              SELECT operation_id AS id FROM removed_event_origins UNION SELECT operation_id FROM removed_calendar_origins
              UNION SELECT id FROM import_operations WHERE id=$op OR ($source IS NOT NULL AND (provider=$source OR provider LIKE '%'||$source||'%'));
            """, ct, ("$op", target.ImportId), ("$source", target.Provider));
    }

    private static async Task<DeletionPreview> PreviewDeletionCoreAsync(SqliteConnection c, SqliteTransaction tx, DeletionTarget target, CancellationToken ct)
    {
        await PrepareDeletionAsync(c, tx, target, ct);
        async Task<long> Count(string sql) => Convert.ToInt64(await ScalarAsync(c, tx, sql, ct));
        var sharedEvents = await Count("SELECT COUNT(*) FROM affected_events a WHERE EXISTS (SELECT 1 FROM event_origins o WHERE o.event_id=a.id AND NOT EXISTS (SELECT 1 FROM removed_event_origins r WHERE r.operation_id=o.operation_id AND r.source_file=o.source_file AND r.record_index=o.record_index));");
        var sharedComponents = await Count("SELECT COUNT(*) FROM affected_components a WHERE EXISTS (SELECT 1 FROM calendar_origins o WHERE o.component_id=a.id AND NOT EXISTS (SELECT 1 FROM removed_calendar_origins r WHERE r.operation_id=o.operation_id AND r.source_file=o.source_file AND r.record_index=o.record_index));");
        var all = await Count("SELECT (SELECT COUNT(*) FROM affected_events)+(SELECT COUNT(*) FROM affected_components);");
        var operations = await Count("""
            SELECT COUNT(*) FROM affected_operations a WHERE NOT EXISTS
              (SELECT 1 FROM event_origins o WHERE o.operation_id=a.id AND NOT EXISTS (SELECT 1 FROM removed_event_origins r WHERE r.operation_id=o.operation_id AND r.source_file=o.source_file AND r.record_index=o.record_index))
              AND NOT EXISTS (SELECT 1 FROM calendar_origins o WHERE o.operation_id=a.id AND NOT EXISTS (SELECT 1 FROM removed_calendar_origins r WHERE r.operation_id=o.operation_id AND r.source_file=o.source_file AND r.record_index=o.record_index));
            """);
        return new(target, all - sharedEvents - sharedComponents, sharedEvents + sharedComponents, sharedComponents, operations,
            await RevisionAsync(c, tx, ct),
            $"Remove {target.Provider ?? $"import #{target.ImportId}"}: {all - sharedEvents - sharedComponents:N0} stored playback records / Calendar definitions removed; " +
            $"{sharedEvents + sharedComponents:N0} shared records retained; {sharedComponents:N0} Calendar definitions recomputed from remaining revisions (earlier events may reappear). " +
            $"{operations:N0} import history entries removed. Mixed-source history retained with filenames, diagnostics and counters cleared. " +
            "External original files are never changed. Exports, backups and any previous restore recovery copy remain. This is logical deletion, not secure erasure.");
    }

    public Task DeleteAsync(DeletionPreview confirmedPreview, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction();
        var current = await PreviewDeletionCoreAsync(c, tx, confirmedPreview.Target, ct);
        if (current.Revision != confirmedPreview.Revision) throw new InvalidOperationException("The library changed after the preview. Review a new deletion preview before confirming.");
        await ExecuteAsync(c, tx, """
            DELETE FROM event_origins WHERE EXISTS (SELECT 1 FROM removed_event_origins r WHERE r.operation_id=event_origins.operation_id AND r.source_file=event_origins.source_file AND r.record_index=event_origins.record_index);
            DELETE FROM calendar_origins WHERE EXISTS (SELECT 1 FROM removed_calendar_origins r WHERE r.operation_id=calendar_origins.operation_id AND r.source_file=calendar_origins.source_file AND r.record_index=calendar_origins.record_index);
            DELETE FROM events WHERE id IN (SELECT id FROM affected_events) AND NOT EXISTS (SELECT 1 FROM event_origins o WHERE o.event_id=events.id);
            DELETE FROM calendar_components WHERE id IN (SELECT id FROM affected_components) AND NOT EXISTS (SELECT 1 FROM calendar_origins o WHERE o.component_id=calendar_components.id);
            """, ct);
        // Reparent shared playback rows, including displayed original metadata, to a surviving owner.
        await ExecuteAsync(c, tx, """
            UPDATE events SET
              first_import_id=(SELECT operation_id FROM event_origins o WHERE o.event_id=events.id ORDER BY operation_id,source_file,record_index LIMIT 1),
              source_file=(SELECT source_file FROM event_origins o WHERE o.event_id=events.id ORDER BY operation_id,source_file,record_index LIMIT 1),
              record_index=(SELECT record_index FROM event_origins o WHERE o.event_id=events.id ORDER BY operation_id,source_file,record_index LIMIT 1),
              metadata_json=COALESCE((SELECT metadata_json FROM event_origins o WHERE o.event_id=events.id ORDER BY operation_id,source_file,record_index LIMIT 1),metadata_json),
              importer_version=(SELECT i.importer_version FROM event_origins o JOIN import_operations i ON i.id=o.operation_id WHERE o.event_id=events.id ORDER BY operation_id,source_file,record_index LIMIT 1)
              WHERE id IN (SELECT id FROM affected_events);
            UPDATE events SET title=COALESCE(json_extract(metadata_json,'$.master_metadata_track_name'),json_extract(metadata_json,'$.episode_name')),
              artist=COALESCE(json_extract(metadata_json,'$.master_metadata_album_artist_name'),json_extract(metadata_json,'$.episode_show_name')),
              album=json_extract(metadata_json,'$.master_metadata_album_album_name'),
              uri=COALESCE(json_extract(metadata_json,'$.spotify_track_uri'),json_extract(metadata_json,'$.spotify_episode_uri')),
              kind=CASE WHEN json_extract(metadata_json,'$.episode_name') IS NOT NULL OR json_extract(metadata_json,'$.spotify_episode_uri') IS NOT NULL
                THEN 'Podcast' ELSE 'Listening' END
              WHERE provider='Spotify' AND id IN (SELECT id FROM affected_events);
            """, ct);
        long last = 0;
        while (true)
        {
            var id = await ScalarAsync(c, tx, "SELECT MIN(id) FROM calendar_components WHERE id>$last AND id IN (SELECT id FROM affected_components);", ct, ("$last", last));
            if (id is null or DBNull) break;
            last = Convert.ToInt64(id); await RecomputeCalendarAsync(c, tx, last, ct);
        }
        await ExecuteAsync(c, tx, """
            DELETE FROM import_operations WHERE id IN (SELECT id FROM affected_operations)
              AND NOT EXISTS (SELECT 1 FROM event_origins o WHERE o.operation_id=import_operations.id)
              AND NOT EXISTS (SELECT 1 FROM calendar_origins o WHERE o.operation_id=import_operations.id);
            UPDATE import_operations SET file_name='[source data removed]',message=NULL,added=0,duplicates=0,warnings=0,processed=NULL,rejected=NULL,updated=0,
              importer_version='[historical counters and diagnostics removed]',status='Retained provenance',
              provider=CASE WHEN EXISTS (SELECT 1 FROM event_origins o WHERE o.operation_id=import_operations.id)
                THEN CASE WHEN EXISTS (SELECT 1 FROM calendar_origins o WHERE o.operation_id=import_operations.id) THEN 'Google Calendar + Spotify' ELSE 'Spotify' END ELSE 'Google Calendar' END
              WHERE id IN (SELECT id FROM affected_operations);
            DELETE FROM data_sources WHERE NOT EXISTS (SELECT 1 FROM events e WHERE e.provider=data_sources.provider)
              AND (provider<>'Google Calendar' OR NOT EXISTS (SELECT 1 FROM calendar_components));
            """, ct);
        MaintenanceFault?.Invoke("DeleteBeforeCommit");
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }, ct);

    private static async Task RecomputeCalendarAsync(SqliteConnection c, SqliteTransaction tx, long id, CancellationToken ct)
    {
        CalendarDefinition? winner = null;
        long first = 0, latest = 0; string? file = null, version = null; int index = 0;
        using (var cmd = Command(c, tx, """
            SELECT o.operation_id,o.source_file,o.record_index,o.definition_json,i.importer_version FROM calendar_origins o
            JOIN import_operations i ON i.id=o.operation_id WHERE component_id=$id ORDER BY operation_id,
              CASE disposition WHEN 'Added' THEN 0 WHEN 'Updated' THEN 0 WHEN 'Already present' THEN 1 WHEN 'Stale' THEN 2 ELSE 3 END,source_file,record_index;
            """, ("$id", id)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var candidate = JsonSerializer.Deserialize<CalendarDefinition>(r.GetString(3))!;
                if (first == 0) first = r.GetInt64(0);
                if (winner is null || candidate.Sequence > winner.Sequence ||
                    candidate.Sequence == winner.Sequence && Nullable.Compare(candidate.ModifiedAt, winner.ModifiedAt) > 0)
                { winner = candidate; latest = r.GetInt64(0); file = r.GetString(1); index = r.GetInt32(2); version = r.GetString(4); }
            }
        var def = winner ?? throw new InvalidDataException("A shared Calendar definition has no surviving provenance.");
        await ExecuteAsync(c, tx, """
            UPDATE calendar_components SET definition_json=$json,content_hash=$hash,title=$title,description=$desc,location=$loc,
              start_sort=$start,is_recurring=$recurring,is_cancelled=$cancelled,sequence=$seq,modified_at=$modified,
              first_import_id=$first,latest_import_id=$latest,source_file=$file,record_index=$index,importer_version=$version WHERE id=$id;
            """, ct, ("$json", JsonSerializer.Serialize(def)), ("$hash", def.ContentHash), ("$title", def.Title), ("$desc", def.Description),
            ("$loc", def.Location), ("$start", def.StartLocal.ToString("s", CultureInfo.InvariantCulture)), ("$recurring", def.IsRecurring),
            ("$cancelled", def.IsCancelled), ("$seq", def.Sequence), ("$modified", def.ModifiedAt is { } time ? Stamp(time) : null),
            ("$first", first), ("$latest", latest), ("$file", file), ("$index", index), ("$version", version), ("$id", id));
    }

    private static async Task<string> RevisionAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in new[] { "events", "event_origins", "calendar_components", "calendar_origins", "import_operations", "data_sources" })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table));
            using var cmd = Command(c, tx, $"SELECT * FROM {table} ORDER BY rowid;"); await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var values = new object[r.FieldCount]; r.GetValues(values);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(values.Select(v => v is DBNull ? null : v)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
