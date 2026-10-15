namespace Relliga.Domain;

public sealed record DigitalEvent(
    DateTimeOffset EndedAt, string? Title, string? Artist, string? Album,
    long DurationMs, string Kind, string? Uri, string MetadataJson);

public sealed record StoredEvent(long Id, DigitalEvent Event, string Provider, string SourceFile,
    int RecordIndex, string ImporterVersion, DateTimeOffset ImportedAt);

public sealed record ImportRecord(DigitalEvent? Event, string SourceFile, int RecordIndex,
    string? Fingerprint = null, string? Warning = null);
public sealed record ImportProgress(int Records, string Message);
public sealed record ImportResult(long OperationId, int Added, int Duplicates, int Warnings, string Summary);
public sealed record ImportOperation(long Id, string FileName, string Status, DateTimeOffset StartedAt,
    int Added, int Duplicates, int Warnings, string? Message);
public sealed record LibrarySummary(long Events, int Sources, ImportOperation? LatestImport);
public sealed record EventQuery(string? Text = null, DateTimeOffset? From = null,
    DateTimeOffset? Until = null, int Page = 0, int PageSize = 50);
public sealed record EventPage(IReadOnlyList<StoredEvent> Items, long Total);
