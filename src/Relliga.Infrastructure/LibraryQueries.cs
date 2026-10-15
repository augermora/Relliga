using System.Globalization;
using Microsoft.Data.Sqlite;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private async Task<LibrarySummary> GetSummaryCoreAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        var sources = new List<SourceStatistics>();
        using (var cmd = Command(c, null, "SELECT provider,COUNT(*),SUM(duration_ms) FROM events GROUP BY provider;"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) sources.Add(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        var calendarCount = Convert.ToInt64(await ScalarAsync(c, null, """
            SELECT COUNT(DISTINCT uid) FROM calendar_components c WHERE NOT EXISTS
              (SELECT 1 FROM calendar_components m WHERE m.uid=c.uid AND m.recurrence_id='' AND m.is_cancelled=1) AND c.is_cancelled=0;
            """, ct));
        var calendarExists = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM data_sources WHERE provider='Google Calendar';", ct)) > 0;
        if (calendarExists)
        {
            var recurring = Convert.ToInt64(await ScalarAsync(c, null,
                "SELECT COUNT(DISTINCT uid) FROM calendar_components WHERE is_recurring=1 AND is_cancelled=0;", ct));
            sources.Add(new("Google Calendar", calendarCount, RecurringDefinitions: recurring));
        }
        var imports = await GetImportsAsync(ct, pageSize: 1);
        var successful = Convert.ToInt32(await ScalarAsync(c, null,
            "SELECT COUNT(*) FROM import_operations WHERE status IN ('Completed','Completed with warnings','Partially completed');", ct));
        var total = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM import_operations;", ct));
        return new(sources.Sum(x => x.Events), sources.Count, imports.FirstOrDefault(), successful, sources, total);
    }

    private async Task<IReadOnlyList<ImportOperation>> GetImportsCoreAsync(CancellationToken ct, int page, int pageSize)
    {
        if (page < 0 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, null, """
            SELECT id,file_name,status,started_at,added,duplicates,warnings,message,provider,importer_version,processed,rejected,updated
            FROM import_operations ORDER BY id DESC LIMIT $limit OFFSET $offset;
            """, ("$limit", pageSize), ("$offset", (long)page * pageSize));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ImportOperation>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), Text(reader, 7), reader.GetString(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.GetInt32(12)));
        return result;
    }
}
