using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private ImportService service = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library.db");
    public async ValueTask InitializeAsync()
    {
        store = new(Database); await store.InitializeAsync();
        service = new(store, [new SpotifyImporter()]);
    }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task<ImportResult> Import(string json, string name = "input.json") => service.ImportAsync(files.Write(json, name), "Spotify");

    [Fact] public async Task PersistsAcrossReopenAndRetainsProvenance()
    {
        await Import($"[{TestFiles.Row()}]"); store.Dispose(); store = new(Database); await store.InitializeAsync();
        var page = await store.QueryAsync(new()); var ev = Assert.Single(page.Items);
        Assert.Equal("input.json", ev.SourceFile); Assert.Equal("1.0.0", ev.ImporterVersion);
        Assert.Equal("Spotify", ev.Provider); Assert.Equal(1, ev.RecordIndex);
        Assert.Equal(1, (await store.GetSummaryAsync()).Sources);
    }

    [Fact] public async Task ReimportDeduplicatesButPreservesIdenticalOccurrencesAndDistinctMetadata()
    {
        var row = TestFiles.Row(); var other = TestFiles.Row(platform: "other-synthetic-device");
        Assert.Equal(3, (await Import($"[{row},{row},{other}]")).Added);
        var repeated = await Import($"[{row},{row},{other}]", "renamed.json");
        Assert.Equal(0, repeated.Added); Assert.Equal(3, repeated.Duplicates);
        Assert.Equal(3, (await store.GetSummaryAsync()).Events);
        Assert.Equal(1, (await Import($"[{row},{row},{row},{other}]")).Added);
    }

    [Fact] public async Task ArchiveAndStandaloneShareEventIdentity()
    {
        await Import($"[{TestFiles.Row()}]");
        var result = await service.ImportAsync(files.Zip(("renamed.json", $"[{TestFiles.Row()}]")), "Spotify");
        Assert.Equal(1, result.Duplicates); Assert.Equal(0, result.Added);
    }

    [Fact] public async Task FtsSearchDateFiltersPagingAndSqlSyntaxAreSafe()
    {
        await Import($"[{TestFiles.Row("Aurora", "2024-12-31T23:00:00Z")},{TestFiles.Row("Nebula")},{TestFiles.Row("Nebulous", "2025-02-01T00:00:00Z")}]");
        var search = await store.QueryAsync(new("nebu", PageSize: 1)); Assert.Equal(2, search.Total); Assert.Single(search.Items);
        var next = await store.QueryAsync(new("nebu", Page: 1, PageSize: 1)); Assert.NotEqual(search.Items[0].Id, next.Items[0].Id);
        Assert.Equal(3, (await store.QueryAsync(new("synthetic artist"))).Total);
        Assert.Empty((await store.QueryAsync(new("\"; DROP TABLE events;--"))).Items);
        var january = await store.QueryAsync(new(From: new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Until: new(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Nebula", Assert.Single(january.Items).Event.Title);
        Assert.Equal(3, (await store.GetSummaryAsync()).Events);
    }

    [Fact] public async Task MalformedRecordsProducePersistedWarnings()
    {
        var result = await Import($"[null,{TestFiles.Row()}]");
        Assert.Equal(1, result.Warnings); Assert.Equal(1, result.Added);
        var op = Assert.Single(await store.GetImportsAsync());
        Assert.Equal("Completed with warnings", op.Status); Assert.Contains("record 1", op.Message!);
    }

    [Fact] public async Task SearchHandlesUnicodeAndDiacritics()
    {
        await Import($"[{TestFiles.Row("Étoile 星")}]");
        Assert.Single((await store.QueryAsync(new("etoil"))).Items);
        Assert.Single((await store.QueryAsync(new("星"))).Items);
    }

    [Fact] public async Task SyntaxErrorRollsBackAllEventsAndKeepsFailedOperation()
    {
        // Large padding ensures the streaming parser yields the first record before encountering invalid syntax.
        var input = "[" + TestFiles.Row() + "," + new string(' ', 40000) + "broken]";
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Import(input));
        Assert.Equal(0, (await store.GetSummaryAsync()).Events);
        Assert.Equal(0, (await store.GetSummaryAsync()).Sources);
        Assert.Equal("Failed", Assert.Single(await store.GetImportsAsync()).Status);
        Assert.Equal(1, (await Import($"[{TestFiles.Row()}]")).Added);
    }

    [Fact] public async Task FatalFailureAfterInsertRollsBackFtsAndProvenance()
    {
        var importer = new FailingImporter(); var failing = new ImportService(store, [importer]);
        await Assert.ThrowsAsync<IOException>(() => failing.ImportAsync("synthetic", "Test"));
        Assert.Empty((await store.QueryAsync(new("Synthetic"))).Items);
        Assert.Equal("Failed", Assert.Single(await store.GetImportsAsync()).Status);
        using var c = new SqliteConnection($"Data Source={Database};Pooling=False"); await c.OpenAsync();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM event_origins;";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact] public async Task CancellationAfterInsertRollsBackAndCanRetry()
    {
        using var cancel = new CancellationTokenSource();
        var importer = new FailingImporter(cancel); var cancelling = new ImportService(store, [importer]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.ImportAsync("synthetic", "Test", ct: cancel.Token));
        Assert.Equal(0, (await store.GetSummaryAsync()).Events);
        Assert.Equal("Cancelled", Assert.Single(await store.GetImportsAsync()).Status);
        Assert.Equal(1, (await Import($"[{TestFiles.Row()}]")).Added);
    }

    [Fact] public async Task InterruptedOperationIsRecoveredWithoutDeletingExistingEvents()
    {
        await Import($"[{TestFiles.Row()}]");
        await store.BeginImportAsync("synthetic.json", "Spotify", "1.0.0", default);
        store.Dispose(); store = new(Database); await store.InitializeAsync();
        Assert.Equal("Interrupted", (await store.GetImportsAsync())[0].Status);
        Assert.Equal(1, (await store.GetSummaryAsync()).Events);
    }

    [Fact] public async Task SecondInstanceCannotOwnSameDatabase()
    {
        using var other = new SqliteHistoryStore(Database);
        await Assert.ThrowsAsync<IOException>(() => other.InitializeAsync());
    }

    [Fact] public async Task RejectsNewerSchemaWithoutChangingVersion()
    {
        store.Dispose();
        using (var c = new SqliteConnection($"Data Source={Database};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=999;"; cmd.ExecuteNonQuery(); }
        store = new(Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync());
        using var check = new SqliteConnection($"Data Source={Database};Pooling=False");
        await check.OpenAsync();
        using var version = check.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.Equal(999L, await version.ExecuteScalarAsync());
    }

    [Fact] public async Task ConcurrentImportIsRejectedAndFirstImportCompletes()
    {
        var blocker = new BlockingImporter(); var serial = new ImportService(store, [blocker]);
        var first = serial.ImportAsync("first", "Test");
        await blocker.Started.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.ImportAsync("second", "Test"));
        blocker.Release.SetResult();
        Assert.Equal(1, (await first).Added);
    }

    [Fact] public async Task LargerCollectionIsPagedWithoutTruncation()
    {
        var json = "[" + string.Join(',', Enumerable.Range(0, 1000).Select(i =>
            TestFiles.Row($"Synthetic {i}", DateTimeOffset.UnixEpoch.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")))) + "]";
        Assert.Equal(1000, (await Import(json)).Added);
        var first = await store.QueryAsync(new(PageSize: 50));
        var last = await store.QueryAsync(new(Page: 19, PageSize: 50));
        Assert.Equal(1000, first.Total); Assert.Equal(50, first.Items.Count); Assert.Equal(50, last.Items.Count);
        Assert.Equal("Synthetic 999", first.Items[0].Event.Title);
        Assert.Equal("Synthetic 0", last.Items[^1].Event.Title);
        Assert.Equal(1000, (await Import(json)).Duplicates);
    }

    private sealed class FailingImporter(CancellationTokenSource? cancellation = null) : IDataImporter
    {
        public string Provider => "Test"; public string Version => "synthetic";
        public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new(new(DateTimeOffset.UtcNow, "Synthetic", null, null, 1, "Listening", null, "{}"), "synthetic", 1, "synthetic-key");
            await Task.Yield();
            if (cancellation is not null) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new IOException("Synthetic read failure");
        }
    }
    private sealed class BlockingImporter : IDataImporter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => "Test"; public string Version => "synthetic";
        public async IAsyncEnumerable<ImportRecord> ReadAsync(string path, IProgress<ImportProgress>? progress, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.SetResult(); await Release.Task.WaitAsync(ct);
            yield return new(new(DateTimeOffset.UtcNow, "Synthetic", null, null, 1, "Listening", null, "{}"), "synthetic", 1, "test");
        }
    }
}
