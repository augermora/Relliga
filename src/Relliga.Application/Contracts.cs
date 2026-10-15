using Relliga.Domain;

namespace Relliga.Application;

public interface IDataImporter
{
    string Provider { get; }
    string Version { get; }
    IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IHistoryStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<long> BeginImportAsync(string fileName, string provider, string version, CancellationToken ct);
    Task<ImportResult> CommitImportAsync(long operationId, string provider,
        IAsyncEnumerable<ImportRecord> records, CancellationToken ct);
    Task FinishUnsuccessfulAsync(long operationId, string status, string message);
    Task<LibrarySummary> GetSummaryAsync(CancellationToken ct = default);
    Task<EventPage> QueryAsync(EventQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ImportOperation>> GetImportsAsync(CancellationToken ct = default);
}
