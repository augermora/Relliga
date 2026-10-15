using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class OwnershipTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private ImportService imports = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library", "relliga.db");
    private string Output(string name) => Path.Combine(files.DirectoryPath, name);
    public async ValueTask InitializeAsync() { store = new(Database, new CalendarExpander()); await store.InitializeAsync(); imports = new(store, [new ExportImporter()]); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task<ImportResult> Play(string title = "Synthetic song", string name = "input.json") => imports.ImportAsync(files.Write("[" + TestFiles.Row(title) + "]", name), "Automatic");
    private Task<ImportResult> Calendar(int sequence, string title) => imports.ImportAsync(files.Write(Calendars.Document(Calendars.Event($"DTSTART:20250103T100000Z\nSEQUENCE:{sequence}\nSUMMARY:{title}")), "calendar.ics"), "Automatic");
    private static EventQuery January(string? text = null) => new(text, Calendars.Date(), Calendars.Date(2));

    [Fact] public async Task JsonExportsAllPagesStableIdentityAndFullOrigins()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 1250).Select(i => TestFiles.Row("Synthetic " + i,
            DateTimeOffset.UnixEpoch.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")))) + "]";
        var path = files.Write(json); await imports.ImportAsync(path, "Automatic"); await imports.ImportAsync(path, "Automatic");
        var result = await store.ExportAsync(new(Output("events.json"), ExportFormat.Json, new(Provider: "Spotify", Page: 10)));
        Assert.Equal(1250, result.Records);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.Destination));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var rows = document.RootElement.GetProperty("events"); Assert.Equal(1250, rows.GetArrayLength());
        Assert.Equal("Synthetic 1249", rows[0].GetProperty("record").GetProperty("event").GetProperty("title").GetString());
        Assert.Equal(2, rows[0].GetProperty("origins").GetArrayLength());
        Assert.Equal("Spotify", rows[0].GetProperty("record").GetProperty("provider").GetString());
        Assert.StartsWith("Spotify:playback-v2:", rows[0].GetProperty("id").GetString());
        Assert.Equal(rows.GetArrayLength(), rows.EnumerateArray().Select(r => r.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(1250, (await store.GetSummaryAsync()).Events);
    }

    [Theory] [InlineData("=1+1")] [InlineData(" +SUM(A1:A2)")] [InlineData("@formula")] [InlineData("-42")] [InlineData("\tvalue")]
    public async Task CsvProtectsSpreadsheetFormulas(string title)
    {
        await Play(title); var path = Output("safe.csv");
        await store.ExportAsync(new(path, ExportFormat.Csv, new(Provider: "Spotify")));
        var text = await File.ReadAllTextAsync(path); Assert.Contains("\"'" + title + "\"", text);
    }

    [Fact] public async Task CsvEscapesCommasQuotesAndNewlinesAsUtf8()
    {
        var title = "Synthetic, \"café\"\nnext line";
        await Play(title); var path = Output("quoted.csv"); await store.ExportAsync(new(path, ExportFormat.Csv, new(Provider: "Spotify")));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Synthetic, \"\"café\"\"\nnext line\"", text); Assert.Contains("2025-01-01T12:00:00.0000000Z", text);
        Assert.StartsWith("id,source,type", text); Assert.Contains("metadata_json,calendar_json", text);
    }

    [Fact] public async Task JsonPreservesNullEmptyTemporalSemanticsAndFilteredCalendarOccurrences()
    {
        await Play();
        await imports.ImportAsync(files.Write(Calendars.Document(
            Calendars.Event("DTSTART;VALUE=DATE:20250103\nDTEND;VALUE=DATE:20250104\nSUMMARY:", "date"),
            Calendars.Event("DTSTART:20250104T090000\nSUMMARY:Floating", "float"),
            Calendars.Event("DTSTART:20250105T100000Z\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Routine", "repeat")), "calendar.ics"), "Automatic");
        var path = Output("calendar.json"); await store.ExportAsync(new(path, ExportFormat.Json, January() with { Provider = "Google Calendar" }));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var rows = json.RootElement.GetProperty("events");
        Assert.Equal(5, rows.GetArrayLength());
        var date = rows.EnumerateArray().Single(r => r.GetProperty("record").GetProperty("calendar").GetProperty("isAllDay").GetBoolean());
        Assert.Equal(JsonValueKind.Null, date.GetProperty("record").GetProperty("event").GetProperty("endedAt").ValueKind);
        Assert.Equal("", date.GetProperty("record").GetProperty("event").GetProperty("title").GetString());
        Assert.Contains("VALUE=DATE", date.GetProperty("metadata").GetProperty("RawIcs").GetString());
        var filtered = await store.ExportAsync(new(Output("filtered.json"), ExportFormat.Json, January("Routine") with { Until = Calendars.Date().AddDays(6) }));
        Assert.Equal(2, filtered.Records);
    }

    [Fact] public async Task ExportRecordsTheEffectiveDefaultCalendarWindow()
    {
        await Calendar(1, "Synthetic"); var path = Output("window.json");
        await store.ExportAsync(new(path, ExportFormat.Json, new()));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var query = json.RootElement.GetProperty("query");
        Assert.Equal(TimeSpan.FromDays(90), query.GetProperty("until").GetDateTimeOffset() - query.GetProperty("from").GetDateTimeOffset());
    }

    [Fact] public async Task ExportCancellationCleansPartialAndPreservesExistingDestination()
    {
        var path = files.Write("[" + string.Join(',', Enumerable.Range(0, 250).Select(i => TestFiles.Row("Track " + i,
            DateTimeOffset.UnixEpoch.AddMinutes(i).ToString("O")))) + "]"); await imports.ImportAsync(path, "Automatic");
        var destination = Output("existing.json"); await File.WriteAllTextAsync(destination, "previous output");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExportAsync(new(destination, ExportFormat.Json, new(Provider: "Spotify"), true),
            new InlineProgress(p => cancellation.Cancel()), cancellation.Token));
        Assert.Equal("previous output", await File.ReadAllTextAsync(destination)); Assert.Empty(Directory.GetFiles(files.DirectoryPath, ".relliga-*.tmp"));
        await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(new(destination, ExportFormat.Json, new())));
        await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(new(Database, ExportFormat.Json, new(), true)));
    }

    [Fact] public async Task BatchRemovalRetainsSharedPlaybackAndReparentsMetadataWithoutTouchingOriginals()
    {
        var first = await Play("Original", "first.json"); var second = await Play("Revised", "second.json");
        var id = Assert.Single((await store.QueryAsync(new())).Items).StableId;
        var preview = await store.PreviewDeletionAsync(new(ImportId: first.OperationId)); Assert.Equal(0, preview.RemovedRecords); Assert.Equal(1, preview.SharedRecords);
        await store.DeleteAsync(preview);
        var row = Assert.Single((await store.QueryAsync(new())).Items); Assert.Equal(id, row.StableId); Assert.Equal("Revised", row.Event.Title); Assert.Equal("second.json", row.SourceFile);
        Assert.True((await store.VerifyAsync()).Healthy); Assert.Equal(second.OperationId, Assert.Single(await store.GetImportsAsync()).Id);
        Assert.True(File.Exists(Output("first.json"))); Assert.True(File.Exists(Output("second.json")));
        Assert.Empty((await store.QueryAsync(new("Original"))).Items); Assert.Single((await store.QueryAsync(new("Revised"))).Items);
    }

    [Fact] public async Task RemovingLatestCalendarRevisionRestoresSurvivingRevisionAndFts()
    {
        await Calendar(1, "Original"); var latest = await Calendar(2, "Revised");
        var preview = await store.PreviewDeletionAsync(new(ImportId: latest.OperationId)); Assert.Equal(1, preview.RevisedComponents);
        await store.DeleteAsync(preview);
        Assert.Equal("Original", Assert.Single((await store.QueryAsync(January())).Items).Event.Title);
        Assert.Single((await store.QueryAsync(January("Original"))).Items); Assert.Empty((await store.QueryAsync(January("Revised"))).Items);
        Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task RemovingCancellationCanResurrectEarlierOwnedSeries()
    {
        await Calendar(1, "Original");
        var cancelled = await imports.ImportAsync(files.Write(Calendars.Document(Calendars.Event("STATUS:CANCELLED\nSEQUENCE:2")), "cancel.ics"), "Automatic");
        Assert.Empty((await store.QueryAsync(January())).Items);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(ImportId: cancelled.OperationId)));
        Assert.Single((await store.QueryAsync(January())).Items); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task SourceRemovalScrubsMixedImportDiagnosticsAndPreservesOtherSourceAfterRestart()
    {
        var archive = files.Zip(("personal-spotify.json", "[" + TestFiles.Row() + "]"), ("calendar.ics", Calendars.Document(Calendars.Event("DTSTART:20250103T100000Z"))));
        await imports.ImportAsync(archive, "Automatic");
        var preview = await store.PreviewDeletionAsync(new(Provider: "Spotify")); Assert.Equal(1, preview.RemovedRecords);
        await store.DeleteAsync(preview);
        Assert.Equal("Google Calendar", Assert.Single((await store.GetSummaryAsync()).BySource!).Provider);
        var history = Assert.Single(await store.GetImportsAsync()); Assert.Equal("[source data removed]", history.FileName); Assert.Null(history.Message);
        Assert.True((await store.VerifyAsync()).Healthy);
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Single((await store.QueryAsync(January())).Items); Assert.Empty((await store.QueryAsync(new(Provider: "Spotify"))).Items);
        Assert.True(File.Exists(archive));
    }

    [Fact] public async Task RemovalRollsBackOnFailureAndRejectsStaleConfirmation()
    {
        await Play(); var preview = await store.PreviewDeletionAsync(new(Provider: "Spotify"));
        store.MaintenanceFault = phase => { if (phase == "DeleteBeforeCommit") throw new IOException("Synthetic deletion failure"); };
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(preview));
        Assert.Single((await store.QueryAsync(new())).Items); Assert.True((await store.VerifyAsync()).Healthy);
        store.MaintenanceFault = null; await Play();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(preview));
        Assert.Single((await store.QueryAsync(new())).Items);
    }

    [Fact] public async Task LastBatchRemovalClearsAllOriginsFtsHistoryAndSources()
    {
        var batch = await Play(); await store.DeleteAsync(await store.PreviewDeletionAsync(new(ImportId: batch.OperationId)));
        Assert.Empty(await store.GetImportsAsync()); Assert.Equal(0, (await store.GetSummaryAsync()).Sources);
        Assert.Empty((await store.QueryAsync(new("Synthetic"))).Items); Assert.True((await store.VerifyAsync()).Healthy);
    }

    [Fact] public async Task ExportSerializesWithDeletionAndRetainsAConsistentSnapshot()
    {
        var path = files.Write("[" + string.Join(',', Enumerable.Range(0, 250).Select(i => TestFiles.Row("Track " + i,
            DateTimeOffset.UnixEpoch.AddMinutes(i).ToString("O")))) + "]"); await imports.ImportAsync(path, "Automatic");
        var preview = await store.PreviewDeletionAsync(new(Provider: "Spotify"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = Task.Run(() => store.ExportAsync(new(Output("consistent.json"), ExportFormat.Json, new(Provider: "Spotify")),
            new InlineProgress(_ => { started.TrySetResult(); release.Task.GetAwaiter().GetResult(); })));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deletion = store.DeleteAsync(preview); Assert.False(deletion.IsCompleted);
        release.SetResult(); Assert.Equal(250, (await export).Records); await deletion;
        Assert.Equal(0, (await store.GetSummaryAsync()).Events);
    }

    private sealed class InlineProgress(Action<OperationProgress> action) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => action(value); }
}
