using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Importers;

// One content router supports standalone files and mixed archives without temporary extraction.
public sealed class ExportImporter : IDataImporter, IImportInspector
{
    public string Provider => "Automatic";
    public string Version => "3.0.0";
    private readonly ImportLimits limits;
    private readonly SpotifyImporter spotify;
    private readonly GoogleCalendarImporter calendar;
    private readonly TelegramJsonImporter telegram;
    public ExportImporter(ImportLimits? limits = null)
    { this.limits = limits ?? new(); spotify = new(this.limits); calendar = new(); telegram = new(this.limits); }

    public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int valid = 0, processed = 0;
        var problems = new List<string>();
        await foreach (var member in ArchiveInputs.OpenAsync(path, limits, ct))
        {
            var prefix = new byte[4096];
            int count = 0, n;
            while (count < prefix.Length && (n = await member.Stream.ReadAsync(prefix.AsMemory(count), ct)) > 0) count += n;
            var lead = Encoding.UTF8.GetString(prefix, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            await using var replay = new PrefixReadStream(prefix, count, member.Stream);
            string source;
            IAsyncEnumerable<ImportRecord> records;
            if (lead.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            { source = "Google Calendar"; records = calendar.ReadCalendarAsync(replay, member.Name, ct); }
            else if (lead.StartsWith('{'))
            { source = "Telegram"; records = telegram.ReadJsonAsync(replay, member.Name, ct); }
            else if (lead.StartsWith('['))
            { source = "Spotify"; records = spotify.ReadJsonAsync(replay, member.Name, ct); }
            else
            {
                if (!member.InArchive) throw new InvalidDataException("Unsupported or empty input. Choose Spotify history, Telegram Desktop JSON, Calendar ICS, or a compatible ZIP.");
                yield return new(null, member.Name, 0, Warning: $"{member.Name}: unsupported archive member skipped.", Rejected: false);
                continue;
            }
            await using var iterator = records.GetAsyncEnumerator(ct);
            while (true)
            {
                bool more; string? failure = null;
                try { more = await iterator.MoveNextAsync(); }
                catch (Exception ex) when (member.InArchive && ex is JsonException or CalendarFormatException or TelegramFormatException)
                { more = false; failure = $"{member.Name}: {ex.Message}"; }
                if (failure is not null)
                {
                    if (problems.Count < 10) problems.Add(failure);
                    yield return new(null, member.Name, 0, Warning: failure, Provider: source,
                        ImporterVersion: SourceVersion(source), Rejected: false, IsFileError: true);
                }
                if (!more) break;
                if (++processed > limits.MaxRecords) throw new InvalidDataException("Export exceeds the record limit.");
                var record = iterator.Current with { Provider = source, ImporterVersion = SourceVersion(source) };
                if (record.Warning is not null && problems.Count < 10) problems.Add(record.Warning);
                if (record.Event is not null || record.Calendar is not null || record.Message is not null) valid++;
                if (processed % 100 == 0) progress?.Report(new(processed, $"Processing export · {processed:N0} records"));
                yield return record;
            }
        }
        if (valid == 0) throw new InvalidDataException("No valid supported records were found. Choose Spotify Extended Streaming History, Telegram Desktop JSON or a valid Calendar export." +
            (problems.Count == 0 ? "" : "\n\n" + string.Join('\n', problems)));
    }

    public async Task<ImportPreview> InspectAsync(string path, CancellationToken ct)
    {
        var sources = new SortedSet<string>();
        var warnings = new List<string>();
        int records = 0, recurring = 0, allDay = 0;
        DateTime? suggestedDate = null;
        await foreach (var record in ReadAsync(path, null, ct))
        {
            if (record.Event is not null || record.Calendar is not null || record.Message is not null)
            {
                records++; sources.Add(record.Provider!);
                if (record.Calendar?.IsRecurring == true) recurring++;
                if (record.Calendar?.IsAllDay == true) allDay++;
                if (record.Calendar is { IsCancelled: false } cal) suggestedDate ??= cal.StartLocal;
            }
            if (record.Warning is not null && warnings.Count < 10) warnings.Add(record.Warning);
            if (records >= 1000) break;
        }
        var source = string.Join(" + ", sources);
        return new(source, $"Detected: {source}\n{records:N0}{(records == 1000 ? "+" : "")} valid records sampled · {recurring:N0} recurring definitions · {allDay:N0} all-day definitions\n" +
            "Recurrences are calculated only when browsing a bounded date window. Full validation runs during import." +
            (warnings.Count == 0 ? "" : "\n\nWarnings\n" + string.Join('\n', warnings)), suggestedDate);
    }
    private string SourceVersion(string source) => source == "Spotify" ? spotify.Version : source == "Telegram" ? telegram.Version : calendar.Version;
}
