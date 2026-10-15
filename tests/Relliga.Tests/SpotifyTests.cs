using System.IO.Compression;
using System.Text.Json;
using Relliga.Domain;
using Relliga.Importers;
using Xunit;

namespace Relliga.Tests;

public sealed class TestFiles : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Relliga.Tests", Guid.NewGuid().ToString("N"));
    public TestFiles() => Directory.CreateDirectory(DirectoryPath);
    public string Write(string text, string name = "input.json")
    { var path = Path.Combine(DirectoryPath, name); File.WriteAllText(path, text); return path; }
    public string Zip(params (string Name, string Content)[] files)
    {
        var path = Path.Combine(DirectoryPath, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open()); writer.Write(content); }
        return path;
    }
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    public static string Row(string title = "Synthetic song", string timestamp = "2025-01-01T12:00:00Z", int ms = 123000,
        string platform = "test-device") => JsonSerializer.Serialize(new
        {
            ts = timestamp, ms_played = ms, master_metadata_track_name = title,
            master_metadata_album_artist_name = "Synthetic artist", master_metadata_album_album_name = "Synthetic album",
            spotify_track_uri = "spotify:track:synthetic", platform, skipped = false
        });
    public static async Task<List<ImportRecord>> Read(string path, ImportLimits? limits = null, CancellationToken ct = default)
    {
        var rows = new List<ImportRecord>();
        await foreach (var row in new SpotifyImporter(limits).ReadAsync(path, null, ct)) rows.Add(row);
        return rows;
    }
}

public sealed class SpotifyTests : IDisposable
{
    private readonly TestFiles files = new();
    public void Dispose() => files.Dispose();

