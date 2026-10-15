using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore : IDataExportService
{
    private static readonly JsonSerializerOptions ExportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Activity.RunAsync(async () =>
        {
            EnsureExternalDestination(request.Destination);
            using var output = new AtomicOutput(request.Destination, request.Overwrite);
            long count = 0;
            await using (var stream = new FileStream(output.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                if (request.Format == ExportFormat.Json)
                {
                    using var writer = new Utf8JsonWriter(stream);
                    writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("applicationVersion", AppInfo.Version);
                    await StreamAsync(request.Query, async (row, connection, transaction) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.WriteStartObject(); writer.WriteString("id", row.StableId);
                        writer.WritePropertyName("record"); JsonSerializer.Serialize(writer, row, ExportJson);
                        writer.WritePropertyName("metadata"); using (var metadata = JsonDocument.Parse(row.Event.MetadataJson)) metadata.RootElement.WriteTo(writer);
                        writer.WriteStartArray("origins");
                        await WriteOriginsAsync(writer, row, connection, transaction, ct);
                        writer.WriteEndArray(); writer.WriteEndObject();
                        await writer.FlushAsync(ct);
                        if (++count % 100 == 0) progress?.Report(new(count, $"Exported {count:N0} records…"));
                    }, ct, effective =>
                    {
                        writer.WritePropertyName("query"); JsonSerializer.Serialize(writer, effective, ExportJson);
                        writer.WriteString("calendarWindowPolicy", "Occurrences only; effective bounds above include Timeline's 90-day default when applicable. Maximum Calendar range: 366 days. Page/pageSize do not limit exports.");
                        writer.WriteStartArray("events"); return Task.CompletedTask;
                    });
                    writer.WriteEndArray(); writer.WriteNumber("recordCount", count); writer.WriteEndObject(); await writer.FlushAsync(ct);
                }
                else if (request.Format == ExportFormat.Csv)
                {
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
                    await writer.WriteLineAsync("id,source,type,title,artist,album,ended_at_utc,duration_ms,uri,start_local,end_local,time_zone,all_day,floating,uid,recurrence_id,occurrence_id,source_file,record_index,importer_version,imported_at_utc,metadata_json,calendar_json");
                    await StreamAsync(request.Query, async (row, _, _) =>
                    {
                        ct.ThrowIfCancellationRequested(); var e = row.Event; var cal = row.Calendar;
                        string?[] fields = [row.StableId, row.Provider, e.Kind, e.Title, e.Artist, e.Album,
                            e.EndedAt is { } end ? Stamp(end) : null, e.DurationMs?.ToString(CultureInfo.InvariantCulture), e.Uri,
                            cal?.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                            cal?.EndLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), cal?.TimeZoneId,
                            cal?.IsAllDay.ToString().ToLowerInvariant(), cal?.IsFloating.ToString().ToLowerInvariant(), cal?.Uid,
                            cal?.ComponentRecurrenceId, cal?.OccurrenceId, row.SourceFile, row.RecordIndex.ToString(CultureInfo.InvariantCulture),
                            row.ImporterVersion, Stamp(row.ImportedAt), e.MetadataJson, cal is null ? null : JsonSerializer.Serialize(cal, ExportJson)];
                        await writer.WriteLineAsync(string.Join(',', fields.Select(Csv)).AsMemory(), ct);
                        if (++count % 100 == 0) progress?.Report(new(count, $"Exported {count:N0} records…"));
                    }, ct);
                    await writer.FlushAsync(ct);
                }
                else throw new ArgumentOutOfRangeException(nameof(request));
            }
            output.Publish(ct);
            return new ExportResult(count, output.Destination);
        }, ct);

    private static string Csv(string? value)
    {
        value ??= "";
        // Also protect leading control characters/whitespace before a formula marker.
        var trimmed = value.TrimStart();
        if (value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n') ||
            trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static async Task WriteOriginsAsync(Utf8JsonWriter writer, StoredEvent row, SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        var calendar = row.Calendar is not null;
        using var cmd = Command(c, tx, calendar ? """
            SELECT o.operation_id,o.source_file,o.record_index,i.started_at,i.importer_version,o.definition_json,o.disposition
            FROM calendar_origins o JOIN import_operations i ON i.id=o.operation_id WHERE o.component_id=$id ORDER BY o.operation_id,o.source_file,o.record_index;
            """ : """
            SELECT o.operation_id,o.source_file,o.record_index,i.started_at,i.importer_version,o.metadata_json,NULL
            FROM event_origins o JOIN import_operations i ON i.id=o.operation_id WHERE o.event_id=$id ORDER BY o.operation_id,o.source_file,o.record_index;
            """, ("$id", row.Id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            writer.WriteStartObject(); writer.WriteNumber("importId", r.GetInt64(0)); writer.WriteString("sourceFile", r.GetString(1));
            writer.WriteNumber("recordIndex", r.GetInt32(2)); writer.WriteString("importedAt", r.GetString(3));
            writer.WriteString("importerVersion", r.GetString(4)); writer.WriteString("disposition", Text(r, 6));
            writer.WritePropertyName(calendar ? "definition" : "metadata");
            if (r.IsDBNull(5)) writer.WriteNullValue();
            else { using var json = JsonDocument.Parse(r.GetString(5)); json.RootElement.WriteTo(writer); }
            writer.WriteEndObject(); await writer.FlushAsync(ct);
        }
    }

    private void EnsureExternalDestination(string path)
    {
        var destination = Path.GetFullPath(path);
        var resolvedDestination = Path.Combine(ResolveDirectory(Path.GetDirectoryName(destination)!), Path.GetFileName(destination));
        var directory = ResolveDirectory(Path.GetDirectoryName(DatabasePath)!) + Path.DirectorySeparatorChar;
        if (resolvedDestination.StartsWith(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Choose a destination outside the managed library directory to protect the active database and recovery files.");
    }

    private static string ResolveDirectory(string path, int depth = 0)
    {
        if (depth > 64) throw new IOException("The destination directory contains too many levels or linked paths.");
        var directory = new DirectoryInfo(path);
        if (directory.Parent is null) return directory.FullName;
        var resolved = new DirectoryInfo(Path.Combine(ResolveDirectory(directory.Parent.FullName, depth + 1), directory.Name));
        if (resolved.Exists && (resolved.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = resolved.ResolveLinkTarget(true) as DirectoryInfo
                ?? throw new IOException("Cannot resolve the destination directory safely.");
            return ResolveDirectory(target.FullName, depth + 1);
        }
        return resolved.FullName;
    }
}
