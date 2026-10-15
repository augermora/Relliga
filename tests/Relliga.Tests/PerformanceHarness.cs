using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;

namespace Relliga.Tests;

/// <summary>Explicit opt-in executable harness, never part of the normal test run. All input is deterministic synthetic data.</summary>
internal static class PerformanceHarness
{
    internal sealed record Measurement(string Operation, double Milliseconds, long AllocatedBytes, long WorkingSetBytes, long PeakWorkingSetBytes);
    private sealed record DatasetResult(int PlaybackRecords, int CalendarDefinitions, long DatabaseBytes, IReadOnlyList<Measurement> Measurements, object QueryPlans);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        var sizes = Option("--sizes", "10000,100000").Split(',').Select(int.Parse).ToArray();
        if (sizes.Length > 3 || sizes.Any(n => n is < 100 or > 1_000_000)) throw new ArgumentException("Benchmark sizes must be 100–1,000,000, at most three datasets.");
        var label = Option("--label", "measurement");
        if (!System.Text.RegularExpressions.Regex.IsMatch(label, "^[a-zA-Z0-9-]+$")) throw new ArgumentException("Use a simple benchmark label.");
        var output = Path.GetFullPath(Option("--output", Path.Combine(AppContext.BaseDirectory, "benchmark-results")));
        Directory.CreateDirectory(output);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var results = new List<DatasetResult>();
        var reportPath = Path.Combine(output, label + ".json");
        foreach (var size in sizes)
        {
            var root = Path.Combine(Path.GetTempPath(), "Relliga.Benchmark", label + "-" + size + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); // Explicitly retained for investigation; never the user's library.
            Console.WriteLine($"[{label}] {size:N0} plays · isolated directory: {root}");
            results.Add(await MeasureDatasetAsync(size, root, args.Contains("--skip-large-artifacts") && size >= 1_000_000, cancellation.Token));
            var report = new
            {
                label, applicationVersion = AppInfo.Version, createdAtUtc = DateTimeOffset.UtcNow,
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount, serverGc = GCSettings.IsServerGC,
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, configuration = "Release", seed = "relliga-synthetic-v1",
                queryRepetitions = 5, notes = "Wall-clock measurements, warm caches after import. Timings include the complete store operation. Allocations are process-wide managed allocations, not retained heap. Peak working set is cumulative process peak. Large-artifact skipping is explicit.", results
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, Json), cancellation.Token);
        }
        Console.WriteLine("Report: " + reportPath); return 0;
    }

    private static async Task<DatasetResult> MeasureDatasetAsync(int size, string root, bool skipArtifacts, CancellationToken ct)
    {
        var measurements = new List<Measurement>();
        async Task Measure(string name, Func<Task> action, int repetitions = 1)
        {
            var elapsed = new List<double>(); long allocated = 0;
            for (int i = 0; i < repetitions; i++)
            {
                ct.ThrowIfCancellationRequested(); var before = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                await action(); watch.Stop(); allocated += GC.GetTotalAllocatedBytes(true) - before; elapsed.Add(watch.Elapsed.TotalMilliseconds);
            }
            elapsed.Sort(); using var process = Process.GetCurrentProcess(); process.Refresh();
            var result = new Measurement(name, elapsed[elapsed.Count / 2], allocated / repetitions, process.WorkingSet64, process.PeakWorkingSet64);
            measurements.Add(result); Console.WriteLine($"  {name}: {result.Milliseconds:N1} ms · allocated {result.AllocatedBytes / 1048576d:N1} MiB · working set {result.WorkingSetBytes / 1048576d:N1} MiB");
        }
        var inputs = Path.Combine(root, "inputs"); Directory.CreateDirectory(inputs);
        var paths = new List<string>();
        await Measure("generate-spotify-fixtures", async () =>
        {
            for (int start = 0; start < size; start += 100_000)
            {
                var path = Path.Combine(inputs, $"synthetic-{start}.json"); paths.Add(path);
                await using var stream = File.Create(path); using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartArray();
                for (int i = start; i < Math.Min(size, start + 100_000); i++)
                {
                    ct.ThrowIfCancellationRequested(); writer.WriteStartObject();
                    writer.WriteString("ts", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i * 30L).ToString("O"));
                    writer.WriteNumber("ms_played", 30_000 + i % 240_000);
                    writer.WriteString("master_metadata_track_name", (i % 1000 == 0 ? "Quasar " : "Synthetic track ") + i % 500);
                    writer.WriteString("master_metadata_album_artist_name", "Synthetic artist " + i % 60);
                    writer.WriteString("master_metadata_album_album_name", "Synthetic album " + i % 80);
                    writer.WriteString("spotify_track_uri", "spotify:track:synthetic-" + i % 500);
                    writer.WriteString("username", "synthetic-account"); writer.WriteString("platform", i % 4 == 0 ? "synthetic-mobile" : "synthetic-desktop");
                    writer.WriteEndObject(); if (i % 500 == 0) await writer.FlushAsync(ct);
                }
                writer.WriteEndArray(); await writer.FlushAsync(ct);
            }
        });
        var calendarCount = Math.Min(1000, Math.Max(10, size / 100));
        var calendarPath = Path.Combine(inputs, "synthetic-calendar.ics");
        var calendar = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Relliga//Synthetic Benchmark//EN\r\n");
        for (int i = 0; i < calendarCount; i++)
        {
            calendar.Append($"BEGIN:VEVENT\r\nUID:benchmark-{i}\r\nSUMMARY:Synthetic planning {i}\r\nDESCRIPTION:Deterministic synthetic benchmark only\r\nLOCATION:Studio {i % 20}\r\n");
            var day = 1 + i % 28;
            if (i % 10 == 0) calendar.Append($"DTSTART;VALUE=DATE:202501{day:00}\r\nDTEND;VALUE=DATE:202501{day + 1:00}\r\n");
            else if (i % 5 == 0) calendar.Append($"DTSTART:202501{day:00}T090000\r\nDTEND:202501{day:00}T100000\r\n");
            else calendar.Append($"DTSTART;TZID=Europe/Madrid:202501{day:00}T090000\r\nDTEND;TZID=Europe/Madrid:202501{day:00}T100000\r\n");
            if (i % 4 == 0) calendar.Append("RRULE:FREQ=WEEKLY;COUNT=52\r\n");
            calendar.Append("END:VEVENT\r\n");
        }
        calendar.Append("END:VCALENDAR\r\n"); await File.WriteAllTextAsync(calendarPath, calendar.ToString(), new UTF8Encoding(false), ct);
        var database = Path.Combine(root, "library", "relliga.db");
        using var store = new SqliteHistoryStore(database, new CalendarExpander()); await store.InitializeAsync(ct);
        var importer = new ExportImporter(); var imports = new ImportService(store, [importer]);
        await Measure("import-spotify", async () =>
        {
            var processed = 0;
            foreach (var path in paths) { processed += (await imports.ImportAsync(path, "Automatic", ct: ct)).Processed; Console.WriteLine($"    Imported {processed:N0}/{size:N0} synthetic plays"); }
        });
        await Measure("import-calendar", async () => { await imports.ImportAsync(calendarPath, "Automatic", ct: ct); });
        var spotify = new EventQuery(Provider: "Spotify");
        await Measure("timeline-first", async () => { _ = await store.QueryAsync(spotify, ct); }, 5);
        await Measure("timeline-middle", async () => { _ = await store.QueryAsync(spotify with { Page = size / 100 }, ct); }, 5);
        await Measure("timeline-last", async () => { _ = await store.QueryAsync(spotify with { Page = size / 50 - 1 }, ct); }, 5);
        await Measure("search-rare", async () => { _ = await store.QueryAsync(spotify with { Text = "Quasar" }, ct); }, 5);
        await Measure("search-common", async () => { _ = await store.QueryAsync(spotify with { Text = "Synthetic" }, ct); }, 5);
        var january = new EventQuery(From: new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Until: new(2025, 2, 1, 0, 0, 0, TimeSpan.Zero));
        await Measure("date-filter", async () => { _ = await store.QueryAsync(january with { Provider = "Spotify" }, ct); }, 5);
        await Measure("calendar-window", async () => { _ = await store.QueryAsync(january with { Provider = "Google Calendar" }, ct); }, 5);
        await Measure("mixed-window", async () => { _ = await store.QueryAsync(january, ct); }, 5);
        await Measure("summary", async () => { _ = await store.GetSummaryAsync(ct); }, 5);
        object plans;
        await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await c.OpenAsync(ct); var entries = new List<object>();
            var filter = SqliteHistoryStore.PlaybackFilter(new(Provider: "Spotify", From: january.From), null);
            foreach (var sql in new[]
            {
                "SELECT id FROM events e " + filter.Sql + " ORDER BY ended_at DESC,id DESC LIMIT 50;",
                "SELECT operation_id FROM event_origins WHERE event_id=1;",
                "SELECT component_id FROM calendar_origins WHERE component_id=1;",
                "SELECT rowid FROM events_fts WHERE events_fts MATCH 'Quasar*';"
            })
            {
                using var cmd = c.CreateCommand(); cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                if (sql.Contains("$from")) { cmd.Parameters.AddWithValue("$from", "2025-01-01T00:00:00.0000000Z"); cmd.Parameters.AddWithValue("$provider", "Spotify"); }
                var details = new List<string>(); await using var reader = await cmd.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) details.Add(reader.GetString(3));
                entries.Add(new { sql, details });
            }
            plans = entries;
        }
        if (!skipArtifacts)
        {
            foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Json })
                await Measure("export-" + format.ToString().ToLowerInvariant(), async () =>
                { await store.ExportAsync(new(Path.Combine(root, "events." + format.ToString().ToLowerInvariant()), format, january), ct: ct); });
            var archive = Path.Combine(root, "backup.zip");
            await Measure("backup-verified", () => store.CreateAsync(archive, ct: ct));
            var preview = await store.InspectAsync(archive, ct);
            await Measure("restore-verified", () => store.RestoreAsync(preview, ct: ct));
        }
        else Console.WriteLine("  Full exports/backup/restore explicitly skipped for the 1M dataset; measured at smaller sizes.");
        return new(size, calendarCount, new FileInfo(database).Length, measurements, plans);
    }
}