    [Fact] public async Task DetectsContentsRegardlessOfFilenameAndNormalizesUtc()
    {
        var rows = await TestFiles.Read(files.Write("[" + TestFiles.Row(timestamp: "2025-01-01T14:00:00+02:00") + "]", "anything.txt"));
        var ev = Assert.Single(rows).Event!;
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), ev.EndedAt);
        Assert.Equal("Synthetic song", ev.Title);
        Assert.Equal("Synthetic artist", ev.Artist);
        Assert.Equal(123000, ev.DurationMs);
        Assert.Contains("test-device", ev.MetadataJson);
        Assert.Equal(1, rows[0].RecordIndex);
    }

    [Fact] public async Task RetainsNullsAndPodcastInformation()
    {
        var rows = await TestFiles.Read(files.Write("""
            [{"ts":"2025-01-01T00:00:00Z","ms_played":0,"master_metadata_track_name":null,
            "episode_name":"Synthetic episode","episode_show_name":null,"spotify_episode_uri":"spotify:episode:synthetic"}]
            """));
        var ev = Assert.Single(rows).Event!;
        Assert.Equal("Podcast", ev.Kind); Assert.Null(ev.Artist); Assert.Null(ev.Album);
        Assert.Equal("Synthetic episode", ev.Title);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"endTime\":\"2025-01-01\",\"msPlayed\":1}]")]
    [InlineData("[{\"ts\":\"invalid\",\"ms_played\":3,\"master_metadata_track_name\":null}]")]
    [InlineData("[{\"ts\":\"2025-01-01T00:00:00Z\",\"ms_played\":-1,\"master_metadata_track_name\":null}]")]
    [InlineData("[{\"ts\":\"2025-01-01T00:00:00\",\"ms_played\":1,\"master_metadata_track_name\":null}]")]
    [InlineData("[{\"ts\":\"01/02/2025 00:00:00Z\",\"ms_played\":1,\"master_metadata_track_name\":null}]")]
    public async Task RejectsUnsupportedOrEntirelyInvalidInput(string json) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Write(json)));

    [Fact] public async Task PreservesFractionalUtcTimestamps()
    {
        var row = Assert.Single(await TestFiles.Read(files.Write("[" + TestFiles.Row(timestamp: "2025-01-01T12:00:00.1234567Z") + "]")));
        Assert.Equal(1234567, row.Event!.EndedAt.Ticks % TimeSpan.TicksPerSecond);
    }

    [Theory] [InlineData("")] [InlineData("not json")] [InlineData("{}")] [InlineData("[{")]
    public async Task RejectsMalformedJson(string json) =>
        await Assert.ThrowsAsync<JsonException>(() => TestFiles.Read(files.Write(json)));

    [Fact] public async Task ReportsMalformedRecordsWithoutDiscardingValidOnes()
    {
        var rows = await TestFiles.Read(files.Write("[42," + TestFiles.Row() + "," + TestFiles.Row(ms: -1) + "]"));
        Assert.Equal(3, rows.Count); Assert.Null(rows[0].Event); Assert.NotNull(rows[0].Warning);
        Assert.NotNull(rows[1].Event); Assert.Contains("duration", rows[2].Warning!);
    }

    [Fact] public async Task CanonicalIdentityIgnoresPropertyOrderAndWhitespace()
    {
        var row = TestFiles.Row();
        using var doc = JsonDocument.Parse(row);
        var reverse = JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Reverse().ToDictionary(x => x.Name, x => x.Value));
        var rows = await TestFiles.Read(files.Write($"[{row},\n{reverse}]"));
        Assert.Equal(rows[0].Fingerprint, rows[1].Fingerprint);
    }

    [Fact] public async Task ReadsJsonWithinArchiveAndReportsUnrelatedJson()
    {
        var zip = files.Zip(("nested/arbitrary.json", "[" + TestFiles.Row() + "]"),
            ("account.json", "{\"unrelated\":true}"), ("readme.txt", "Synthetic archive"));
        var rows = await TestFiles.Read(zip);
        Assert.Single(rows, x => x.Event is not null);
        Assert.Single(rows, x => x.Warning is not null);
        Assert.Equal("nested/arbitrary.json", rows[0].SourceFile);
    }

    [Theory] [InlineData("../escape.json")] [InlineData("C:/escape.json")] [InlineData("/escape.json")] [InlineData("..\\escape.json")]
    public async Task RejectsUnsafeArchivePaths(string name) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Zip((name, "[]"))));

    [Fact] public async Task RejectsDuplicateArchiveNames() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Zip(("same.json", "[]"), ("same.json", "[]"))));

    [Fact] public async Task RejectsExcessiveEntries() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Zip(("a", "a"), ("b", "b")), new(MaxEntries: 1)));

    [Fact] public async Task RejectsCompressionBomb() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Zip(("a.json", new string(' ', 100000)))));

    [Fact] public async Task RejectsOversizeJson() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Write("[" + TestFiles.Row() + "]"), new(MaxJsonBytes: 20)));

    [Fact] public async Task EnforcesRecordLimit() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Write($"[{TestFiles.Row()},{TestFiles.Row()}]"), new(MaxRecords: 1)));

    [Fact] public async Task RejectsBrokenZip() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Write("not a ZIP", "broken.zip")));

    [Fact] public async Task RejectsTotalExpandedSizeAcrossMembers() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => TestFiles.Read(files.Zip(("a.json", "[" + TestFiles.Row() + "]"),
            ("b.json", "[" + TestFiles.Row() + "]")), new(MaxExpandedBytes: 400)));

    [Fact] public async Task AmbiguousDuplicatePropertiesAndOversizeTextAreReported()
    {
        var duplicate = TestFiles.Row().Replace("\"ms_played\":123000", "\"ms_played\":123000,\"ms_played\":1");
        var oversized = TestFiles.Row(new string('a', 65537));
        var rows = await TestFiles.Read(files.Write($"[{duplicate},{oversized},{TestFiles.Row()}]"));
        Assert.Contains("Duplicate JSON", rows[0].Warning!); Assert.Contains("text length", rows[1].Warning!);
        Assert.NotNull(rows[2].Event);
    }

    [Fact] public async Task ExcessiveJsonDepthIsRejected()
    {
        var nested = new string('[', 40) + "0" + new string(']', 40);
        await Assert.ThrowsAsync<JsonException>(() => TestFiles.Read(files.Write(nested)));
    }

    [Fact] public async Task TruncatedArchiveMemberRetainsValidRecordsWithExplicitWarning()
    {
        var malformed = "[" + TestFiles.Row() + "," + new string(' ', 40000) + "broken]";
        var rows = await TestFiles.Read(files.Zip(("broken.json", malformed), ("valid.json", "[" + TestFiles.Row("Other") + "]")));
        Assert.Equal(2, rows.Count(x => x.Event is not null));
        Assert.Contains(rows, x => x.Warning?.Contains("broken.json") == true);
    }

    [Fact] public async Task CancellationIsObserved()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestFiles.Read(files.Write($"[{TestFiles.Row()}]"), ct: cancel.Token));
    }
}
