using Relliga.Domain;

namespace Relliga.Application;

/// <summary>Serializes library work, including multi-call imports and restore activation.</summary>
public sealed class LibraryActivity
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<bool> entered = new();
    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        if (entered.Value) return await operation();
        await gate.WaitAsync(ct);
        try { entered.Value = true; return await operation(); }
        finally { entered.Value = false; gate.Release(); }
    }
    public Task RunAsync(Func<Task> operation, CancellationToken ct = default) =>
        RunAsync(async () => { await operation(); return true; }, ct);
}

public interface ILibraryActivityOwner { LibraryActivity Activity { get; } }
public enum ExportFormat { Csv, Json }
public sealed record OperationProgress(long Records, string Message);
public sealed record ExportRequest(string Destination, ExportFormat Format, EventQuery Query, bool Overwrite = false);
public sealed record ExportResult(long Records, string Destination);
public interface IDataExportService
{
    Task<ExportResult> ExportAsync(ExportRequest request, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed record DeletionTarget(string? Provider = null, long? ImportId = null);
public sealed record DeletionPreview(DeletionTarget Target, long RemovedRecords, long SharedRecords,
    long RevisedComponents, long RemovedImports, string Revision, string Explanation);
public interface ILibraryManagement
{
    Task<DeletionPreview> PreviewDeletionAsync(DeletionTarget target, CancellationToken ct = default);
    Task DeleteAsync(DeletionPreview confirmedPreview, CancellationToken ct = default);
}
public sealed record HealthReport(IReadOnlyList<string> Problems)
{
    public bool Healthy => Problems.Count == 0;
    public string Description => Healthy ? "SQLite, schema, foreign keys, provenance and both search indexes passed. No managed original files are stored."
        : string.Join('\n', Problems);
}
public interface ILibraryHealth { Task<HealthReport> VerifyAsync(CancellationToken ct = default); }
public sealed record BackupManifest(int FormatVersion, int SchemaVersion, string AppVersion, DateTimeOffset CreatedAt,
    string DatabaseSha256, long DatabaseBytes, long PlaybackRecords, long CalendarComponents, long Imports,
    string[] Sources);
public sealed record BackupPreview(string Path, string ArchiveSha256, BackupManifest Manifest)
{
    public string Description => $"Backup from {Manifest.CreatedAt:u} · Relliga {Manifest.AppVersion}\n" +
        $"{Manifest.PlaybackRecords:N0} playback records · {Manifest.CalendarComponents:N0} Calendar definitions · {Manifest.Imports:N0} imports\n" +
        $"Sources: {string.Join(", ", Manifest.Sources)}\nReplaces the entire current library. External original files are excluded. The previous library is retained as a local recovery copy. Backups and recovery copies are unencrypted.";
}
public interface ILibraryBackup
{
    Task CreateAsync(string destination, bool overwrite = false, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<BackupPreview> InspectAsync(string path, CancellationToken ct = default);
    Task RestoreAsync(BackupPreview confirmedPreview, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task DiscardRecoveryAsync(CancellationToken ct = default);
}
