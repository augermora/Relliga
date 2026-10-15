using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    public async Task<ImportResult> CommitImportAsync(long operationId, string provider,
        IAsyncEnumerable<ImportRecord> records, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var tx = c.BeginTransaction();
        var stableCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var providers = new SortedSet<string>(); var versions = new SortedSet<string>();
        int added = 0, duplicates = 0, warnings = 0, processed = 0, rejected = 0, updated = 0, fileErrors = 0;
        var samples = new List<string>();
        try
        {
            await foreach (var record in records.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                var source = record.Provider ?? provider;
                var version = record.ImporterVersion;
                if (source != "Automatic") providers.Add(source);
                if (version is not null) versions.Add($"{source} {version}");
                if (record.Warning is not null)
                {
                    warnings++;
                    if (samples.Count < 100) samples.Add(record.Warning);
                }
                if (record.IsFileError) fileErrors++;
                if (record.Event is null && record.Calendar is null)
                {
                    if (record.Rejected) { processed++; rejected++; }
                    continue;
                }
                processed++;
                await ExecuteAsync(c, tx, "INSERT INTO data_sources(provider) VALUES($p) ON CONFLICT DO NOTHING;", ct, ("$p", source));
                if (record.Calendar is { } calendar)
                {
                    var disposition = await SaveCalendarAsync(c, tx, calendar, record, operationId, version ?? "1.0.0", ct);
                    if (disposition == "Added") added++;
                    else if (disposition == "Updated") updated++;
                    else duplicates++;
                    if (disposition is "Stale" or "Conflict")
                    {
                        warnings++;
                        if (samples.Count < 100) samples.Add($"{record.SourceFile}, record {record.RecordIndex}: {(disposition == "Stale" ? "older calendar revision skipped" : "conflicting revision without a newer sequence/timestamp; existing definition retained")}.");
                    }
                    continue;
                }
                var ev = record.Event!;
                if (ev.EndedAt is null || ev.DurationMs is null || string.IsNullOrWhiteSpace(record.Fingerprint))
                    throw new InvalidDataException("Playback importer did not provide its timestamp, duration or event identity.");
                var stableKey = source + ":" + (record.StableFingerprint ?? record.Fingerprint);
                var rawKey = source + ":" + record.Fingerprint;
                var occurrence = rawCounts.GetValueOrDefault(rawKey) + 1; rawCounts[rawKey] = occurrence;
                var stableOccurrence = stableCounts.GetValueOrDefault(stableKey) + 1; stableCounts[stableKey] = stableOccurrence;
                var fingerprint = record.StableFingerprint ?? record.Fingerprint;
                var id = await ScalarAsync(c, tx, """
                    INSERT INTO events(provider,fingerprint,occurrence,ended_at,title,artist,album,duration_ms,kind,uri,metadata_json,
                      first_import_id,source_file,record_index,stable_fingerprint,stable_occurrence,importer_version)
                    VALUES($p,$fp,$occ,$at,$title,$artist,$album,$ms,$kind,$uri,$meta,$op,$file,$index,$stable,$sn,
                      COALESCE($version,(SELECT importer_version FROM import_operations WHERE id=$op)))
                    ON CONFLICT DO NOTHING RETURNING id;
                    """, ct, ("$p", source), ("$fp", record.Fingerprint), ("$occ", occurrence), ("$at", Stamp(ev.EndedAt.Value)),
                    ("$title", ev.Title), ("$artist", ev.Artist), ("$album", ev.Album), ("$ms", ev.DurationMs),
                    ("$kind", ev.Kind), ("$uri", ev.Uri), ("$meta", ev.MetadataJson), ("$op", operationId),
                    ("$file", record.SourceFile), ("$index", record.RecordIndex), ("$stable", fingerprint), ("$sn", stableOccurrence), ("$version", version));
                if (id is not null) added++;
                else
                {
                    duplicates++;
                    id = await ScalarAsync(c, tx, "SELECT id FROM events WHERE provider=$p AND stable_fingerprint=$s AND stable_occurrence=$n;", ct,
                        ("$p", source), ("$s", fingerprint), ("$n", stableOccurrence));
                    if (id is null) throw new InvalidDataException("Conflicting playback identities; no records have been saved. Report this export structure to the maintainers.");
                }
                await ExecuteAsync(c, tx, """
                    INSERT INTO event_origins(event_id,operation_id,source_file,record_index,metadata_json) VALUES($id,$op,$file,$index,$meta);
                    """, ct, ("$id", id), ("$op", operationId), ("$file", record.SourceFile), ("$index", record.RecordIndex), ("$meta", ev.MetadataJson));
            }
            var summary = $"{added:N0} records added · {duplicates:N0} already present · {updated:N0} updated · {rejected:N0} rejected · {warnings:N0} warnings";
            if (fileErrors > 0) summary += $"\n{fileErrors:N0} archive members could not be fully parsed; their unparsed records cannot be counted.";
            if (samples.Count > 0) summary += "\n\n" + string.Join('\n', samples) + (warnings > samples.Count ? $"\n… {warnings - samples.Count:N0} further warnings." : "");
            await ExecuteAsync(c, tx, """
                UPDATE import_operations SET status=$status,added=$a,duplicates=$d,warnings=$w,updated=$u,processed=$p,rejected=$r,
                  message=$m,finished_at=$at,provider=$provider,importer_version=CASE WHEN $versions='' THEN importer_version ELSE $versions END WHERE id=$id;
                """, ct, ("$status", rejected > 0 || fileErrors > 0 ? "Partially completed" : warnings > 0 ? "Completed with warnings" : "Completed"),
                ("$a", added), ("$d", duplicates), ("$w", warnings), ("$u", updated), ("$p", processed), ("$r", rejected), ("$m", summary),
                ("$at", Stamp(DateTimeOffset.UtcNow)), ("$provider", providers.Count > 0 ? string.Join(" + ", providers) : provider),
                ("$versions", string.Join("; ", versions)), ("$id", operationId));
            ct.ThrowIfCancellationRequested(); tx.Commit();
            return new(operationId, added, duplicates, warnings, summary, updated, processed, rejected);
        }
        catch (Exception failure)
        {
            try
            {
                tx.Rollback();
                await ExecuteAsync(c, null, """
                    UPDATE import_operations SET processed=$p,rejected=$r,warnings=$w,duplicates=$d,provider=$provider,
                      importer_version=CASE WHEN $versions='' THEN importer_version ELSE $versions END,message=$m WHERE id=$id;
                    """, CancellationToken.None, ("$p", processed), ("$r", rejected), ("$w", warnings), ("$d", duplicates),
                    ("$provider", providers.Count > 0 ? string.Join(" + ", providers) : provider), ("$versions", string.Join("; ", versions)),
                    ("$m", samples.Count > 0 ? string.Join('\n', samples) : null), ("$id", operationId));
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Import failed and its recovery statistics could not be saved.", failure, recoveryError);
            }
            throw;
        }
    }

    private static async Task<string> SaveCalendarAsync(SqliteConnection c, SqliteTransaction tx, CalendarDefinition def,
        ImportRecord record, long operationId, string version, CancellationToken ct)
    {
        var existing = await ScalarAsync(c, tx, "SELECT definition_json FROM calendar_components WHERE uid=$uid AND recurrence_id=$rid;", ct,
            ("$uid", def.Uid), ("$rid", def.RecurrenceId));
        var disposition = "Added";
        if (existing is string json)
        {
            var old = JsonSerializer.Deserialize<CalendarDefinition>(json)!;
            var timeOrder = Nullable.Compare(def.ModifiedAt, old.ModifiedAt);
            disposition = old.ContentHash == def.ContentHash ? "Already present" :
                def.Sequence < old.Sequence || def.Sequence == old.Sequence && timeOrder < 0 ? "Stale" :
                def.Sequence > old.Sequence || def.Sequence == old.Sequence && timeOrder > 0 ? "Updated" : "Conflict";
        }
        var serialized = JsonSerializer.Serialize(def);
        if (disposition is "Added" or "Updated")
            await ExecuteAsync(c, tx, """
                INSERT INTO calendar_components(uid,recurrence_id,definition_json,content_hash,title,description,location,start_sort,
                  is_recurring,is_cancelled,sequence,modified_at,first_import_id,latest_import_id,source_file,record_index,importer_version)
                VALUES($uid,$rid,$json,$hash,$title,$description,$location,$start,$recurring,$cancelled,$sequence,$modified,$op,$op,$file,$index,$version)
                ON CONFLICT(uid,recurrence_id) DO UPDATE SET definition_json=excluded.definition_json,content_hash=excluded.content_hash,
                  title=excluded.title,description=excluded.description,location=excluded.location,start_sort=excluded.start_sort,
                  is_recurring=excluded.is_recurring,is_cancelled=excluded.is_cancelled,sequence=excluded.sequence,modified_at=excluded.modified_at,
                  latest_import_id=excluded.latest_import_id,source_file=excluded.source_file,record_index=excluded.record_index,importer_version=excluded.importer_version;
                """, ct, ("$uid", def.Uid), ("$rid", def.RecurrenceId), ("$json", serialized), ("$hash", def.ContentHash),
                ("$title", def.Title), ("$description", def.Description), ("$location", def.Location),
                ("$start", def.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                ("$recurring", def.IsRecurring), ("$cancelled", def.IsCancelled), ("$sequence", def.Sequence),
                ("$modified", def.ModifiedAt is { } modified ? Stamp(modified) : null), ("$op", operationId),
                ("$file", record.SourceFile), ("$index", record.RecordIndex), ("$version", version));
        await ExecuteAsync(c, tx, """
            INSERT INTO calendar_origins(component_id,operation_id,source_file,record_index,definition_json,disposition)
            VALUES((SELECT id FROM calendar_components WHERE uid=$uid AND recurrence_id=$rid),$op,$file,$index,$json,$disposition);
            """, ct, ("$uid", def.Uid), ("$rid", def.RecurrenceId), ("$op", operationId), ("$file", record.SourceFile),
            ("$index", record.RecordIndex), ("$json", serialized), ("$disposition", disposition));
        return disposition;
    }

    public async Task FinishUnsuccessfulAsync(long operationId, string status, string message)
    {
        await using var c = await OpenAsync(CancellationToken.None);
        await ExecuteAsync(c, null, """
            UPDATE import_operations SET status=$s,message=$m || CASE WHEN message IS NULL THEN '' ELSE char(10)||message END,finished_at=$at WHERE id=$id;
            """, CancellationToken.None, ("$s", status), ("$m", message), ("$at", Stamp(DateTimeOffset.UtcNow)), ("$id", operationId));
    }
}
