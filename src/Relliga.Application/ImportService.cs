using Relliga.Domain;

namespace Relliga.Application;

public sealed class ImportService(IHistoryStore store, IEnumerable<IDataImporter> importers)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ImportResult> ImportAsync(string path, string provider,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another import is already in progress.");
        try
        {
            var importer = importers.Single(x => x.Provider == provider);
            var id = await store.BeginImportAsync(Path.GetFileName(path), provider, importer.Version, ct);
            try
            {
                return await store.CommitImportAsync(id, provider, importer.ReadAsync(path, progress, ct), ct);
            }
            catch (Exception ex)
            {
                try
                {
                    await store.FinishUnsuccessfulAsync(id, ex is OperationCanceledException ? "Cancelled" : "Failed",
                        ex is OperationCanceledException ? "Import cancelled. No events were saved." : ex.Message);
                }
                catch (Exception statusError)
                {
                    throw new AggregateException("Import failed and its status could not be saved. The library may be unavailable.", ex, statusError);
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
