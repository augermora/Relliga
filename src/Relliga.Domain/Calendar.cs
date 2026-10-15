namespace Relliga.Domain;

// Raw definitions remain authoritative. Dates and floating clocks have no invented instant.
public sealed record CalendarDefinition(string Uid, string RecurrenceId, string RawIcs,
    string? Title, string? Description, string? Location, DateTime StartLocal, DateTime? EndLocal,
    string? TimeZoneId, bool IsAllDay, bool IsFloating, bool IsRecurring, bool IsCancelled,
    int Sequence, DateTimeOffset? ModifiedAt, string ContentHash);

public sealed record CalendarOccurrence(string Uid, string ComponentRecurrenceId, string OccurrenceId,
    DateTime StartLocal, DateTime? EndLocal, DateTimeOffset? StartInstant, DateTimeOffset? EndInstant,
    string? TimeZoneId, bool IsAllDay, bool IsFloating, string? Title, string? Description, string? Location);
