using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Desktop;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class CalendarPersistenceTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private ImportService service = null!;
    private readonly ExportImporter importer = new();
    private string Database => Path.Combine(files.DirectoryPath, "unified.db");
    public async ValueTask InitializeAsync()
    {
        store = new(Database, new CalendarExpander()); await store.InitializeAsync(); service = new(store, [importer]);
    }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task<ImportResult> ImportCalendar(string content, string name = "calendar.ics") => service.ImportAsync(files.Write(content, name), "Automatic");
    private EventQuery January(string? text = null, int page = 0, int size = 50, string? provider = null, string? kind = null) =>
        new(text, Calendars.Date(), Calendars.Date(2), page, size, provider, kind);

    [Fact] public async Task CalendarDefinitionsPersistButInfiniteOccurrencesAreOnlyQueried()
    {
        var document = Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY\nSUMMARY:Synthetic routine"));
        var result = await ImportCalendar(document); Assert.Equal(1, result.Added);
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        var page = await store.QueryAsync(January()); Assert.Equal(31, page.Total);
        Assert.Equal(1, (await store.GetSummaryAsync()).Events);
        Assert.All(page.Items, x => Assert.NotNull(x.Calendar));
        using var c = new SqliteConnection($"Data Source={Database};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM calendar_components;";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact] public async Task ReimportAndOverlappingExportsUpdateStableUidWithoutAddingEvents()
    {
        var original = Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nSUMMARY:Original\nSEQUENCE:1"));
        Assert.Equal(1, (await ImportCalendar(original)).Added);
        Assert.Equal(1, (await ImportCalendar(original, "renamed.ics")).Duplicates);
        var revised = Calendars.Document(Calendars.Event("DTSTART:20250101T110000Z\nSUMMARY:Revised\nSEQUENCE:2"));
        var update = await ImportCalendar(revised); Assert.Equal(1, update.Updated); Assert.Equal(0, update.Added);
        var item = Assert.Single((await store.QueryAsync(January("Revised"))).Items);
        Assert.Equal(11, item.Calendar!.StartLocal.Hour);
        await ImportCalendar(original); // Older revision cannot undo the newer event.
        Assert.Equal("Revised", Assert.Single((await store.QueryAsync(January())).Items).Event.Title);
        Assert.Equal(1, (await store.GetSummaryAsync()).Events);
    }

    [Fact] public async Task OverridesArrivingInAnotherExportReplaceOriginalOccurrences()
    {
        await ImportCalendar(Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Routine")));
        await ImportCalendar(Calendars.Document(Calendars.Event("RECURRENCE-ID:20250102T100000Z\nDTSTART:20250104T140000Z\nSUMMARY:Rescheduled\nSEQUENCE:1")), "override.ics");
        var page = await store.QueryAsync(January()); Assert.Equal(3, page.Total);
        Assert.Equal("Rescheduled", page.Items[0].Event.Title);
        Assert.DoesNotContain(page.Items, x => x.Calendar!.StartLocal.Day == 2);
        Assert.Single((await store.QueryAsync(January("Rescheduled"))).Items);
        Assert.Equal(2, (await store.QueryAsync(January("Routine"))).Total);
    }

    [Fact] public async Task SpotifyAndCalendarShareChronologicalSearchAndFilters()
    {
        await service.ImportAsync(files.Write($"[{TestFiles.Row("Synthetic music", "2025-01-02T12:00:00Z")}]"), "Automatic");
        await ImportCalendar(Calendars.Document(Calendars.Event("DTSTART:20250103T100000Z\nSUMMARY:Synthetic appointment\nDESCRIPTION:Shared synthetic term\nLOCATION:Synthetic studio")));
        var page = await store.QueryAsync(January("synthetic"));
        Assert.Equal(2, page.Total); Assert.Equal("Google Calendar", page.Items[0].Provider); Assert.Equal("Spotify", page.Items[1].Provider);
        Assert.Single((await store.QueryAsync(January(provider: "Spotify"))).Items);
        Assert.Single((await store.QueryAsync(January(kind: "Calendar"))).Items);
        Assert.Single((await store.QueryAsync(January("studio"))).Items);
        var summary = await store.GetSummaryAsync(); Assert.Equal(2, summary.Sources); Assert.Equal(2, summary.SuccessfulImports);
        Assert.Equal(2, summary.BySource!.Count);
    }

    [Fact] public async Task CalendarOnlyAllDayAndFloatingRowsRemainSemanticallyDistinct()
    {
        await ImportCalendar(Calendars.Document(Calendars.Event("DTSTART;VALUE=DATE:20250102\nSUMMARY:Synthetic date", "all-day"),
            Calendars.Event("DTSTART:20250103T090000\nSUMMARY:Synthetic floating", "floating")));
        var rows = (await store.QueryAsync(January())).Items;
        Assert.Equal(2, rows.Count); Assert.All(rows, x => Assert.Null(x.Event.EndedAt));
        Assert.Contains(rows, x => new EventItem(x).DateLabel.Contains("All day"));
        Assert.Contains(rows, x => new EventItem(x).Details.Contains("no UTC instant"));
    }

    [Fact] public async Task HistoryAndStatisticsIncludeWarningsAndRealCounters()
    {
        await ImportCalendar(Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z", "valid"), Calendars.Event("SUMMARY:Missing start", "invalid")));
        var op = Assert.Single(await store.GetImportsAsync());
        Assert.Equal("Google Calendar", op.Provider); Assert.Equal("Partially completed", op.Status);
        Assert.Equal(2, op.Processed); Assert.Equal(1, op.Added); Assert.Equal(1, op.Rejected); Assert.Equal(1, op.Warnings);
        Assert.Contains("Google Calendar 1.0.0", op.ImporterVersion);
        Assert.Contains("DTSTART", new ImportItem(op).Details);
    }

    [Fact] public async Task ImportHistoryIsPagedBeyondTwentyOperations()
    {
        var document = Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z"));
        for (int i = 0; i < 23; i++) await ImportCalendar(document);
        var first = await store.GetImportsAsync(); var next = await store.GetImportsAsync(page: 1);
        Assert.Equal(20, first.Count); Assert.Equal(3, next.Count); Assert.True(first[^1].Id > next[0].Id);
        Assert.Equal(23, (await store.GetSummaryAsync()).TotalImports);
    }

    [Fact] public async Task ExcessiveWindowFailsClearlyAndDefaultWindowIsBounded()
    {
        await ImportCalendar(Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(new(From: Calendars.Date(), Until: Calendars.Date().AddYears(2))));
        var page = await store.QueryAsync(new()); Assert.NotNull(page.WindowFrom); Assert.NotNull(page.WindowUntil);
        Assert.InRange(page.Total, 1, 91);
    }

    [Fact] public async Task LargeDefinitionCollectionUsesPersistedPaging()
    {
        var events = Enumerable.Range(0, 600).Select(i => Calendars.Event($"DTSTART:20250115T100000Z\nSUMMARY:Synthetic item {i}", $"synthetic-{i:0000}")).ToArray();
        await ImportCalendar(Calendars.Document(events));
        var first = await store.QueryAsync(January(size: 50)); var last = await store.QueryAsync(January(page: 11, size: 50));
        Assert.Equal(600, first.Total); Assert.Equal(50, first.Items.Count); Assert.Equal(50, last.Items.Count);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(last.Items.Select(x => x.Id)));
    }

    [Fact] public async Task CalendarWritesRollbackOnCancellationAndFailure()
    {
        var parsed = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z")));
        using var cancel = new CancellationTokenSource();
        var failing = new ImportService(store, [new FailingCalendar(Assert.Single(parsed), cancel)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.ImportAsync("synthetic.ics", "Google Calendar", ct: cancel.Token));
        Assert.Equal(0, (await store.GetSummaryAsync()).Events);
        Assert.Equal("Cancelled", Assert.Single(await store.GetImportsAsync()).Status);
        Assert.Equal(1, (await store.GetImportsAsync())[0].Processed);
        var failure = new ImportService(store, [new FailingCalendar(parsed[0])]);
        await Assert.ThrowsAsync<IOException>(() => failure.ImportAsync("synthetic.ics", "Google Calendar"));
        Assert.Equal(0, (await store.GetSummaryAsync()).Events);
        Assert.Empty((await store.QueryAsync(January())).Items);
    }

    [Fact] public async Task SpotifyMetadataChangesDeduplicateWithoutLosingRepeatedOccurrences()
    {
        var row = TestFiles.Row();
        await service.ImportAsync(files.Write($"[{row},{row}]"), "Automatic");
        var changed = row.Replace("Synthetic song", "Updated song name").Replace("\"skipped\":false", "\"skipped\":true,\"ip_addr\":\"192.0.2.1\"");
        var result = await service.ImportAsync(files.Write($"[{changed},{changed}]"), "Automatic");
        Assert.Equal(0, result.Added); Assert.Equal(2, result.Duplicates); Assert.Equal(2, (await store.GetSummaryAsync()).Events);
        var distinct = TestFiles.Row(platform: "other-device");
        Assert.Equal(1, (await service.ImportAsync(files.Write($"[{distinct}]"), "Automatic")).Added);
    }

    [Fact] public async Task MissingPlaybackUriKeepsConservativeIdentityAcrossSeparateExports()
    {
        var one = TestFiles.Row("First synthetic track").Replace("spotify:track:synthetic", "");
        var two = TestFiles.Row("Other synthetic track").Replace("spotify:track:synthetic", "");
        Assert.Equal(1, (await service.ImportAsync(files.Write($"[{one}]"), "Automatic")).Added);
        Assert.Equal(1, (await service.ImportAsync(files.Write($"[{two}]"), "Automatic")).Added);
        Assert.Equal(1, (await service.ImportAsync(files.Write($"[{one}]"), "Automatic")).Duplicates);
    }

    [Fact] public async Task DesktopFlowPreviewsCalendarAndFiltersMixedTimeline()
    {
        var calendar = files.Write(Calendars.Document(Calendars.Event("DTSTART:20250103T100000Z\nSUMMARY:Synthetic appointment")), "desktop.ics");
        var vm = new MainViewModel(store, service, importer, store) { SelectFile = () => Task.FromResult<string?>(calendar) };
        await vm.InitializeCommand.ExecuteAsync(null); await vm.PickFileCommand.ExecuteAsync(null);
        Assert.Contains("Google Calendar", vm.Preview); await vm.ImportCommand.ExecuteAsync(null); Assert.Null(vm.Error);
        vm.SelectedSection = 2; if (vm.FilterCommand.ExecutionTask is { } load) await load;
        Assert.True(Assert.Single(vm.Events).IsCalendar);
        vm.SourceFilter = "Spotify"; await vm.FilterCommand.ExecuteAsync(null); Assert.Empty(vm.Events);
        vm.SourceFilter = "All sources"; vm.KindFilter = "Calendar"; await vm.FilterCommand.ExecuteAsync(null);
        vm.SelectedEvent = Assert.Single(vm.Events); Assert.Contains("UID:", vm.SelectedDetails);
        Assert.Contains("Calendar", vm.SourceStatistics); Assert.Contains("Google Calendar", vm.RecentImports[0].Details);
    }

    [Fact] public async Task ShippedSyntheticFixturesWorkTogetherAndCanBeReimported()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var spotifyPath = Path.Combine(fixtureDirectory, "synthetic-spotify.json");
        var calendarPath = Path.Combine(fixtureDirectory, "synthetic-calendar.ics");
        Assert.Equal(2, (await service.ImportAsync(spotifyPath, "Automatic")).Added);
        Assert.Equal(4, (await service.ImportAsync(calendarPath, "Automatic")).Added);
        var result = await store.QueryAsync(January()); Assert.Equal(7, result.Total);
        Assert.Equal(2, (await service.ImportAsync(spotifyPath, "Automatic")).Duplicates);
        Assert.Equal(4, (await service.ImportAsync(calendarPath, "Automatic")).Duplicates);
        Assert.Equal(7, (await store.QueryAsync(January())).Total);
    }

    [Fact] public async Task MalformedArchiveMemberIsPartialWithoutInventingRejectedRecordCounts()
    {
        var zip = files.Zip(("good.ics", Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z"))),
            ("bad.ics", "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT"));
        var result = await service.ImportAsync(zip, "Automatic");
        Assert.Equal(1, result.Processed); Assert.Equal(0, result.Rejected);
        var op = Assert.Single(await store.GetImportsAsync()); Assert.Equal("Partially completed", op.Status);
        Assert.Contains("unparsed records cannot be counted", op.Message!);
    }

    private sealed class FailingCalendar(ImportRecord record, CancellationTokenSource? cancel = null) : IDataImporter
    {
        public string Provider => "Google Calendar"; public string Version => "synthetic";
        public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return record; await Task.Yield();
            if (cancel is not null) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new IOException("Synthetic calendar failure");
        }
    }
}
