using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Utility;
using Relliga.Application;
using Relliga.Domain;
using Calendar = Ical.Net.Calendar;

namespace Relliga.Importers;

public sealed class CalendarFormatException(string message) : FormatException(message);

public sealed class GoogleCalendarImporter : IDataImporter
{
    public string Provider => "Google Calendar";
    public string Version => "1.0.0";
    public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var record in new ExportImporter().ReadAsync(path, progress, cancellationToken))
        {
            if (record.Event is not null && record.Calendar is null)
                throw new InvalidDataException("This input contains Spotify records; use automatic import for mixed archives.");
            yield return record;
        }
    }

    internal async IAsyncEnumerable<ImportRecord> ReadCalendarAsync(Stream input, string name,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var bounded = new BoundedReadStream(input, 16 * 1024 * 1024);
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true), true, 16384, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        var blocks = SplitCalendar(text, ct);
        var header = blocks.Header;
        var parsed = new List<(int Index, string Uid, CalendarDefinition? Definition, string? Error)>();
        int index = 0;
        foreach (var raw in blocks.Events)
        {
            ct.ThrowIfCancellationRequested();
            if (++index > 10000) throw new InvalidDataException("Calendar exceeds the 10,000 component limit.");
            var uid = PropertyValue(raw, "UID") ?? "";
            try { parsed.Add((index, uid, Normalize(header, raw), null)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
            { parsed.Add((index, uid, null, ex.Message)); }
        }
        // An invalid override must not silently restore the master's unmodified occurrence.
        var blocked = parsed.Where(x => x.Error is not null && x.Uid.Length > 0).Select(x => x.Uid).ToHashSet(StringComparer.Ordinal);
        if (header.Contains("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            yield return new(null, name, 0, Warning: $"{name}: embedded VTIMEZONE definitions are preserved. Recognized timezone IDs use the bundled timezone database; custom timezone definitions are not supported.", Rejected: false);
        if (blocks.OtherComponents > 0)
            yield return new(null, name, 0, Warning: $"{name}: {blocks.OtherComponents} non-event calendar components were not imported.", Rejected: false);
        foreach (var item in parsed)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Error is not null || blocked.Contains(item.Uid))
            {
                yield return new(null, name, item.Index, Warning: $"{name}, VEVENT {item.Index}: {item.Error ?? "Series skipped because another component or override is invalid."}");
                continue;
            }
            var def = item.Definition!;
            var metadata = JsonSerializer.Serialize(new { def.Uid, def.RecurrenceId, def.Sequence, def.ModifiedAt, def.RawIcs });
            yield return new(new(null, def.Title, null, null, null, "Calendar", null, metadata), name, item.Index,
                def.ContentHash, Calendar: def);
        }
    }

    private static CalendarDefinition Normalize(string header, string raw)
    {
        if (raw.Length > 1_048_576) throw new CalendarFormatException("VEVENT exceeds the 1 MiB character limit.");
        var uid = PropertyValue(raw, "UID");
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 2048) throw new CalendarFormatException("A stable UID is required (maximum 2,048 characters).");
        foreach (var singleton in new[] { "UID", "DTSTART", "DTEND", "DURATION", "RECURRENCE-ID", "SEQUENCE", "SUMMARY" })
            if (Regex.Matches(raw, $@"(?im)^{singleton}(?:;[^:\r\n]*)?:").Count > 1)
                throw new CalendarFormatException($"Duplicate {singleton} properties are ambiguous.");
        if (Regex.IsMatch(raw, @"(?im)^RECURRENCE-ID;[^\r\n]*RANGE="))
            throw new CalendarFormatException("RECURRENCE-ID RANGE (including THISANDFUTURE) is not supported; this series is rejected.");
        if (Regex.IsMatch(raw, @"(?im)^EXRULE[:;]"))
            throw new CalendarFormatException("Legacy EXRULE recurrence is unsupported; use EXDATE.");
        if (PropertyValue(raw, "DTEND") is not null && PropertyValue(raw, "DURATION") is not null)
            throw new CalendarFormatException("Use DTEND or DURATION, not both.");
        var cancelled = string.Equals(PropertyValue(raw, "STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(PropertyValue(header, "METHOD"), "CANCEL", StringComparison.OrdinalIgnoreCase);
        if (PropertyValue(raw, "DTSTART") is null && !cancelled)
            throw new CalendarFormatException("DTSTART is required for active events.");
        if (PropertyValue(raw, "RRULE") is { } ruleText)
        {
            var allowed = new HashSet<string>(["FREQ", "UNTIL", "COUNT", "INTERVAL", "BYDAY", "BYMONTHDAY", "BYYEARDAY", "BYWEEKNO", "BYMONTH", "BYSETPOS", "WKST", "BYHOUR", "BYMINUTE", "BYSECOND"], StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in ruleText.Split(';'))
            {
                var pieces = part.Split('=', 2);
                if (pieces.Length != 2 || pieces[1].Length == 0 || !allowed.Contains(pieces[0]) || !seen.Add(pieces[0]))
                    throw new CalendarFormatException("Malformed, duplicate or unsupported RRULE parameter.");
            }
            if (!seen.Contains("FREQ")) throw new CalendarFormatException("RRULE must specify FREQ.");
        }
        var ics = header + raw + "\r\nEND:VCALENDAR\r\n";
        var calendar = Calendar.Load(ics) ?? throw new CalendarFormatException("The calendar could not be parsed.");
        var ev = calendar.Events.Single();
        var recurrenceId = ev.RecurrenceIdentifier?.StartTime;
        var start = ev.DtStart ?? recurrenceId;
        if (start is null && !cancelled) throw new CalendarFormatException("Invalid DTSTART.");
        if (ev.Summary?.Length > 65536 || ev.Description?.Length > 262144 || ev.Location?.Length > 65536)
            throw new CalendarFormatException("Calendar text exceeds the supported length limit.");
        foreach (var dt in new[] { start, ev.DtEnd, recurrenceId, ev.LastModified, ev.DtStamp }.Where(x => x is not null))
            ValidateTime(dt!);
        if (ev.Sequence < 0) throw new CalendarFormatException("SEQUENCE cannot be negative.");
        var recurrenceTimes = ev.RecurrenceDates.GetAllDates().Concat(ev.ExceptionDates.GetAllDates())
            .Concat(ev.RecurrenceDates.GetAllPeriods().SelectMany(p => new[] { p.StartTime, p.EffectiveEndTime }.OfType<CalDateTime>())).ToArray();
        if (recurrenceTimes.Length > 10000) throw new CalendarFormatException("Recurrence dates exceed the 10,000 value safety limit.");
        foreach (var dt in recurrenceTimes)
        {
            ValidateTime(dt);
            if (start is not null && (dt.HasTime != start.HasTime || dt.HasTime && dt.IsFloating != start.IsFloating))
                throw new CalendarFormatException("Recurrence dates must use compatible date-only, floating or timezone-aware semantics.");
        }
        if (start is not null && ev.DtEnd is { } end)
        {
            if (start.HasTime != end.HasTime || start.IsFloating != end.IsFloating || end < start || !start.HasTime && end == start)
                throw new CalendarFormatException("DTEND must follow DTSTART and use compatible date/time semantics.");
        }
        if (ev.RecurrenceRule is { } rule)
        {
            if (rule.Frequency is not (FrequencyType.Daily or FrequencyType.Weekly or FrequencyType.Monthly or FrequencyType.Yearly))
                throw new CalendarFormatException("Only DAILY, WEEKLY, MONTHLY and YEARLY recurrence frequencies are supported.");
            if (rule.Interval is < 1 or > 1000 || rule.Count is < 1 or > 1_000_000 || rule.Count is not null && rule.Until is not null)
                throw new CalendarFormatException("Invalid or excessive recurrence interval, COUNT, or COUNT/UNTIL combination.");
            if (rule.BySecond.Count > 0 || rule.ByMinute.Count > 0 || rule.ByHour.Count > 0)
                throw new CalendarFormatException("RRULE BYSECOND/BYMINUTE/BYHOUR expansion is not supported in this iteration.");
            if (rule.Until is { } until && start is not null && (until.HasTime != start.HasTime ||
                start.HasTime && !start.IsFloating && !until.IsUtc || start.HasTime && start.IsFloating && !until.IsFloating))
                throw new CalendarFormatException("UNTIL must match the DTSTART date type and use UTC for timezone-aware events.");
            if (new[] { rule.ByDay.Count, rule.ByMonthDay.Count, rule.ByYearDay.Count, rule.ByWeekNo.Count, rule.ByMonth.Count, rule.BySetPosition.Count }.Any(n => n > 128))
                throw new CalendarFormatException("Recurrence selector exceeds the safety limit.");
            if (rule.ByMonth.Any(n => n is < 1 or > 12) || rule.ByMonthDay.Any(n => n == 0 || Math.Abs((long)n) > 31) ||
                rule.ByYearDay.Any(n => n == 0 || Math.Abs((long)n) > 366) || rule.ByWeekNo.Any(n => n == 0 || Math.Abs((long)n) > 53) ||
                rule.BySetPosition.Any(n => n == 0 || Math.Abs((long)n) > 366))
                throw new CalendarFormatException("Recurrence selector contains an out-of-range value.");
        }
        if (Regex.Matches(raw, @"(?im)^RRULE[:;]").Count > 1) throw new CalendarFormatException("Multiple RRULE properties are unsupported.");
        var recurring = ev.RecurrenceRule is not null || Regex.IsMatch(raw, @"(?im)^RDATE[:;]");
        if (recurrenceId is not null && recurring) throw new CalendarFormatException("Recurrence rules on an override are unsupported; use a new series UID.");
        if (start is not null && ev.End is { } effectiveEnd && effectiveEnd < start)
            throw new CalendarFormatException("Calendar duration cannot be negative.");
        var modified = PropertyValue(raw, "LAST-MODIFIED") is not null ? Instant(ev.LastModified) :
            PropertyValue(raw, "DTSTAMP") is not null ? Instant(ev.DtStamp) : null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ics)));
        return new(uid, Identity(recurrenceId), ics, ev.Summary, ev.Description, ev.Location,
            start?.Value ?? DateTime.MinValue, ev.DtEnd?.Value ?? (ev.Duration is not null ? ev.End?.Value : null),
            start?.TzId, start?.HasTime == false, start?.HasTime == true && start.IsFloating,
            recurring, cancelled, ev.Sequence, modified, hash);
    }

    internal static void ValidateTime(CalDateTime value)
    {
        if (!value.HasTime || value.IsFloating || value.IsUtc) return;
        if (TimeZoneResolvers.TimeZoneResolver(value.TzId!) is null)
            throw new CalendarFormatException($"Unrecognized timezone '{value.TzId}'. Custom VTIMEZONE rules are preserved but cannot be evaluated safely.");
        _ = value.AsUtc;
    }
    internal static DateTimeOffset? Instant(CalDateTime? value) => value is null || !value.HasTime || value.IsFloating ? null : new(value.AsUtc, TimeSpan.Zero);
    internal static string Identity(CalDateTime? value) => value is null ? "" : !value.HasTime ? "D:" + value.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) :
        value.IsFloating ? "F:" + value.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) : "U:" + value.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string? PropertyValue(string text, string property)
    {
        var match = Regex.Match(text, $@"(?im)^{property}(?:;[^:\r\n]*)?:(.*)$");
        return match.Success ? match.Groups[1].Value.TrimEnd('\r') : null;
    }

    private sealed record CalendarBlocks(string Header, List<string> Events, int OtherComponents);
    private static CalendarBlocks SplitCalendar(string text, CancellationToken ct)
    {
        var unfolded = Regex.Replace(text.TrimStart('\uFEFF'), @"\r?\n[ \t]", "");
        var lines = unfolded.Replace("\r\n", "\n").Split('\n');
        var stack = new Stack<string>();
        var header = new StringBuilder(); var block = new StringBuilder();
        var events = new List<string>(); int calendars = 0, others = 0;
        string? top = null;
        foreach (var original in lines)
        {
            ct.ThrowIfCancellationRequested();
            var line = original.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length > 1_048_576) throw new CalendarFormatException("Calendar property exceeds the line length limit.");
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var kind = line[6..].ToUpperInvariant();
                if (stack.Count == 0 && (kind != "VCALENDAR" || ++calendars != 1)) throw new CalendarFormatException("Expected one VCALENDAR document.");
                if (stack.Count == 1) { top = kind; block.Clear(); }
                stack.Push(kind);
                if (stack.Count > 32) throw new CalendarFormatException("Calendar nesting exceeds the safety limit.");
            }
            if (stack.Count == 0) throw new CalendarFormatException("Content outside VCALENDAR.");
            if (top is not null) block.Append(line).Append("\r\n");
            else if (!line.Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase)) header.Append(line).Append("\r\n");
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Pop() != line[4..].ToUpperInvariant()) throw new CalendarFormatException("Mismatched calendar component boundaries.");
                if (stack.Count == 1 && top is not null)
                {
                    if (top == "VEVENT") events.Add(block.ToString());
                    else if (top == "VTIMEZONE") header.Append(block);
                    else others++;
                    if (events.Count > 10000) throw new InvalidDataException("Calendar exceeds the component limit.");
                    top = null;
                }
            }
        }
        if (calendars != 1 || stack.Count != 0 || PropertyValue(header.ToString(), "VERSION") != "2.0")
            throw new CalendarFormatException("Incomplete calendar or unsupported VERSION; expected VCALENDAR version 2.0.");
        return new(header.ToString(), events, others);
    }
}
