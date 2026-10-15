using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class ScalabilityTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library", "relliga.db");
    public async ValueTask InitializeAsync() { store = new(Database, new CalendarExpander()); await store.InitializeAsync(); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }

    [Fact] public async Task EqualTimestampPagingIsDeterministicAcrossEveryPageAndRestart()
    {
        var importer = new ExportImporter(); var imports = new ImportService(store, [importer]);
        var json = "[" + string.Join(',', Enumerable.Range(0, 1100).Select(i => TestFiles.Row("Synthetic " + i))) + "]";
        await imports.ImportAsync(files.Write(json), "Automatic");
        var identifiers = new List<long>();
        for (int page = 0; page < 22; page++)
        {
            var result = await store.QueryAsync(new(Provider: "Spotify", Page: page)); Assert.Equal(1100, result.Total);
            identifiers.AddRange(result.Items.Select(x => x.Id));
        }
        Assert.Equal(1100, identifiers.Distinct().Count()); Assert.Equal(identifiers.OrderDescending(), identifiers);
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync();
        Assert.Equal(identifiers.TakeLast(50), (await store.QueryAsync(new(Provider: "Spotify", Page: 21))).Items.Select(x => x.Id));
    }

    [Fact] public async Task ActualPlaybackPredicatesUseDateIndexAndFtsRowidLookup()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); await connection.OpenAsync();
        async Task<string> Explain(EventQuery query, string? text)
        {
            var (where, parameters) = SqliteHistoryStore.PlaybackFilter(query, text);
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN SELECT e.id FROM " + SqliteHistoryStore.PlaybackFrom(text) + " " + where + " ORDER BY e.ended_at DESC,e.id DESC LIMIT 50;";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            var details = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            return string.Join('\n', details);
        }
        var date = await Explain(new(Provider: "Spotify", From: DateTimeOffset.UnixEpoch), null);
        Assert.Contains("SEARCH e USING COVERING INDEX ix_events_provider_date", date);
        var search = await Explain(new(Provider: "Spotify"), "\"Quasar\"*");
        Assert.Contains("SEARCH e USING INTEGER PRIMARY KEY", search); Assert.Contains("VIRTUAL TABLE INDEX", search);
        using var origins = connection.CreateCommand(); origins.CommandText = "EXPLAIN QUERY PLAN SELECT operation_id FROM event_origins WHERE event_id=1;";
        await using var originReader = await origins.ExecuteReaderAsync(); Assert.True(await originReader.ReadAsync()); Assert.Contains("SEARCH event_origins", originReader.GetString(3));
    }

    [Fact] public async Task SourceDateKindAndFtsFiltersStillAgreeAfterQuerySpecialization()
    {
        var importer = new ExportImporter(); var imports = new ImportService(store, [importer]);
        await imports.ImportAsync(files.Write("[" + TestFiles.Row("Quasar") + "," + TestFiles.Row("Other", "2025-02-01T12:00:00Z") + "]"), "Automatic");
        var query = new EventQuery("Quasar", new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2025, 2, 1, 0, 0, 0, TimeSpan.Zero), Provider: "Spotify", Kind: "Listening");
        Assert.Single((await store.QueryAsync(query)).Items); Assert.Empty((await store.QueryAsync(query with { Kind = "Calendar" })).Items);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryAsync(new(), cancel.Token));
    }
}
