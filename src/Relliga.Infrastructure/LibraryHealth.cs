using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Relliga.Application;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore : ILibraryHealth
{
    public Task<HealthReport> VerifyAsync(CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct);
        return await VerifyConnectionAsync(c, ct);
    }, ct);

    private static async Task ValidateSchemaAsync(SqliteConnection c, int version, CancellationToken ct)
    {
        if (version is < 1 or > CurrentSchemaVersion) throw new InvalidDataException($"Unsupported library schema {version}. Supported backup schemas: 1–{CurrentSchemaVersion}.");
        await using var expected = new SqliteConnection("Data Source=:memory:"); await expected.OpenAsync(ct);
        await ExecuteAsync(expected, null, SchemaV1, ct);
        if (version >= 2) { using var tx = expected.BeginTransaction(); await MigrateV2Async(expected, tx, ct); if (version >= 3) await ExecuteAsync(expected, tx, SchemaV3, ct); if (version >= 4) await ExecuteAsync(expected, tx, SchemaV4, ct); tx.Commit(); }
        static async Task<List<string>> ReadSchema(SqliteConnection connection, CancellationToken token)
        {
            var result = new List<string>();
            using var cmd = Command(connection, null, "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;");
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) result.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2) + ":" +
                Regex.Replace(Text(reader, 3) ?? "", @"\s+", "").ToLowerInvariant());
            return result;
        }
        if (!(await ReadSchema(c, ct)).SequenceEqual(await ReadSchema(expected, ct)))
            throw new InvalidDataException("The database schema differs from Relliga's supported schema. Unknown tables, views, indexes or triggers cannot be activated.");
    }

    private static async Task<HealthReport> VerifyConnectionAsync(SqliteConnection c, CancellationToken ct)
    {
        var problems = new List<string>();
        var version = Convert.ToInt32(await ScalarAsync(c, null, "PRAGMA user_version;", ct));
        try { await ValidateSchemaAsync(c, version, ct); }
        catch (InvalidDataException ex) { return new([ex.Message]); }
        if (version != CurrentSchemaVersion) return new([$"This library requires migration to schema {CurrentSchemaVersion} before use."]);
        using (var cmd = Command(c, null, "PRAGMA integrity_check;"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) if (r.GetString(0) != "ok") problems.Add("SQLite: " + r.GetString(0));
        using (var cmd = Command(c, null, "PRAGMA foreign_key_check;"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) problems.Add("Broken foreign keys. Preserve a backup and recover from a known healthy library.");
        var invalid = Convert.ToInt64(await ScalarAsync(c, null, """
            SELECT (SELECT COUNT(*) FROM events e WHERE NOT EXISTS
              (SELECT 1 FROM event_origins o WHERE o.event_id=e.id AND o.operation_id=e.first_import_id)
              OR stable_fingerprint IS NULL OR stable_occurrence IS NULL OR NOT json_valid(metadata_json)) +
              (SELECT COUNT(*) FROM calendar_components c WHERE NOT json_valid(definition_json) OR NOT EXISTS
              (SELECT 1 FROM calendar_origins o WHERE o.component_id=c.id AND o.operation_id=c.first_import_id) OR NOT EXISTS
              (SELECT 1 FROM calendar_origins o WHERE o.component_id=c.id AND o.operation_id=c.latest_import_id AND o.definition_json=c.definition_json)) +
              (SELECT COUNT(*) FROM event_origins WHERE metadata_json IS NOT NULL AND NOT json_valid(metadata_json)) +
              (SELECT COUNT(*) FROM calendar_origins WHERE NOT json_valid(definition_json));
            """, ct));
        if (invalid > 0) problems.Add($"{invalid:N0} invalid event/provenance relationships or JSON records. Keep the library for recovery; no repairs were attempted.");
        var invalidMessages = Convert.ToInt64(await ScalarAsync(c, null, """
            SELECT (SELECT COUNT(*) FROM telegram_messages m JOIN events e ON e.id=m.event_id WHERE e.provider<>'Telegram' OR e.kind<>'Message' OR NOT json_valid(m.message_json)
              OR m.message_json<>e.metadata_json OR EXISTS(SELECT 1 FROM telegram_deletions d WHERE d.account_id=m.account_id AND d.chat_id=m.chat_id AND d.message_id=m.message_id))
            + (SELECT COUNT(*) FROM events e WHERE e.provider='Telegram' AND NOT EXISTS(SELECT 1 FROM telegram_messages m WHERE m.event_id=e.id));
            """, ct));
        if (invalidMessages > 0) problems.Add("Conversation identity, content or deletion consistency failed.");
        foreach (var table in new[] { "events_fts", "calendar_fts" })
        {
            using var tx = c.BeginTransaction();
            try { await ExecuteAsync(c, tx, $"INSERT INTO {table}({table},rank) VALUES('integrity-check',1);", ct); }
            catch (SqliteException ex) { problems.Add($"{table}: search index verification failed ({ex.SqliteErrorCode}). An index rebuild from preserved content may be needed; verification did not rebuild it."); }
            finally { tx.Rollback(); }
        }
        return new(problems);
    }
}
