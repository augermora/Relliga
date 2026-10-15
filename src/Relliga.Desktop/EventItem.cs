using System.Globalization;
using System.Text.Json;
using Relliga.Domain;

namespace Relliga.Desktop;

public sealed record EventItem(StoredEvent Record)
{
    public string Title => Record.Event.Title ?? "Title not provided";
    public string Artist => Record.Event.Artist ?? "Creator not provided";
    public string DateLabel => Record.Event.EndedAt.UtcDateTime.ToString("dd MMM yyyy · HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    public string PlaybackLabel => $"{Record.Event.DurationMs / 3_600_000:00}:{Record.Event.DurationMs / 60_000 % 60:00}:{Record.Event.DurationMs / 1000 % 60:00} played";
    public string Details
    {
        get
        {
            var ev = Record.Event;
            using var metadata = JsonDocument.Parse(ev.MetadataJson);
            var formatted = JsonSerializer.Serialize(metadata.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return $"{Title}\n{Artist}\n\nEnded: {DateLabel}\nPlayed: {ev.DurationMs:N0} ms\nType: {ev.Kind}\nAlbum: {ev.Album ?? "Not provided"}\nURI: {ev.Uri ?? "Not provided"}\n\nSource: {Record.Provider}\nFile: {Record.SourceFile}\nRecord: {Record.RecordIndex}\nImporter: {Record.ImporterVersion}\nFirst imported: {Record.ImportedAt:u}\n\nExport metadata\n{formatted}";
        }
    }
    public override string ToString() => $"{Title}, {Artist}, {DateLabel}, {PlaybackLabel}";
}
