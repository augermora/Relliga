using System.Globalization;
using System.Text.Json;
using Relliga.Domain;
using Avalonia.Media;

namespace Relliga.Desktop;

public sealed record EventItem(StoredEvent Record)
{
    public string Title => Record.Event.Title ?? "Title not provided";
    public bool IsCalendar => Record.Calendar is not null;
    private static readonly IBrush CalendarAccent = new SolidColorBrush(Color.Parse("#C5B6FF"));
    private static readonly IBrush PlaybackAccent = new SolidColorBrush(Color.Parse("#9ADBC4"));
    public IBrush AccentBrush => IsCalendar ? CalendarAccent : PlaybackAccent;
    public string SourceLabel => $"{(IsCalendar ? "▣" : "♫")}  {Record.Provider} · {Record.Event.Kind}";
    public string Artist => IsCalendar ? Record.Calendar!.Location ?? "Location not provided" : Record.Event.Artist ?? "Creator not provided";
    public string DateLabel => Record.Calendar is { } cal
        ? cal.IsAllDay ? cal.StartLocal.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) + " · All day"
            : cal.IsFloating ? cal.StartLocal.ToString("dd MMM yyyy · HH:mm", CultureInfo.InvariantCulture) + " · Floating local time"
            : cal.StartInstant!.Value.UtcDateTime.ToString("dd MMM yyyy · HH:mm 'UTC'", CultureInfo.InvariantCulture)
        : Record.Event.EndedAt!.Value.UtcDateTime.ToString("dd MMM yyyy · HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    public string PlaybackLabel => Record.Calendar is { } cal
        ? cal.IsAllDay ? "Date-only · End date is exclusive" : cal.IsFloating ? "No timezone assigned in export" : $"Original timezone: {cal.TimeZoneId}"
        : $"{Record.Event.DurationMs / 3_600_000:00}:{Record.Event.DurationMs / 60_000 % 60:00}:{Record.Event.DurationMs / 1000 % 60:00} played";
    public string Details
    {
        get
        {
            var ev = Record.Event;
            using var metadata = JsonDocument.Parse(ev.MetadataJson);
            var formatted = JsonSerializer.Serialize(metadata.RootElement, new JsonSerializerOptions { WriteIndented = true });
            if (Record.Calendar is { } cal)
                return $"{Title}\nGoogle Calendar\n\n{DateLabel}\nLocal start: {cal.StartLocal.ToString(cal.IsAllDay ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\nOccurrence end: {(cal.EndLocal is { } end ? end.ToString(cal.IsAllDay ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "Not provided")}\n" +
                    $"Time semantics: {(cal.IsAllDay ? "All day (exclusive end date)" : cal.IsFloating ? "Floating wall-clock time; no UTC instant assigned" : cal.TimeZoneId)}\n" +
                    $"Location: {cal.Location ?? "Not provided"}\n\n{cal.Description ?? "Description not provided"}\n\nUID: {cal.Uid}\nOccurrence: {cal.OccurrenceId}\n" +
                    $"Override: {(cal.ComponentRecurrenceId.Length > 0 ? "Yes" : "No")}\nFile: {Record.SourceFile}\nRecord: {Record.RecordIndex}\nImporter: {Record.ImporterVersion}\nRevision imported: {Record.ImportedAt:u}\n\nPreserved export metadata\n{formatted}";
            return $"{Title}\n{Artist}\n\nEnded: {DateLabel}\nPlayed: {ev.DurationMs:N0} ms\nType: {ev.Kind}\nAlbum: {ev.Album ?? "Not provided"}\nURI: {ev.Uri ?? "Not provided"}\n\nSource: {Record.Provider}\nFile: {Record.SourceFile}\nRecord: {Record.RecordIndex}\nImporter: {Record.ImporterVersion}\nFirst imported: {Record.ImportedAt:u}\n\nExport metadata\n{formatted}";
        }
    }
    public override string ToString() => $"{Title}, {Artist}, {DateLabel}, {PlaybackLabel}";
}
