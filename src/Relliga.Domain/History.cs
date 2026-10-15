namespace Relliga.Domain;

public sealed record DigitalEvent(
    DateTimeOffset? EndedAt, string? Title, string? Artist, string? Album,
    long? DurationMs, string Kind, string? Uri, string MetadataJson);

public sealed record StoredEvent(long Id, DigitalEvent Event, string Provider, string SourceFile,
    int RecordIndex, string ImporterVersion, DateTimeOffset ImportedAt, CalendarOccurrence? Calendar = null);

public sealed record ImportRecord(DigitalEvent? Event, string SourceFile, int RecordIndex,
    string? Fingerprint = null, string? Warning = null, CalendarDefinition? Calendar = null,
    string? Provider = null, string? ImporterVersion = null, bool Rejected = true, string? StableFingerprint = null, bool IsFileError = false);
public sealed record ImportProgress(int Records, string Message);
public sealed record ImportResult(long OperationId, int Added, int Duplicates, int Warnings, string Summary, int Updated = 0, int Processed = 0, int Rejected = 0);
public sealed record ImportOperation(long Id, string FileName, string Status, DateTimeOffset StartedAt,
    int Added, int Duplicates, int Warnings, string? Message, string Provider = "", string ImporterVersion = "",
    int? Processed = 0, int? Rejected = 0, int Updated = 0);
public sealed record SourceStatistics(string Provider, long Events, long DurationMs = 0, long RecurringDefinitions = 0);
public sealed record LibrarySummary(long Events, int Sources, ImportOperation? LatestImport,
    int SuccessfulImports = 0, IReadOnlyList<SourceStatistics>? BySource = null, long TotalImports = 0);
public sealed record EventQuery(string? Text = null, DateTimeOffset? From = null,
    DateTimeOffset? Until = null, int Page = 0, int PageSize = 50, string? Provider = null, string? Kind = null);
public sealed record EventPage(IReadOnlyList<StoredEvent> Items, long Total, DateTimeOffset? WindowFrom = null, DateTimeOffset? WindowUntil = null);
