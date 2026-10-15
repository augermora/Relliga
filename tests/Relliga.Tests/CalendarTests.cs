using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public static class Calendars
{
    public static string Document(params string[] components) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Relliga Synthetic Tests//EN\r\n" + string.Join("\r\n", components) + "\r\nEND:VCALENDAR\r\n";
    public static string Event(string properties, string uid = "synthetic-calendar-event") =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20250101T000000Z\r\n{properties.Replace("\n", "\r\n").Replace("\r\r", "\r")}\r\nEND:VEVENT";
    public static DateTimeOffset Date(int month = 1, int day = 1) => new(2025, month, day, 0, 0, 0, TimeSpan.Zero);
    public static async Task<List<ImportRecord>> Parse(TestFiles files, string content, string name = "calendar.ics", CancellationToken ct = default)
    {
        var result = new List<ImportRecord>();
        await foreach (var record in new ExportImporter().ReadAsync(files.Write(content, name), null, ct)) result.Add(record);
        return result;
    }
    public static IReadOnlyList<CalendarOccurrence> Expand(IEnumerable<ImportRecord> records, DateTimeOffset? from = null, DateTimeOffset? until = null) =>
        new CalendarExpander().Expand(records.Where(x => x.Calendar is not null).Select(x => x.Calendar!).ToArray(),
            from ?? Date(), until ?? Date(2), default).ToArray();
}

public sealed class CalendarParsingTests : IDisposable
{
    private readonly TestFiles files = new();
    public void Dispose() => files.Dispose();

