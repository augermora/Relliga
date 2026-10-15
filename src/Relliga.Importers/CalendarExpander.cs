using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Ical.Net.Utility;
using NodaTime;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Importers;

public sealed class CalendarExpander : ICalendarExpander
{
    public IEnumerable<CalendarOccurrence> Expand(IReadOnlyList<CalendarDefinition> definitions,
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        if (until <= from || until - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Calendar queries require an increasing window of at most 366 days.");
        var master = definitions.SingleOrDefault(x => x.RecurrenceId.Length == 0);
        if (master?.IsCancelled == true) yield break;
        var parsed = definitions.ToDictionary(x => x.RecurrenceId,
            x => (Definition: x, Event: Calendar.Load(x.RawIcs)!.Events.Single()), StringComparer.Ordinal);
        var overrides = definitions.Where(x => x.RecurrenceId.Length > 0).ToDictionary(x => x.RecurrenceId, StringComparer.Ordinal);
        int evaluated = 0;
        foreach (var pair in parsed.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (pair.Definition.IsCancelled) continue;
            var ev = pair.Event;
            if (ev.DtStart is null) continue;
            var fromClock = ev.DtStart.IsFloating ? new CalDateTime(from.UtcDateTime.Date.AddDays(-2), null, ev.DtStart.HasTime) :
                new CalDateTime(from.UtcDateTime.AddDays(-2), "UTC");
            var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 1000 };
            foreach (var occurrence in Evaluate(pair.Definition, ev, fromClock, until, options, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (++evaluated > 20000) throw new InvalidOperationException("Calendar series exceeds the 20,000 occurrence evaluation limit. Narrow the date window.");
                var start = occurrence.Period.StartTime;
                var instant = GoogleCalendarImporter.Instant(start);
                var sort = instant?.UtcDateTime ?? start.Value;
                if (sort >= until.UtcDateTime.AddDays(2)) break;
                var key = GoogleCalendarImporter.Identity(start);
                if (pair.Definition.RecurrenceId.Length == 0 && overrides.ContainsKey(key)) continue;
                if (sort < from.UtcDateTime || sort >= until.UtcDateTime) continue;
                var end = occurrence.Period.EffectiveEndTime;
                yield return new(pair.Definition.Uid, pair.Definition.RecurrenceId,
                    pair.Definition.RecurrenceId.Length == 0 ? key : pair.Definition.RecurrenceId,
                    start.Value, end?.Value, instant, GoogleCalendarImporter.Instant(end), start.TzId,
                    !start.HasTime, start.HasTime && start.IsFloating, ev.Summary, ev.Description, ev.Location);
            }
        }
    }

    private static IEnumerable<Occurrence> Evaluate(CalendarDefinition definition, CalendarEvent ev, CalDateTime from,
        DateTimeOffset until, EvaluationOptions options, CancellationToken ct)
    {
        if (ev.RecurrenceRule is null || ev.DtStart is null || ev.DtStart.IsFloating || ev.DtStart.IsUtc)
            return ev.GetOccurrences(from, options);
        return EvaluateZonedRule(definition, ev, from, until, options, ct);
    }

    private static IEnumerable<Occurrence> EvaluateZonedRule(CalendarDefinition definition, CalendarEvent ev, CalDateTime from,
        DateTimeOffset until, EvaluationOptions options, CancellationToken ct)
    {
        // iCal.Net 5.2 interprets explicit gap timestamps using the pre-transition offset,
        // but RRULE-generated gap times must be omitted before COUNT and EXDATE are applied.
        string Without(params string[] properties) => properties.Aggregate(definition.RawIcs,
            (text, property) => Regex.Replace(text, $@"(?im)^{property}(?:;[^:\r\n]*)?:[^\r\n]*\r?\n", ""));
        var ruleEvent = Calendar.Load(Without("RDATE", "EXDATE"))!.Events.Single();
        var datesEvent = Calendar.Load(Without("RRULE"))!.Events.Single();
        var exclusions = Calendar.Load(Without("RRULE", "RDATE"))!.Events.Single();
        var count = ruleEvent.RecurrenceRule!.Count;
        var ruleUntil = ruleEvent.RecurrenceRule.Until;
        ruleEvent.RecurrenceRule.Count = null;
        ruleEvent.RecurrenceRule.Until = null;
        var zone = TimeZoneResolvers.TimeZoneResolver(ev.DtStart!.TzId!)!;
        // Evaluate rule dates on a floating clock so the library cannot normalize a gap
        // to a different clock hour before we validate the intended zoned local time.
        var originalEnd = ruleEvent.DtEnd;
        ruleEvent.Properties.Remove("DURATION"); ruleEvent.Properties.Remove("DTEND");
        exclusions.Properties.Remove("DURATION"); exclusions.Properties.Remove("DTEND");
        ruleEvent.DtStart = new CalDateTime(ev.DtStart.Value, null, true);
        if (originalEnd is not null) ruleEvent.DtEnd = new CalDateTime(originalEnd.Value, null, true);
        var occurrences = new Dictionary<string, Occurrence>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        int candidates = 0;
        void CheckBudget()
        {
            ct.ThrowIfCancellationRequested();
            if (++candidates > 1_000_000 || deadline.Elapsed > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException("Calendar recurrence exceeds the evaluation budget. Narrow the window or simplify this series.");
        }
        foreach (var occurrence in datesEvent.GetOccurrences(from, options))
        {
            CheckBudget();
            if (occurrence.Period.StartTime.AsUtc >= until.UtcDateTime.AddDays(2)) break;
            occurrences[GoogleCalendarImporter.Identity(occurrence.Period.StartTime)] = occurrence;
            if (occurrences.Count > 20000) throw new InvalidOperationException("Calendar series exceeds the occurrence limit.");
        }
        int validCount = 0;
        var floatingFrom = new CalDateTime(from.ToTimeZone(ev.DtStart.TzId).Value, null, true);
        foreach (var occurrence in ruleEvent.GetOccurrences(count is not null ? ruleEvent.DtStart : floatingFrom, options))
        {
            CheckBudget();
            var start = new CalDateTime(occurrence.Period.StartTime.Value, ev.DtStart.TzId, true);
            if (ruleUntil is not null && start.AsUtc > ruleUntil.AsUtc) break;
            if (start.AsUtc >= until.UtcDateTime.AddDays(2)) break;
            var explicitStart = start.Value == ev.DtStart.Value;
            if (!explicitStart && zone.MapLocal(LocalDateTime.FromDateTime(start.Value)).Count == 0) continue;
            if (count is not null && ++validCount > count) break;
            if (start.AsUtc < from.AsUtc) continue;
            exclusions.DtStart = start;
            if (!exclusions.GetOccurrences(start, options).Any()) continue;
            occurrences.TryAdd(GoogleCalendarImporter.Identity(start), new Occurrence(ev, new Ical.Net.DataTypes.Period(start, ev.EffectiveDuration)));
            if (occurrences.Count > 20000) throw new InvalidOperationException("Calendar series exceeds the occurrence limit.");
        }
        foreach (var occurrence in occurrences.Values.OrderBy(x => x.Period.StartTime.AsUtc)) yield return occurrence;
    }
}
