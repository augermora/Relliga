using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Importers;

public sealed record ImportLimits(long MaxJsonBytes = 64 * 1024 * 1024,
    long MaxArchiveBytes = 256 * 1024 * 1024, long MaxExpandedBytes = 256 * 1024 * 1024,
    int MaxEntries = 256, int MaxCompressionRatio = 200, int MaxRecords = 1_000_000);

public sealed class SpotifyImporter(ImportLimits? limits = null) : IDataImporter
{
    private readonly ImportLimits limits = limits ?? new();
    public string Provider => "Spotify";
    public string Version => "2.0.0";

    internal IAsyncEnumerable<ImportRecord> ReadJsonAsync(Stream stream, string name, CancellationToken ct) => ParseAsync(stream, name, ct);

    public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int records = 0, supported = 0;
        long actualExpanded = 0;
        void CountExpanded(int bytes)
        {
            actualExpanded += bytes;
            if (actualExpanded > limits.MaxExpandedBytes)
                throw new InvalidDataException("ZIP exceeds the actual expanded byte limit.");
        }
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (input.Length > limits.MaxArchiveBytes) throw new InvalidDataException("ZIP exceeds the archive size limit.");
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            ValidateArchive(zip);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                await using var stream = entry.Open();
                await using var iterator = ParseAsync(stream, entry.FullName, cancellationToken, CountExpanded).GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool hasNext;
                    string? parseError = null;
                    try { hasNext = await iterator.MoveNextAsync(); }
                    catch (JsonException ex) { hasNext = false; parseError = $"{entry.FullName}: invalid or unsupported JSON member. {ex.Message}"; }
                    if (parseError is not null) yield return new(null, entry.FullName, 0, Warning: parseError);
                    if (!hasNext) break;
                    var record = iterator.Current;
                    if (++records > limits.MaxRecords) throw new InvalidDataException("Export exceeds the record limit.");
                    if (record.Event is not null) supported++;
                    if (records % 250 == 0) progress?.Report(new(records, $"Reading Spotify export · {records:N0} records"));
                    yield return record;
                }
            }
        }
        else
        {
            if (input.Length > limits.MaxJsonBytes) throw new InvalidDataException("JSON exceeds the 64 MiB input limit.");
            await foreach (var record in ParseAsync(input, Path.GetFileName(path), cancellationToken))
            {
                if (++records > limits.MaxRecords) throw new InvalidDataException("Export exceeds the record limit.");
                if (record.Event is not null) supported++;
                if (records % 250 == 0) progress?.Report(new(records, $"Reading Spotify export · {records:N0} records"));
                yield return record;
            }
        }
        if (supported == 0) throw new InvalidDataException(records == 0
            ? "The input contains no listening records. Choose a Spotify Extended Streaming History export."
            : "No valid Spotify Extended Streaming History records were found. Standard account history is not supported.");
        progress?.Report(new(records, $"Processed {records:N0} records"));
    }

    private void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > limits.MaxEntries) throw new InvalidDataException("ZIP contains too many entries (maximum 256).");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains("..") || name.Length > 1024 || !names.Add(name))
                throw new InvalidDataException("ZIP contains an unsafe entry path.");
            expanded = checked(expanded + e.Length);
            if (expanded > limits.MaxExpandedBytes || e.Length > limits.MaxJsonBytes ||
                (e.Length > 0 && (e.CompressedLength == 0 || (double)e.Length / e.CompressedLength > limits.MaxCompressionRatio)))
                throw new InvalidDataException("ZIP exceeds safe expanded size or compression ratio limits.");
        }
    }

    private async IAsyncEnumerable<ImportRecord> ParseAsync(Stream stream, string name,
        [EnumeratorCancellation] CancellationToken ct, Action<int>? countExpanded = null)
    {
        // The bounded wrapper checks actual decompressed bytes, independently of ZIP headers.
        await using var bounded = new BoundedReadStream(stream, limits.MaxJsonBytes, countExpanded);
        var options = new JsonSerializerOptions { MaxDepth = 32, DefaultBufferSize = 16384 };
        int index = 0;
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(bounded, options, ct))
        {
            ct.ThrowIfCancellationRequested();
            index++;
            ImportRecord result;
            try { result = Normalize(row, name, index); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            { result = new(null, name, index, Warning: $"{name}, record {index}: {ex.Message}"); }
            yield return result;
        }
    }

    private static ImportRecord Normalize(JsonElement row, string name, int index)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("ts", out _) ||
            !row.TryGetProperty("ms_played", out var duration) ||
            !(row.TryGetProperty("master_metadata_track_name", out _) || row.TryGetProperty("episode_name", out _) ||
              row.TryGetProperty("spotify_track_uri", out _)))
            throw new FormatException("Unsupported record; expected Spotify extended history fields.");
        if (row.GetRawText().Length > 1_048_576) throw new FormatException("Record exceeds the 1 MiB character limit.");
        var timestamp = GetString(row, "ts");
        string[] timestampFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (timestamp is null || !DateTimeOffset.TryParseExact(timestamp, timestampFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended))
            throw new FormatException("Missing or invalid timestamp with UTC offset.");
        if (!duration.TryGetInt64(out var ms) || ms < 0) throw new FormatException("Playback duration must be a non-negative integer.");
        var track = GetString(row, "master_metadata_track_name");
        var episode = GetString(row, "episode_name");
        var isEpisode = episode is not null || GetString(row, "spotify_episode_uri") is not null;
        var canonical = CanonicalJson(row);
        var ev = new DigitalEvent(ended.ToUniversalTime(), track ?? episode,
            GetString(row, "master_metadata_album_artist_name") ?? GetString(row, "episode_show_name"),
            GetString(row, "master_metadata_album_album_name"), ms, isEpisode ? "Podcast" : "Listening",
            GetString(row, "spotify_track_uri") ?? GetString(row, "spotify_episode_uri"), canonical);
        var stable = !string.IsNullOrWhiteSpace(ev.Uri) ? PlaybackIdentity.Create(ev.Uri, ended, ms,
            GetString(row, "username"), GetString(row, "platform")) : null;
        return new(ev, name, index, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), StableFingerprint: stable);
    }

    private static string? GetString(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"{key} must be a string or null.");
        var text = value.GetString();
        if (text?.Length > 65536) throw new FormatException($"{key} exceeds the text length limit.");
        return text;
    }

    private static string CanonicalJson(JsonElement row)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, row);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(property.Name)) throw new FormatException("Duplicate JSON property names are ambiguous.");
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

internal sealed class BoundedReadStream(Stream inner, long limit, Action<int>? onRead = null) : Stream
{
    private long read;
    private int Check(int count)
    {
        read += count;
        onRead?.Invoke(count);
        if (read > limit) throw new InvalidDataException("Decompressed input exceeds the safe byte limit.");
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Check(await inner.ReadAsync(buffer, ct));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
