using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore : ILibraryBackup
{
    private const long MaxBackupBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 65536;
    private string RecoveryPath => DatabasePath + ".previous";
    private string JournalPath => DatabasePath + ".restore-journal";
    internal string StagingPrefix => ".relliga-stage-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? DatabasePath.ToUpperInvariant() : DatabasePath)))[..12].ToLowerInvariant() + "-";
    internal Action<string>? MaintenanceFault { get; set; }
    private bool activatingRestore;
    private sealed record RestoreJournal(int Version, string PreviousSha256, string CandidateSha256);

    public Task CreateAsync(string destination, bool overwrite = false, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Activity.RunAsync(async () =>
        {
            EnsureExternalDestination(destination);
            using var output = new AtomicOutput(destination, overwrite);
            using var staging = new StagingDirectory(Path.GetDirectoryName(DatabasePath)!, StagingPrefix);
            var snapshot = Path.Combine(staging.Path, "library.db");
            progress?.Report(new(0, "Creating a consistent SQLite snapshot…"));
            ct.ThrowIfCancellationRequested();
            await using (var source = await OpenAsync(ct))
            await using (var target = new SqliteConnection($"Data Source={snapshot};Pooling=False"))
            {
                await target.OpenAsync(ct); source.BackupDatabase(target);
                ct.ThrowIfCancellationRequested();
                await ExecuteAsync(target, null, "PRAGMA journal_mode=DELETE;", ct);
                var health = await VerifyConnectionAsync(target, ct);
                if (!health.Healthy) throw new InvalidDataException("Cannot back up an unhealthy library: " + health.Description);
            }
            var manifest = await DescribeSnapshotAsync(snapshot, ct);
            if (manifest.DatabaseBytes > MaxBackupBytes) throw new InvalidDataException("The library exceeds the 2 GiB portable backup limit.");
            progress?.Report(new(0, "Writing the backup archive and checksum…"));
            await using (var file = new FileStream(output.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await using (var entry = archive.CreateEntry("manifest.json").Open()) await JsonSerializer.SerializeAsync(entry, manifest, ExportJson, ct);
                await using (var entry = archive.CreateEntry("library.db", CompressionLevel.Fastest).Open())
                await using (var db = File.OpenRead(snapshot)) await db.CopyToAsync(entry, ct);
            }
            progress?.Report(new(0, "Verifying the completed archive…"));
            _ = await InspectBackupCoreAsync(output.Temporary, ct);
            MaintenanceFault?.Invoke("BackupBeforePublish");
            output.Publish(ct);
        }, ct);

    public Task<BackupPreview> InspectAsync(string path, CancellationToken ct = default) =>
        Activity.RunAsync(() => InspectBackupCoreAsync(path, ct), ct);

    private async Task<BackupPreview> InspectBackupCoreAsync(string path, CancellationToken ct)
    {
        using var staging = new StagingDirectory(Path.GetDirectoryName(DatabasePath)!, StagingPrefix);
        await using var input = OpenBackupInput(path);
        var archiveHash = await HashAsync(input, ct); input.Position = 0;
        var manifest = await ReadArchiveAsync(input, Path.Combine(staging.Path, "library.db"), ct);
        return new(Path.GetFullPath(path), archiveHash, manifest);
    }

    private static FileStream OpenBackupInput(string path)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (input.Length is <= 0 or > MaxBackupBytes) { input.Dispose(); throw new InvalidDataException("Backup archive is empty or exceeds 2 GiB."); }
        return input;
    }

    private static async Task<BackupManifest> ReadArchiveAsync(FileStream file, string stagedDatabase, CancellationToken ct)
    {
        // Reject large central directories before ZipArchive materializes their entries. Format v1 has no ZIP comments/ZIP64.
        if (file.Length < 22) throw new InvalidDataException("Truncated backup archive.");
        var footer = new byte[22]; file.Position = file.Length - 22; await file.ReadExactlyAsync(footer, ct);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054b50 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) != 0 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(8)) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(10)) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12)) > 4096 ||
            (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16)) +
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12)) != file.Length - 22 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(20)) != 0)
            throw new InvalidDataException("Unsupported backup ZIP directory, entry count, comment or ZIP64 structure.");
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != 2) throw new InvalidDataException("A Relliga backup must contain exactly manifest.json and library.db.");
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName is not ("manifest.json" or "library.db") || unixType is not (0 or 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length <= 0 ||
                entry.Length > (entry.FullName == "manifest.json" ? MaxManifestBytes : MaxBackupBytes) ||
                entry.Length > Math.Max(1L, entry.CompressedLength) * 500)
                throw new InvalidDataException("Unsafe backup entry, path, link, size or compression ratio.");
        }
        if (archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != 2) throw new InvalidDataException("Duplicate backup entries.");
        BackupManifest manifest;
        await using (var entry = archive.GetEntry("manifest.json")!.Open())
        {
            using var buffer = new MemoryStream(); await CopyBoundedAsync(entry, buffer, MaxManifestBytes, ct);
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() != json.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("Duplicate manifest fields.");
            manifest = json.RootElement.Deserialize<BackupManifest>(ExportJson) ?? throw new InvalidDataException("Missing backup manifest.");
        }
        if (manifest.FormatVersion != 1 || manifest.SchemaVersion is < 1 or > CurrentSchemaVersion || manifest.AppVersion is null ||
            !Version.TryParse(manifest.AppVersion, out var appVersion) || appVersion.Major > 0 || appVersion.Minor > Version.Parse(AppInfo.Version).Minor ||
            manifest.DatabaseBytes is <= 0 or > MaxBackupBytes || manifest.DatabaseSha256?.Length != 64 ||
            !manifest.DatabaseSha256.All(Uri.IsHexDigit) || manifest.Sources is null || manifest.Sources.Length > 10 ||
            manifest.Sources.Any(s => s is not ("Spotify" or "Google Calendar" or "Telegram")) || manifest.CreatedAt == default)
            throw new InvalidDataException("Unsupported or invalid backup manifest. Update Relliga for newer formats or schemas.");
        await using (var entry = archive.GetEntry("library.db")!.Open())
        await using (var db = new FileStream(stagedDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await CopyBoundedAsync(entry, db, manifest.DatabaseBytes, ct);
        if (new FileInfo(stagedDatabase).Length != manifest.DatabaseBytes || await HashFileAsync(stagedDatabase, ct) != manifest.DatabaseSha256)
            throw new InvalidDataException("The backup database size or SHA-256 checksum does not match its manifest.");
        await using (var c = new SqliteConnection($"Data Source={stagedDatabase};Pooling=False"))
        {
            await c.OpenAsync(ct);
            var version = Convert.ToInt32(await ScalarAsync(c, null, "PRAGMA user_version;", ct));
            if (version != manifest.SchemaVersion) throw new InvalidDataException("Database and manifest schema versions disagree.");
            await ValidateSchemaAsync(c, version, ct); // Inspect schema before allowing migration SQL to run.
        }
        // Migrations operate only on this disposable staged copy, never on the active library.
        using (var staged = new SqliteHistoryStore(stagedDatabase))
        {
            await staged.InitializeAsync(ct);
            var health = await staged.VerifyAsync(ct);
            if (!health.Healthy) throw new InvalidDataException("Backup database verification failed: " + health.Description);
        }
        var actual = await DescribeSnapshotAsync(stagedDatabase, ct);
        if (manifest.PlaybackRecords != actual.PlaybackRecords || manifest.CalendarComponents != actual.CalendarComponents ||
            manifest.Imports != actual.Imports || !manifest.Sources.SequenceEqual(actual.Sources))
            throw new InvalidDataException("Backup manifest counts or sources disagree with the database.");
        // Close/checkpoint all staged connections before activation; SQLite backup below strips any WAL sidecars.
        return manifest;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[65536]; long read = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if ((read += count) > limit) throw new InvalidDataException("Expanded backup data exceeds its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    private static async Task<BackupManifest> DescribeSnapshotAsync(string path, CancellationToken ct)
    {
        await using var c = new SqliteConnection($"Data Source={path};Pooling=False"); await c.OpenAsync(ct);
        var sources = new List<string>(); using (var cmd = Command(c, null, "SELECT provider FROM data_sources ORDER BY provider;"))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) sources.Add(r.GetString(0));
        var schema = Convert.ToInt32(await ScalarAsync(c, null, "PRAGMA user_version;", ct));
        var playback = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM events;", ct));
        var calendar = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM calendar_components;", ct));
        var imports = Convert.ToInt64(await ScalarAsync(c, null, "SELECT COUNT(*) FROM import_operations;", ct));
        await c.CloseAsync();
        return new(1, schema, AppInfo.Version, DateTimeOffset.UtcNow, await HashFileAsync(path, ct), new FileInfo(path).Length,
            playback, calendar, imports, sources.ToArray());
    }

    public Task RestoreAsync(BackupPreview confirmedPreview, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Activity.RunAsync(async () =>
        {
            if (ownership is null) throw new InvalidOperationException("Open the current library before restoring a backup.");
            using var staging = new StagingDirectory(Path.GetDirectoryName(DatabasePath)!, StagingPrefix);
            var staged = Path.Combine(staging.Path, "library.db");
            progress?.Report(new(0, "Revalidating and staging the selected backup…"));
            await using (var input = OpenBackupInput(confirmedPreview.Path))
            {
                if (await HashAsync(input, ct) != confirmedPreview.ArchiveSha256) throw new InvalidDataException("The backup changed after its preview. Select and review it again.");
                input.Position = 0; _ = await ReadArchiveAsync(input, staged, ct);
            }
            // Consolidate a migrated staged database into one standalone file.
            var candidate = Path.Combine(staging.Path, "candidate.db");
            await using (var source = new SqliteConnection($"Data Source={staged};Pooling=False"))
            await using (var target = new SqliteConnection($"Data Source={candidate};Pooling=False"))
            { await source.OpenAsync(ct); await target.OpenAsync(ct); source.BackupDatabase(target); await ExecuteAsync(target, null, "PRAGMA journal_mode=DELETE;", ct); }
            // Never reactivate live workers from a portable backup. Retain deletion knowledge from this device.
            await using (var candidateConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = candidate, Pooling = false, ForeignKeys = true }.ToString()))
            {
                await candidateConnection.OpenAsync(ct);
                await ExecuteAsync(candidateConnection, null, "ATTACH DATABASE $path AS active_library;", ct, ("$path", DatabasePath));
                using (var candidateTx = candidateConnection.BeginTransaction())
                {
                    await ExecuteAsync(candidateConnection, candidateTx, """
                        INSERT INTO telegram_deletions SELECT * FROM active_library.telegram_deletions WHERE 1 ON CONFLICT DO NOTHING;
                        DELETE FROM event_origins WHERE event_id IN (SELECT event_id FROM telegram_messages m JOIN telegram_deletions d ON d.account_id=m.account_id AND d.chat_id=m.chat_id AND d.message_id=m.message_id);
                        DELETE FROM events WHERE id IN (SELECT event_id FROM telegram_messages m JOIN telegram_deletions d ON d.account_id=m.account_id AND d.chat_id=m.chat_id AND d.message_id=m.message_id);
                        UPDATE connections SET automatic=0,state='Paused';
                        DELETE FROM sync_checkpoints;
                        """, ct);
                    await CleanConversationMetadataAsync(candidateConnection, candidateTx, ct); candidateTx.Commit();
                }
                await ExecuteAsync(candidateConnection, null, "DETACH DATABASE active_library;", ct);
                var health = await VerifyConnectionAsync(candidateConnection, ct);
                if (!health.Healthy) throw new InvalidDataException("Restored conversation validation failed: " + health.Description);
            }
            ct.ThrowIfCancellationRequested(); MaintenanceFault?.Invoke("RestoreBeforeActivation");
            progress?.Report(new(0, "Activating the verified library. Cancellation is paused while recovery is secured…"));
            // Every library connection is closed and pooled connections are disabled. The ownership lock stays held.
            await using (var current = await OpenAsync(ct))
            {
                await ExecuteAsync(current, null, "PRAGMA wal_checkpoint(TRUNCATE);", ct);
                var mode = await ScalarAsync(current, null, "PRAGMA journal_mode=DELETE;", ct);
                if (!string.Equals(mode?.ToString(), "delete", StringComparison.OrdinalIgnoreCase)) throw new IOException("Could not close the active SQLite WAL safely.");
            }
            ct.ThrowIfCancellationRequested();
            var journal = new RestoreJournal(1, await HashFileAsync(DatabasePath, ct), await HashFileAsync(candidate, ct));
            using (var flush = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
            using (var journalOutput = new AtomicOutput(JournalPath, false))
            {
                await File.WriteAllTextAsync(journalOutput.Temporary, JsonSerializer.Serialize(journal), ct);
                journalOutput.Publish(ct);
            }
            activatingRestore = true;
            try
            {
                MaintenanceFault?.Invoke("RestoreJournalWritten");
                File.Replace(candidate, DatabasePath, RecoveryPath); // Same filesystem; unsupported filesystems fail safely.
                MaintenanceFault?.Invoke("RestoreReplaced");
                var health = await VerifyAsync(CancellationToken.None);
                if (!health.Healthy) throw new InvalidDataException("Replacement validation failed: " + health.Description);
                MaintenanceFault?.Invoke("RestoreValidated");
                File.Delete(JournalPath); // Commit point. A surviving journal always means rollback on next startup.
            }
            catch (Exception failure)
            {
                try { await RecoverRestoreAsync(CancellationToken.None); }
                catch (Exception recovery) { throw new AggregateException("Restore failed. Recovery is pending; close and reopen Relliga. Keep the .previous and .restore-journal files.", failure, recovery); }
                throw;
            }
            finally { activatingRestore = false; }
        }, ct);

    private async Task RecoverRestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(JournalPath)) return;
        if (new FileInfo(JournalPath).Length > 4096) throw new InvalidDataException("Invalid restore recovery journal. Preserve the library and recovery files for manual recovery.");
        var journal = JsonSerializer.Deserialize<RestoreJournal>(await File.ReadAllTextAsync(JournalPath, ct))
            ?? throw new InvalidDataException("Unreadable restore recovery journal.");
        if (journal.Version != 1 || journal.PreviousSha256 is not { Length: 64 } || journal.CandidateSha256 is not { Length: 64 } ||
            !journal.PreviousSha256.All(Uri.IsHexDigit) || !journal.CandidateSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Unsupported recovery journal. No library files were changed.");
        var activeHash = File.Exists(DatabasePath) ? await HashFileAsync(DatabasePath, ct) : null;
        if (activeHash is not null && activeHash != journal.PreviousSha256 && activeHash != journal.CandidateSha256)
            throw new IOException("Restore recovery found an unexpected active database. Both libraries were preserved. Keep all recovery files for manual inspection.");
        if (File.Exists(RecoveryPath) && await HashFileAsync(RecoveryPath, ct) == journal.PreviousSha256)
        {
            // Candidate never enters WAL before the journal is removed; no uncheckpointed replacement can exist.
            if (File.Exists(DatabasePath)) File.Replace(RecoveryPath, DatabasePath, null);
            else File.Move(RecoveryPath, DatabasePath);
        }
        else if (activeHash != journal.PreviousSha256)
            throw new IOException("Automatic restore recovery cannot identify the previous library. Preserve all files; no library was replaced.");
        File.Delete(JournalPath);
    }

    public Task DiscardRecoveryAsync(CancellationToken ct = default) => Activity.RunAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(JournalPath)) throw new InvalidOperationException("Pending restore recovery must finish before discarding its copy.");
        if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        return Task.CompletedTask;
    }, ct);
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    { await using var file = File.OpenRead(path); return await HashAsync(file, ct); }
    private static async Task<string> HashAsync(Stream stream, CancellationToken ct) => Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

    private sealed class StagingDirectory : IDisposable
    {
        public string Path { get; }
        public StagingDirectory(string parent, string prefix)
        {
            Path = System.IO.Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => DeleteStaging(Path);
    }

    private static void CleanAbandonedStaging(string parent, string prefix)
    {
        foreach (var path in Directory.EnumerateDirectories(parent, prefix + "*"))
            if (System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path), "^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "[a-f0-9]{32}$")) DeleteStaging(path);
    }
    private static void DeleteStaging(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to follow a link in library staging.");
        var entries = Directory.GetFileSystemEntries(path);
        foreach (var entry in entries)
        {
            if (System.IO.Path.GetFileName(entry) is not ("library.db" or "library.db-wal" or "library.db-shm" or "library.db.lock" or "library.db-journal" or "candidate.db" or "candidate.db-journal" or "candidate.db-wal" or "candidate.db-shm") ||
                (File.GetAttributes(entry) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Unexpected staging contents were preserved. Review " + path);
        }
        foreach (var entry in entries) File.Delete(entry);
        Directory.Delete(path);
    }
}
