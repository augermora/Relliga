using Relliga.Domain;
using Microsoft.Data.Sqlite;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Activity.RunAsync(() => InitializeCoreAsync(ct), ct);
    public Task<long> BeginImportAsync(string fileName, string provider, string version, CancellationToken ct) =>
        Activity.RunAsync(() => BeginImportCoreAsync(fileName, provider, version, ct), ct);
    public Task<ImportResult> CommitImportAsync(long operationId, string provider, IAsyncEnumerable<ImportRecord> records, CancellationToken ct) =>
        Activity.RunAsync(() => CommitImportCoreAsync(operationId, provider, records, ct), ct);
    public Task FinishUnsuccessfulAsync(long operationId, string status, string message) =>
        Activity.RunAsync(() => FinishUnsuccessfulCoreAsync(operationId, status, message));
    public Task<LibrarySummary> GetSummaryAsync(CancellationToken ct = default) => Activity.RunAsync(() => GetSummaryCoreAsync(ct), ct);
    public Task<EventPage> QueryAsync(EventQuery query, CancellationToken ct = default) => Activity.RunAsync(() => QueryCoreAsync(query, ct), ct);
    public Task<IReadOnlyList<ImportOperation>> GetImportsAsync(CancellationToken ct = default, int page = 0, int pageSize = 20) =>
        Activity.RunAsync(() => GetImportsCoreAsync(ct, page, pageSize), ct);
    internal Task StreamAsync(EventQuery query, Func<StoredEvent, SqliteConnection, SqliteTransaction, Task> sink, CancellationToken ct, Func<EventQuery, Task>? prepared = null) =>
        Activity.RunAsync(() => QueryCoreAsync(query, ct, sink, prepared), ct);
}
