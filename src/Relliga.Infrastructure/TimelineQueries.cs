using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private async Task<EventPage> QueryCoreAsync(EventQuery query, CancellationToken ct,
        Func<StoredEvent, SqliteConnection, SqliteTransaction, Task>? sink = null, Func<EventQuery, Task>? prepared = null)
    {
        if (query.Page < 0 || query.PageSize is < 1 or > 200 || query.Text?.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(query), "Use pages of 1–200 events and queries up to 500 characters.");
        await using var c = await OpenAsync(ct);
        await ExecuteAsync(c, null, "PRAGMA temp_store=FILE;", ct);
        using var tx = c.BeginTransaction(deferred: true);
        var includeCalendar = query.Provider is null or "Google Calendar" && query.Kind is null or "Calendar";
        var hasCalendars = includeCalendar && Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM calendar_components;", ct)) > 0;
        var from = query.From;
        var until = query.Until;
        if (hasCalendars)
        {
            from ??= until?.AddDays(-90) ?? new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-30), TimeSpan.Zero);
            until ??= from.Value.AddDays(90);
            if (until <= from || until - from > TimeSpan.FromDays(366))
                throw new ArgumentException("Choose a date range of at most 366 days when including Calendar. Select Spotify to browse an unrestricted playback history.");
            if (calendarExpander is null) throw new InvalidOperationException("Calendar evaluation is not configured.");
        }
        if (from is not null && until <= from) throw new ArgumentException("The end date must follow the start date.");
        if (prepared is not null) await prepared(query with { From = from, Until = until, Page = 0 });
        var terms = Regex.Matches(query.Text ?? "", @"[\p{L}\p{N}_]+")
            .Select(m => $"\"{m.Value}\"*").Take(30).ToArray();
        var text = terms.Length > 0 ? string.Join(" AND ", terms) : null;
        await ExecuteAsync(c, tx, """
            CREATE TEMP TABLE occurrences(component_id INTEGER NOT NULL,sort_key TEXT NOT NULL,occurrence_key TEXT NOT NULL,payload TEXT NOT NULL,
              PRIMARY KEY(component_id,occurrence_key));
            CREATE INDEX temp.ix_occurrences_sort ON occurrences(sort_key DESC,component_id DESC,occurrence_key);
            """, ct);
        if (hasCalendars) await PopulateOccurrencesAsync(c, tx, from!.Value, until!.Value, text, ct);
        var (where, parameters) = PlaybackFilter(query with { From = from, Until = until }, text);
        var playbackFrom = PlaybackFrom(text);
        var playbackCount = Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM " + playbackFrom + " " + where, ct, parameters.ToArray()));
        var occurrenceCount = Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM occurrences;", ct));
        var count = playbackCount + occurrenceCount;
        parameters.Add(("$limit", sink is null ? query.PageSize : -1)); parameters.Add(("$offset", sink is null ? (long)query.Page * query.PageSize : 0));
        var references = new List<(long Id, bool Calendar, string? Payload)>();
        var sql = occurrenceCount == 0 ? "SELECT e.id,0,NULL FROM " + playbackFrom + " " + where + " ORDER BY e.ended_at DESC,e.id DESC LIMIT $limit OFFSET $offset;" :
            playbackCount == 0 ? "SELECT component_id,1,payload FROM occurrences ORDER BY sort_key DESC,component_id DESC,occurrence_key LIMIT $limit OFFSET $offset;" : """
            WITH timeline AS (
              SELECT e.id,0 AS calendar,e.ended_at AS sort_key,'' AS occurrence_key,NULL AS payload FROM
            """ + " " + playbackFrom + " " + where + """
              UNION ALL SELECT component_id,1,sort_key,occurrence_key,payload FROM occurrences)
            SELECT id,calendar,payload FROM timeline ORDER BY sort_key DESC,calendar DESC,id DESC,occurrence_key LIMIT $limit OFFSET $offset;
            """;
        using (var cmd = Command(c, tx, sql, parameters.ToArray()))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var reference = (Id: r.GetInt64(0), Calendar: r.GetBoolean(1), Payload: Text(r, 2));
                if (sink is null) references.Add(reference);
                else await sink(await ReadStoredEventAsync(c, tx, reference.Id, reference.Calendar,
                    reference.Payload is null ? null : JsonSerializer.Deserialize<CalendarOccurrence>(reference.Payload), ct), c, tx);
            }
        var items = new List<StoredEvent>();
        foreach (var reference in references)
            items.Add(await ReadStoredEventAsync(c, tx, reference.Id, reference.Calendar,
                reference.Payload is null ? null : JsonSerializer.Deserialize<CalendarOccurrence>(reference.Payload), ct));
        tx.Commit();
        return new(items, count, hasCalendars ? from : null, hasCalendars ? until : null);
    }

    // FTS supplies rowids. Avoid the provider/date index turning selective text searches into a provider-wide scan.
    internal static string PlaybackFrom(string? text) => text is null ? "events e" : "events e NOT INDEXED";

    internal static (string Sql, List<(string, object?)> Parameters) PlaybackFilter(EventQuery query, string? text)
    {
        var clauses = new List<string>(); var parameters = new List<(string, object?)>();
        void Add(string expression, string name, object? value)
        { if (value is not null) { clauses.Add(expression); parameters.Add((name, value)); } }
        Add("e.ended_at >= $from", "$from", query.From is { } from ? Stamp(from) : null);
        Add("e.ended_at < $until", "$until", query.Until is { } until ? Stamp(until) : null);
        Add("e.provider=$provider", "$provider", query.Provider);
        Add("e.kind=$kind", "$kind", query.Kind);
        Add("e.id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH $text)", "$text", text);
        return (clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses), parameters);
    }

    private async Task PopulateOccurrencesAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset from,
        DateTimeOffset until, string? text, CancellationToken ct)
    {
        // Read candidate series in batches, keeping a single series in managed memory at a time.
        string lastUid = ""; int definitionsRead = 0, occurrenceCount = 0;
        while (true)
        {
            var uids = new List<string>();
            using (var cmd = Command(c, tx, """
                SELECT DISTINCT uid FROM calendar_components WHERE uid>$uid AND
                  (is_recurring=1 OR recurrence_id<>'' OR start_sort >= $from AND start_sort < $until)
                ORDER BY uid LIMIT 100;
                """, ("$uid", lastUid), ("$from", from.UtcDateTime.AddDays(-2).ToString("s", CultureInfo.InvariantCulture)),
                ("$until", until.UtcDateTime.AddDays(2).ToString("s", CultureInfo.InvariantCulture))))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) uids.Add(r.GetString(0));
            if (uids.Count == 0) break;
            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();
                var definitions = new List<CalendarDefinition>();
                long definitionBytes = 0;
                var ids = new Dictionary<string, (long Id, bool Matches)>(StringComparer.Ordinal);
                var match = text is null ? "1" : "id IN (SELECT rowid FROM calendar_fts WHERE calendar_fts MATCH $text)";
                var args = new List<(string, object?)> { ("$uid", uid) };
                if (text is not null) args.Add(("$text", text));
                using (var cmd = Command(c, tx, $"SELECT id,definition_json,{match} FROM calendar_components WHERE uid=$uid LIMIT 10001;", args.ToArray()))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                    {
                        if (++definitionsRead > 20000) throw new InvalidOperationException("Calendar query exceeds the 20,000 definition safety limit. Filter by source or narrow the library.");
                        var definitionJson = r.GetString(1);
                        definitionBytes += definitionJson.Length * 2L;
                        if (definitionBytes > 32 * 1024 * 1024) throw new InvalidOperationException("A calendar series exceeds the 32 MiB evaluation memory budget.");
                        var def = JsonSerializer.Deserialize<CalendarDefinition>(definitionJson)!;
                        definitions.Add(def); ids[def.RecurrenceId] = (r.GetInt64(0), r.GetBoolean(2));
                    }
                if (definitions.Count > 10000) throw new InvalidOperationException("A calendar series contains too many overrides.");
                if (!ids.Values.Any(x => x.Matches)) continue;
                foreach (var occurrence in calendarExpander!.Expand(definitions, from, until, ct))
                {
                    if (++occurrenceCount > 50000) throw new InvalidOperationException("Calendar query exceeds 50,000 occurrences. Narrow the date window.");
                    var component = ids[occurrence.ComponentRecurrenceId];
                    if (!component.Matches) continue;
                    var sort = occurrence.StartInstant?.UtcDateTime ?? occurrence.StartLocal;
                    await ExecuteAsync(c, tx, """
                        INSERT INTO occurrences(component_id,sort_key,occurrence_key,payload) VALUES($id,$sort,$key,$payload) ON CONFLICT DO NOTHING;
                        """, ct, ("$id", component.Id), ("$sort", sort.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)),
                        ("$key", occurrence.OccurrenceId), ("$payload", JsonSerializer.Serialize(occurrence with { Title = null, Description = null, Location = null })));
                }
            }
            lastUid = uids[^1];
        }
    }

    private static async Task<StoredEvent> ReadStoredEventAsync(SqliteConnection c, SqliteTransaction tx, long id,
        bool calendar, CalendarOccurrence? occurrence, CancellationToken ct)
    {
        var sql = calendar ? """
            SELECT c.source_file,c.record_index,c.importer_version,i.started_at,c.definition_json
            FROM calendar_components c JOIN import_operations i ON i.id=c.latest_import_id WHERE c.id=$id;
            """ : """
            SELECT e.ended_at,e.title,e.artist,e.album,e.duration_ms,e.kind,e.uri,e.metadata_json,e.provider,
              e.source_file,e.record_index,e.importer_version,i.started_at,e.stable_fingerprint,e.stable_occurrence FROM events e JOIN import_operations i ON i.id=e.first_import_id WHERE e.id=$id;
            """;
        using var cmd = Command(c, tx, sql, ("$id", id)); await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidDataException("Timeline record disappeared during the query.");
        if (calendar)
        {
            var def = JsonSerializer.Deserialize<CalendarDefinition>(r.GetString(4))!;
            var item = occurrence! with { Title = def.Title, Description = def.Description, Location = def.Location };
            return new(id, new(item.EndInstant, item.Title, null, null, null, "Calendar", null,
                JsonSerializer.Serialize(new { def.Uid, def.RecurrenceId, def.Sequence, def.ModifiedAt, def.RawIcs })), "Google Calendar",
                r.GetString(0), r.GetInt32(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture), item,
                "calendar:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new[] { def.Uid, item.OccurrenceId })))));
        }
        return new(id, new(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), Text(r, 1), Text(r, 2), Text(r, 3),
            r.GetInt64(4), r.GetString(5), Text(r, 6), r.GetString(7)), r.GetString(8), r.GetString(9), r.GetInt32(10),
            r.GetString(11), DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture), StableId:
                r.GetString(8) + ":" + r.GetString(13) + ":" + r.GetInt32(14).ToString(CultureInfo.InvariantCulture));
    }
}