    [Fact] public async Task DetectsCalendarContentsRegardlessOfFilenameAndPreservesText()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250110T100000Z\nDTEND:20250110T110000Z\nSUMMARY:Synthetic planning\nDESCRIPTION:Line one\\nLine two\nLOCATION:Synthetic room")), "unknown.data");
        var record = Assert.Single(records); Assert.Equal("Google Calendar", record.Provider);
        Assert.Equal("Synthetic planning", record.Calendar!.Title);
        Assert.Contains("Line two", record.Calendar.Description!);
        var occurrence = Assert.Single(Calendars.Expand(records));
        Assert.Equal(new DateTimeOffset(2025, 1, 10, 10, 0, 0, TimeSpan.Zero), occurrence.StartInstant);
        Assert.Equal(11, occurrence.EndInstant!.Value.Hour);
    }

    [Fact] public async Task AllDayDatesNeverAcquireUtcInstants()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;VALUE=DATE:20250110\nDTEND;VALUE=DATE:20250112\nSUMMARY:Synthetic holiday")));
        var occurrence = Assert.Single(Calendars.Expand(records));
        Assert.True(occurrence.IsAllDay); Assert.Null(occurrence.StartInstant); Assert.Null(occurrence.EndInstant);
        Assert.Equal(new DateTime(2025, 1, 10), occurrence.StartLocal); Assert.Equal(new DateTime(2025, 1, 12), occurrence.EndLocal);
        Assert.Null(records[0].Event!.EndedAt); Assert.Null(records[0].Event!.DurationMs);
    }

    [Fact] public async Task FloatingClockIsNotAssignedTheMachineTimezone()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250110T100000\nDTEND:20250110T110000")));
        var occurrence = Assert.Single(Calendars.Expand(records));
        Assert.True(occurrence.IsFloating); Assert.Null(occurrence.TimeZoneId); Assert.Null(occurrence.StartInstant);
        Assert.Equal(10, occurrence.StartLocal.Hour);
    }

    [Fact] public async Task DailyWallClockRecurrenceTracksSpringDaylightSaving()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;TZID=Europe/Madrid:20250329T090000\nDTEND;TZID=Europe/Madrid:20250329T100000\nRRULE:FREQ=DAILY;COUNT=3")));
        var occurrences = Calendars.Expand(records, Calendars.Date(3, 28), Calendars.Date(4, 2));
        Assert.Equal(3, occurrences.Count); Assert.All(occurrences, x => Assert.Equal(9, x.StartLocal.Hour));
        Assert.Equal(new[] { 8, 7, 7 }, occurrences.Select(x => x.StartInstant!.Value.Hour));
    }

    [Fact] public async Task AutumnTransitionRetainsWallClockAndUsesEarlierAmbiguousOffset()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;TZID=Europe/Madrid:20251025T023000\nDURATION:PT1H\nRRULE:FREQ=DAILY;COUNT=3")));
        var occurrences = Calendars.Expand(records, Calendars.Date(10, 24), Calendars.Date(10, 29));
        Assert.Equal(new[] { 0, 0, 1 }, occurrences.Select(x => x.StartInstant!.Value.Hour));
    }

    [Fact] public async Task RecurrenceSkipsNonexistentSpringClockTimes()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;TZID=Europe/Madrid:20250329T023000\nRRULE:FREQ=DAILY;COUNT=3")));
        var occurrences = Calendars.Expand(records, Calendars.Date(3, 28), Calendars.Date(4, 3));
        Assert.Equal(new[] { 29, 31, 1 }, occurrences.Select(x => x.StartLocal.Day));
    }

    [Fact] public async Task GapCountIsAppliedBeforeExclusionDates()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;TZID=Europe/Madrid:20250329T023000\nRRULE:FREQ=DAILY;COUNT=3\nEXDATE;TZID=Europe/Madrid:20250331T023000")));
        var occurrences = Calendars.Expand(records, Calendars.Date(3, 28), Calendars.Date(4, 3));
        Assert.Equal(new[] { 29, 1 }, occurrences.Select(x => x.StartLocal.Day));
    }

    [Fact] public async Task ZonedUntilUsesInstantAcrossAnAmbiguousClockBoundary()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;TZID=Europe/Madrid:20251025T023000\nRRULE:FREQ=DAILY;UNTIL=20251026T011500Z")));
        var occurrences = Calendars.Expand(records, Calendars.Date(10, 24), Calendars.Date(10, 29));
        Assert.Equal(new[] { 25, 26 }, occurrences.Select(x => x.StartLocal.Day));
    }

    [Fact] public async Task AllDayRecurrenceDatesAndExclusionsKeepDateSemantics()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART;VALUE=DATE:20250101\nRRULE:FREQ=DAILY;COUNT=3\nEXDATE;VALUE=DATE:20250102\nRDATE;VALUE=DATE:20250110")));
        var occurrences = Calendars.Expand(records);
        Assert.Equal(new[] { 1, 3, 10 }, occurrences.Select(x => x.StartLocal.Day));
        Assert.All(occurrences, x => Assert.Null(x.StartInstant));
    }

    [Fact] public async Task RecurrenceDatePeriodsRetainTheirOwnDurations()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nDURATION:PT1H\nRDATE;VALUE=PERIOD:20250105T100000Z/PT2H")));
        var occurrence = Assert.Single(Calendars.Expand(records), x => x.StartLocal.Day == 5);
        Assert.Equal(12, occurrence.EndLocal!.Value.Hour);
    }

    [Fact] public async Task EntirelyUnsupportedRecurrenceReportsItsActualReason()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=SECONDLY"))));
        Assert.Contains("Only DAILY", error.Message);
    }

    [Fact] public async Task RecurrenceDatesExclusionsAndMovedOverrideAreCombined()
    {
        var master = Calendars.Event("DTSTART:20250101T100000Z\nDURATION:PT1H\nRRULE:FREQ=DAILY;COUNT=4\nRDATE:20250110T100000Z\nEXDATE:20250102T100000Z\nSUMMARY:Original");
        var changed = Calendars.Event("RECURRENCE-ID:20250103T100000Z\nDTSTART:20250103T140000Z\nDURATION:PT2H\nSUMMARY:Changed\nSEQUENCE:1");
        var records = await Calendars.Parse(files, Calendars.Document(master, changed));
        var occurrences = Calendars.Expand(records).OrderBy(x => x.StartInstant).ToArray();
        Assert.Equal(new[] { 1, 3, 4, 10 }, occurrences.Select(x => x.StartLocal.Day));
        Assert.Equal("Changed", occurrences[1].Title); Assert.Equal(14, occurrences[1].StartLocal.Hour);
        Assert.Equal(16, occurrences[1].EndLocal!.Value.Hour);
    }

    [Fact] public async Task CancelledOverrideRemovesOnlyItsOccurrence()
    {
        var records = await Calendars.Parse(files, Calendars.Document(
            Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY;COUNT=3"),
            Calendars.Event("RECURRENCE-ID:20250102T100000Z\nSTATUS:CANCELLED\nSEQUENCE:1")));
        Assert.Equal(new[] { 1, 3 }, Calendars.Expand(records).OrderBy(x => x.StartLocal).Select(x => x.StartLocal.Day));
    }

    [Fact] public async Task InfiniteRecurrenceRemainsOneDefinitionAndHasBoundedOccurrences()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY")));
        Assert.Single(records); Assert.True(records[0].Calendar!.IsRecurring);
        Assert.Equal(31, Calendars.Expand(records).Count);
        Assert.Throws<ArgumentException>(() => Calendars.Expand(records, Calendars.Date(), Calendars.Date().AddYears(2)));
    }

    [Fact] public async Task MonthlyByDayAndUntilAreEvaluated()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250106T100000Z\nRRULE:FREQ=MONTHLY;BYDAY=1MO;UNTIL=20250401T000000Z")));
        Assert.Equal(new[] { 6, 3, 3 }, Calendars.Expand(records, Calendars.Date(), Calendars.Date(4)).Select(x => x.StartLocal.Day));
    }

    [Fact] public async Task MixedArchiveDetectsActualContents()
    {
        var zip = files.Zip(("renamed-calendar.txt", Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z"))),
            ("arbitrary.txt", $"[{TestFiles.Row()}]"));
        var result = new List<ImportRecord>();
        await foreach (var record in new ExportImporter().ReadAsync(zip, null, default)) result.Add(record);
        Assert.Contains(result, x => x.Calendar is not null); Assert.Contains(result, x => x.Provider == "Spotify");
    }

    [Theory]
    [InlineData("DTSTART:20251301T100000Z")]
    [InlineData("DTSTART:20250101T100000Z\nDTEND:20241231T100000Z")]
    [InlineData("DTSTART:20250101T100000Z\nRRULE:FREQ=SECONDLY")]
    [InlineData("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY;INTERVAL=0")]
    [InlineData("DTSTART;TZID=Unknown/Synthetic:20250101T100000")]
    [InlineData("DTSTART:20250101T100000Z\nRECURRENCE-ID;RANGE=THISANDFUTURE:20250101T100000Z")]
    [InlineData("SUMMARY:Missing start")]
    public async Task InvalidComponentsProduceWarningsWhileOtherEventsImport(string invalid)
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event(invalid, "invalid"), Calendars.Event("DTSTART:20250105T100000Z", "valid")));
        Assert.Contains(records, x => x.Warning is not null); Assert.Single(records, x => x.Calendar is not null);
    }

    [Fact] public async Task UnsupportedOverrideBlocksItsMasterRatherThanResurrectingWrongOccurrence()
    {
        var records = await Calendars.Parse(files, Calendars.Document(
            Calendars.Event("DTSTART:20250101T100000Z\nRRULE:FREQ=DAILY"),
            Calendars.Event("DTSTART:20250102T120000Z\nRECURRENCE-ID;RANGE=THISANDFUTURE:20250102T100000Z"),
            Calendars.Event("DTSTART:20250104T100000Z", "other")));
        Assert.Equal(2, records.Count(x => x.Warning is not null)); Assert.Single(records, x => x.Calendar is not null);
    }

    [Fact] public async Task MalformedDocumentFailsWithoutGuessing()
    {
        await Assert.ThrowsAsync<CalendarFormatException>(() => Calendars.Parse(files, "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:broken"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Calendars.Parse(files, ""));
    }

    [Fact] public async Task MissingUidIsRejectedInsteadOfUsingLibraryGeneratedIdentity()
    {
        var invalid = "BEGIN:VEVENT\nDTSTART:20250101T100000Z\nEND:VEVENT";
        var result = await Calendars.Parse(files, Calendars.Document(invalid, Calendars.Event("DTSTART:20250101T100000Z", "valid")));
        Assert.Contains(result, x => x.Warning?.Contains("UID") == true);
    }

    [Fact] public async Task FoldedAndEscapedTextAndRemoteAttachmentsRemainPassive()
    {
        var records = await Calendars.Parse(files, Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z\nSUMMARY:Folded\n title\nDESCRIPTION:Comma\\, semi\\; slash\\\\\nATTACH:https://example.invalid/never-fetch\nBEGIN:VALARM\nACTION:DISPLAY\nTRIGGER:-PT5M\nDESCRIPTION:Passive alarm\nEND:VALARM")));
        var def = Assert.Single(records).Calendar!;
        Assert.Equal("Foldedtitle", def.Title); Assert.Contains("https://example.invalid/never-fetch", def.RawIcs);
    }

    [Fact] public async Task CalendarCancellationAndArchiveTraversalAreEnforced()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Calendars.Parse(files, Calendars.Document(), ct: cancelled.Token));
        var bad = files.Zip(("../escape.ics", Calendars.Document()));
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in new ExportImporter().ReadAsync(bad, null, default)) { } });
    }

    [Theory] [InlineData("ratio")] [InlineData("entries")] [InlineData("expanded")]
    public async Task AutomaticArchiveRouterEnforcesIndependentSafetyLimits(string limit)
    {
        var contents = limit == "ratio" ? new string(' ', 100000) : Calendars.Document(Calendars.Event("DTSTART:20250101T100000Z"));
        var zip = files.Zip(("one.ics", contents), ("two.ics", contents));
        var limits = limit == "entries" ? new ImportLimits(MaxEntries: 1) : limit == "expanded" ? new(MaxExpandedBytes: 100) : new();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in new ExportImporter(limits).ReadAsync(zip, null, default)) { } });
    }

    [Fact] public async Task UnknownTimezoneInRecurrenceDateProducesAnImportWarning()
    {
        var invalid = Calendars.Event("DTSTART:20250101T100000Z\nRDATE;TZID=Unknown/Synthetic:20250102T100000", "invalid-date");
        var records = await Calendars.Parse(files, Calendars.Document(invalid, Calendars.Event("DTSTART:20250101T100000Z", "valid")));
        Assert.Contains(records, x => x.Warning?.Contains("Unknown/Synthetic", StringComparison.Ordinal) == true);
        Assert.Single(records, x => x.Calendar is not null);
    }
}
