using System.Text;
using System.Text.Json;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class TelegramTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private string Database => Path.Combine(files.DirectoryPath, "library", "relliga.db");
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-telegram.json"));
    public async ValueTask InitializeAsync() { store = new(Database, new CalendarExpander()); await store.InitializeAsync(); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private Task<ImportResult> Import(string? json = null) => new ImportService(store, [new ExportImporter()]).ImportAsync(files.Write(json ?? Fixture), "Automatic");
    private static ConnectionRecord Connection() => new(Guid.NewGuid().ToString("N"), "Telegram", true, "Connected", AccountId: "user9001");
    private static ConversationMessage Live(string text = "Live revised Aurora", long id = 1) => new("user9001", 8001, id, "Synthetic studio chat", "chatTypePrivate", "user8001", "Synthetic collaborator", text,
        DateTimeOffset.FromUnixTimeSeconds(1735819200), DateTimeOffset.FromUnixTimeSeconds(1735992000), "TDLib", "{\"synthetic\":true}");

    [Fact] public async Task OfficialJsonDetectsContentsNormalizesRichTextAndPersistsAcrossRestart()
    {
        Assert.Equal(2, (await Import()).Added); var row = Assert.Single((await store.QueryAsync(new(Text: "Aurora", Provider: "Telegram"))).Items);
        Assert.Equal("Meet at Aurora studio", row.Event.Title); Assert.Equal("Message", row.Event.Kind); Assert.Null(row.Event.DurationMs);
        var message = JsonSerializer.Deserialize<ConversationMessage>(row.Event.MetadataJson)!;
        Assert.Equal("user9001:8001", message.ConversationKey); Assert.Equal(1, message.MessageId); Assert.Null(message.EditedAt);
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync(); Assert.Equal(2, (await store.GetSummaryAsync()).Events);
        Assert.Equal(2, (await Import()).Duplicates); Assert.True((await store.VerifyAsync()).Healthy);
    }
    [Fact] public async Task JsonAndLiveShareIdentityWithoutLosingOriginsOrNewerEdits()
    {
        await Import(); var connection = Connection(); await store.SaveConnectionAsync(connection);
        await store.ApplySyncPageAsync(new(connection.Id, "history/8001", "head:1048576", [Live()]));
        Assert.Equal(2, (await store.QueryAsync(new(Provider: "Telegram"))).Total);
        Assert.Single((await store.QueryAsync(new(Text: "revised", Provider: "Telegram"))).Items);
        var old = await Import(); Assert.Equal(0, old.Added); Assert.Single((await store.QueryAsync(new(Text: "revised", Provider: "Telegram"))).Items);
        var count = (await store.GetImportsAsync(pageSize: 100)).Count;
        await store.ApplySyncPageAsync(new(connection.Id, "history/8001", "head:1048576", [Live()]));
        Assert.Equal(count, (await store.GetImportsAsync(pageSize: 100)).Count); Assert.True((await store.VerifyAsync()).Healthy);
        var output = Path.Combine(files.DirectoryPath, "export.json"); await store.ExportAsync(new(output, ExportFormat.Json, new(Provider: "Telegram")));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output)); var events = doc.RootElement.GetProperty("events"); Assert.Equal(2, events.GetArrayLength()); Assert.Contains(events.EnumerateArray(), e => e.GetProperty("origins").GetArrayLength() >= 3);
    }
    [Fact] public async Task CheckpointAndMessagesRollbackTogetherOnFaultAndCancellation()
    {
        var connection = Connection(); await store.SaveConnectionAsync(connection);
        store.MaintenanceFault = stage => { if (stage == "SyncBeforeCommit") throw new IOException("Synthetic write fault"); };
        await Assert.ThrowsAsync<IOException>(() => store.ApplySyncPageAsync(new(connection.Id, "history", "cursor", [Live()])));
        Assert.Null(await store.GetCheckpointAsync(connection.Id, "history")); Assert.Empty((await store.QueryAsync(new(Provider: "Telegram"))).Items);
        store.MaintenanceFault = null; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ApplySyncPageAsync(new(connection.Id, "history", "cursor", [Live()]), cancel.Token));
        await store.ApplySyncPageAsync(new(connection.Id, "history", "committed", [Live()]));
        store.Dispose(); store = new(Database, new CalendarExpander()); await store.InitializeAsync(); Assert.Equal("committed", await store.GetCheckpointAsync(connection.Id, "history")); Assert.Single((await store.QueryAsync(new(Provider: "Telegram"))).Items);
    }
    [Fact] public async Task ProviderDeletionPurgesAllOriginsAndSearchAndPreventsOldImportResurrection()
    {
        await Import(); var connection = Connection(); await store.SaveConnectionAsync(connection);
        await store.ApplySyncPageAsync(new(connection.Id, "deletions", null, [], [("user9001", 8001, 1)]));
        Assert.Empty((await store.QueryAsync(new(Text: "Aurora", Provider: "Telegram"))).Items);
        await Import(); Assert.Single((await store.QueryAsync(new(Provider: "Telegram"))).Items); Assert.True((await store.VerifyAsync()).Healthy);
    }
    [Fact] public async Task BackupRestoreKeepsDeletionKnowledgeAndNeverReactivatesAccountWorkers()
    {
        await Import(); var connection = Connection(); await store.SaveConnectionAsync(connection);
        var backup = Path.Combine(files.DirectoryPath, "snapshot.zip"); await store.CreateAsync(backup);
        await store.ApplySyncPageAsync(new(connection.Id, "deletions", null, [], [("user9001", 8001, 1)]));
        await store.RestoreAsync(await store.InspectAsync(backup));
        Assert.Single((await store.QueryAsync(new(Provider: "Telegram"))).Items); Assert.False((await store.GetConnectionAsync("Telegram"))!.Automatic); Assert.True((await store.VerifyAsync()).Healthy);
    }
    [Fact] public async Task BatchDeletionRecomputesSurvivingMessageRevisionAndSourceDeletionCleansConversations()
    {
        await Import(); var connection = Connection(); await store.SaveConnectionAsync(connection); await store.ApplySyncPageAsync(new(connection.Id, "x", "1", [Live()]));
        var latest = (await store.GetImportsAsync()).First(); await store.DeleteAsync(await store.PreviewDeletionAsync(new(ImportId: latest.Id)));
        Assert.Single((await store.QueryAsync(new(Text: "Meet", Provider: "Telegram"))).Items); Assert.True((await store.VerifyAsync()).Healthy);
        await store.DeleteAsync(await store.PreviewDeletionAsync(new(Provider: "Telegram"))); Assert.Empty(await store.GetConversationsAsync()); Assert.True((await store.VerifyAsync()).Healthy);
    }
    [Fact] public async Task ConversationPagingIsDeterministicAndIndexedTextQueryDoesNotMixChats()
    {
        var connection = Connection(); await store.SaveConnectionAsync(connection);
        await store.ApplySyncPageAsync(new(connection.Id, "x", "100", Enumerable.Range(1, 100).Reverse().Select(i => Live("Synthetic page " + i, i)).ToArray()));
        var chat = Assert.Single(await store.GetConversationsAsync("studio")); Assert.Equal(100, chat.Messages);
        var first = await store.QueryAsync(new(Provider: "Telegram", ConversationKey: chat.Key, PageSize: 50)); var second = await store.QueryAsync(new(Provider: "Telegram", ConversationKey: chat.Key, Page: 1, PageSize: 50));
        Assert.Equal(100, first.Total); Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id))); Assert.Empty((await store.QueryAsync(new(Provider: "Telegram", ConversationKey: "user9001:123"))).Items);
        Assert.Equal(100, JsonSerializer.Deserialize<ConversationMessage>(first.Items[0].Event.MetadataJson)!.MessageId);
        Assert.Equal(1, JsonSerializer.Deserialize<ConversationMessage>(second.Items[^1].Event.MetadataJson)!.MessageId);
    }
    [Fact] public async Task UnsafeOversizedAndUnsupportedJsonRollbackAndMixedZipImportsWork()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Import("{}")); Assert.Empty((await store.QueryAsync(new(Provider: "Telegram"))).Items);
        var unsupported = Fixture.Replace("personal_chat", "secret_chat"); await Assert.ThrowsAnyAsync<Exception>(() => Import(unsupported));
        var zip = files.Zip(("arbitrary.data", Fixture), ("music.json", "[" + TestFiles.Row() + "]"));
        Assert.Equal(3, (await new ImportService(store, [new ExportImporter()]).ImportAsync(zip, "Automatic")).Added);
        var oversized = Fixture.Replace("Synthetic project notes", new string('x', 65537)); var result = await Import(oversized); Assert.Equal(1, result.Rejected);
    }
    [Fact] public async Task SingleChatExportsRemainIsolatedAndEphemeralRecordsAreExcluded()
    {
        using var doc = JsonDocument.Parse(Fixture); var single = doc.RootElement.GetProperty("chats").GetProperty("list")[0].GetRawText();
        Assert.Equal(2, (await Import(single)).Added); var messages = (await store.QueryAsync(new(Provider: "Telegram"))).Items;
        Assert.All(messages, m => Assert.StartsWith("unattributed-", JsonSerializer.Deserialize<ConversationMessage>(m.Event.MetadataJson)!.AccountId));
        await Import(); Assert.Equal(4, (await store.QueryAsync(new(Provider: "Telegram"))).Total);
        var ephemeral = Fixture.Replace("\"id\": 1,", "\"id\": 1, \"self_destruct_period_seconds\": 10,"); Assert.Equal(1, (await Import(ephemeral)).Warnings);
    }
    [Fact] public async Task MalformedMessageIsReportedWhileValidMessagesSurviveAndCsvRetainsMessageSemantics()
    {
        var malformed = Fixture.Replace("\"date_unixtime\": \"1735819200\",", "");
        var result = await Import(malformed); Assert.Equal(1,result.Added); Assert.Equal(1,result.Rejected);
        var path = Path.Combine(files.DirectoryPath,"messages.csv"); await store.ExportAsync(new(path,ExportFormat.Csv,new(Provider:"Telegram")));
        var csv = await File.ReadAllTextAsync(path); Assert.Contains("\"Telegram\",\"Message\",\"Synthetic project notes\"",csv);
        Assert.Contains("\"2025-01-03T12:00:00.0000000Z\",\"\",\"\"",csv); Assert.DoesNotContain("synthetic-password",csv);
    }
    [Fact] public async Task WindowsVaultActuallyEncryptsAndReopensSyntheticSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(files.DirectoryPath, "vault"); ICredentialVault vault = new WindowsCredentialVault(directory); var secret = Encoding.UTF8.GetBytes("synthetic-only-session-material");
        await vault.WriteAsync("test", secret); var blob = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory))); Assert.DoesNotContain("synthetic-only-session-material", Encoding.UTF8.GetString(blob));
        Assert.Equal(secret, await new WindowsCredentialVault(directory).ReadAsync("test")); await vault.DeleteAsync("test"); Assert.Null(await vault.ReadAsync("test"));
    }
    [Fact] public async Task EqualRevisionJsonConflictRetainsExistingContentAndAccountNamespacesRemainDistinct()
    {
        await Import(); var conflict = await Import(Fixture.Replace("Synthetic project notes","Conflicting synthetic text"));
        Assert.Equal(1,conflict.Warnings); Assert.Single((await store.QueryAsync(new(Text:"notes",Provider:"Telegram"))).Items);
        await Import(Fixture.Replace("\"user_id\": 9001","\"user_id\": 9002")); Assert.Equal(4,(await store.QueryAsync(new(Provider:"Telegram"))).Total);
    }
    [Fact] public async Task VersionThreeLibraryMigratesWithoutChangingExistingPlaybackIdentity()
    {
        await new ImportService(store,[new SpotifyImporter()]).ImportAsync(files.Write("["+TestFiles.Row()+"]"),"Spotify");
        var original=Assert.Single((await store.QueryAsync(new(Provider:"Spotify"))).Items); store.Dispose();
        await using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Database,Pooling=false}.ToString()))
        {
            await connection.OpenAsync();using var command=connection.CreateCommand();command.CommandText="DROP TABLE telegram_messages; DROP TABLE telegram_chats; DROP TABLE telegram_participants; DROP TABLE telegram_deletions; DROP TABLE sync_checkpoints; DROP TABLE connections; PRAGMA user_version=3;";await command.ExecuteNonQueryAsync();
        }
        store=new(Database,new CalendarExpander());await store.InitializeAsync();Assert.Equal(original,Assert.Single((await store.QueryAsync(new(Provider:"Spotify"))).Items));Assert.True((await store.VerifyAsync()).Healthy);
    }
}
