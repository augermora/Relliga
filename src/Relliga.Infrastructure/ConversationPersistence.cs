using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore : IConnectionStore
{
    public Task<ConnectionRecord?> GetConnectionAsync(string provider, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct); using var cmd = Command(c, null, "SELECT id,provider,automatic,state,last_success,account_id FROM connections WHERE provider=$p;", ("$p", provider));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ConnectionRecord(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), Text(r, 4) is { } time ? DateTimeOffset.Parse(time, CultureInfo.InvariantCulture) : null, Text(r, 5)) : null;
    }, ct);
    public Task SaveConnectionAsync(ConnectionRecord connection, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction();
        await ExecuteAsync(c, tx, "DELETE FROM connections WHERE provider=$p AND id<>$id;", ct, ("$p", connection.Provider), ("$id", connection.Id));
        await ExecuteAsync(c, tx, """
            INSERT INTO connections(id,provider,automatic,state,last_success,account_id) VALUES($id,$p,$a,$s,$last,$account)
            ON CONFLICT(id) DO UPDATE SET automatic=excluded.automatic,state=excluded.state,last_success=excluded.last_success,account_id=excluded.account_id;
            """, ct, ("$id", connection.Id), ("$p", connection.Provider), ("$a", connection.Automatic), ("$s", connection.State), ("$last", connection.LastSuccess is { } last ? Stamp(last) : null), ("$account", connection.AccountId)); tx.Commit();
    }, ct);
    public Task<string?> GetCheckpointAsync(string connectionId, string scope, CancellationToken ct = default) => Activity.RunAsync(async () =>
    { await using var c = await OpenAsync(ct); return await ScalarAsync(c, null, "SELECT cursor FROM sync_checkpoints WHERE connection_id=$id AND scope=$scope;", ct, ("$id", connectionId), ("$scope", scope)) as string; }, ct);

    public Task ApplySyncPageAsync(SyncPage page, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        if (page.Messages.Count > 100 || page.Deleted?.Count > 100 || page.Cursor?.Length > 4096 || page.Scope.Length > 200) throw new InvalidDataException("Synchronization page exceeds its safety budget.");
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction();
        var connected = Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM connections WHERE id=$id AND (automatic=1 OR state='Synchronizing') AND state<>'Disconnected';", ct, ("$id", page.ConnectionId)));
        if (connected != 1) throw new InvalidOperationException("The connection changed or the library was restored. Reconnect before synchronizing.");
        var expectedAccount = await ScalarAsync(c,tx,"SELECT account_id FROM connections WHERE id=$id;",ct,("$id",page.ConnectionId)) as string;
        if (expectedAccount is not null && (page.Messages.Any(m=>m.AccountId!=expectedAccount) || page.Deleted?.Any(d=>d.Account!=expectedAccount)==true))
            throw new InvalidDataException("Synchronization data does not match the authorized account. The page was rejected.");
        if (page.Deleted is not null) foreach (var deletion in page.Deleted) await DeleteProviderMessageAsync(c, tx, deletion.Account, deletion.Chat, deletion.Message, ct);
        if (page.Deleted is { Count: > 0 }) await CleanConversationMetadataAsync(c,tx,ct);
        var changed = new List<ConversationMessage>();
        foreach (var message in page.Messages)
        {
            var current = await ScalarAsync(c, tx, "SELECT message_json FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id=$m;", ct,
                ("$a", message.AccountId), ("$c", message.ChatId), ("$m", message.MessageId)) as string;
            if (current != JsonSerializer.Serialize(message)) changed.Add(message);
        }
        if (changed.Count > 0)
        {
            var operation = Convert.ToInt64(await ScalarAsync(c, tx, "INSERT INTO import_operations(file_name,provider,importer_version,started_at,status) VALUES('TDLib synchronization','Telegram','TDLib adapter 1.0.0',$now,'Running') RETURNING id;", ct, ("$now", Stamp(DateTimeOffset.UtcNow))));
            var added = 0; var updated = 0; var duplicates = 0;
            for (var i = 0; i < changed.Count; i++)
            {
                var result = await SaveMessageAsync(c, tx, changed[i], "TDLib", i + 1, operation, "TDLib adapter 1.0.0", ct);
                if (result == "Added") added++; else if (result == "Updated") updated++; else duplicates++;
            }
            await ExecuteAsync(c, tx, "UPDATE import_operations SET status='Completed',added=$a,updated=$u,duplicates=$d,processed=$n,rejected=0,finished_at=$now,message='Cloud message batch synchronized.' WHERE id=$id;", ct,
                ("$a", added), ("$u", updated), ("$d", duplicates), ("$n", changed.Count), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$id", operation));
        }
        await ExecuteAsync(c, tx, "INSERT INTO sync_checkpoints(connection_id,scope,cursor) VALUES($id,$scope,$cursor) ON CONFLICT(connection_id,scope) DO UPDATE SET cursor=excluded.cursor;", ct,
            ("$id", page.ConnectionId), ("$scope", page.Scope), ("$cursor", page.Cursor));
        MaintenanceFault?.Invoke("SyncBeforeCommit"); ct.ThrowIfCancellationRequested(); tx.Commit();
    }, ct);

    private static async Task<string> SaveMessageAsync(SqliteConnection c, SqliteTransaction tx, ConversationMessage message, string file, int index, long operation, string version, CancellationToken ct)
    {
        if (message.AccountId.Length is 0 or > 150 || message.ChatType.Length > 100 || message.ChatId == 0 || message.MessageId <= 0 || message.MessageId > int.MaxValue ||
            message.Text?.Length > 65536 || message.ChatTitle?.Length > 65536 || message.SenderName?.Length > 65536 || message.SenderId?.Length > 200 || message.OriginalJson.Length > 1_048_576 || message.Ingestion is not ("JSON" or "TDLib"))
            throw new InvalidDataException("Invalid or oversized conversation message.");
        if (Convert.ToInt64(await ScalarAsync(c, tx, "SELECT COUNT(*) FROM telegram_deletions WHERE account_id=$a AND chat_id=$c AND message_id=$m;", ct, ("$a", message.AccountId), ("$c", message.ChatId), ("$m", message.MessageId))) > 0) return "Deleted";
        await ExecuteAsync(c, tx, "INSERT INTO data_sources(provider) VALUES('Telegram') ON CONFLICT DO NOTHING;", ct);
        await ExecuteAsync(c, tx, "INSERT INTO telegram_chats(account_id,chat_id,title,type) VALUES($a,$c,$title,$type) ON CONFLICT(account_id,chat_id) DO UPDATE SET title=COALESCE(excluded.title,title),type=excluded.type;", ct,
            ("$a", message.AccountId), ("$c", message.ChatId), ("$title", message.ChatTitle), ("$type", message.ChatType));
        if (message.SenderId is { } sender) await ExecuteAsync(c, tx, "INSERT INTO telegram_participants(account_id,participant_id,name) VALUES($a,$s,$name) ON CONFLICT(account_id,participant_id) DO UPDATE SET name=COALESCE(excluded.name,name);", ct, ("$a", message.AccountId), ("$s", sender), ("$name", message.SenderName));
        var json = JsonSerializer.Serialize(message);
        var existing = await ScalarAsync(c, tx, "SELECT message_json FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id=$m;", ct, ("$a", message.AccountId), ("$c", message.ChatId), ("$m", message.MessageId)) as string;
        var previous = existing is null ? null : JsonSerializer.Deserialize<ConversationMessage>(existing)!;
        // An older export cannot replace a newer edit; TDLib wins equal-revision disagreements.
        var wins = previous is null || (message.EditedAt ?? message.SentAt) > (previous.EditedAt ?? previous.SentAt) ||
            (message.EditedAt ?? message.SentAt) == (previous.EditedAt ?? previous.SentAt) && message.Ingestion == "TDLib";
        var id = await ScalarAsync(c, tx, "SELECT event_id FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id=$m;", ct, ("$a", message.AccountId), ("$c", message.ChatId), ("$m", message.MessageId));
        if (id is null)
        {
            id = await ScalarAsync(c, tx, """
                INSERT INTO events(provider,fingerprint,occurrence,ended_at,title,artist,album,duration_ms,kind,metadata_json,first_import_id,source_file,record_index,stable_fingerprint,stable_occurrence,importer_version)
                VALUES('Telegram',$key,1,$sent,$text,$sender,$chat,0,'Message',$json,$op,$file,$index,$key,1,$version) RETURNING id;
                """, ct, ("$key", message.Identity), ("$sent", Stamp(message.SentAt)), ("$text", message.Text), ("$sender", message.SenderName), ("$chat", message.ChatTitle), ("$json", json), ("$op", operation), ("$file", file), ("$index", index), ("$version", version));
            await ExecuteAsync(c, tx, "INSERT INTO telegram_messages(event_id,account_id,chat_id,message_id,message_json) VALUES($id,$a,$c,$m,$json);", ct, ("$id", id), ("$a", message.AccountId), ("$c", message.ChatId), ("$m", message.MessageId), ("$json", json));
        }
        else if (wins && previous != message)
        {
            await ExecuteAsync(c, tx, "UPDATE telegram_messages SET message_json=$json WHERE event_id=$id; UPDATE events SET title=$text,artist=$sender,album=$chat,metadata_json=$json WHERE id=$id;", ct,
                ("$id", id), ("$json", json), ("$text", message.Text), ("$sender", message.SenderName), ("$chat", message.ChatTitle));
        }
        await ExecuteAsync(c, tx, "INSERT INTO event_origins(event_id,operation_id,source_file,record_index,metadata_json) VALUES($id,$op,$file,$index,$json);", ct, ("$id", id), ("$op", operation), ("$file", file), ("$index", index), ("$json", json));
        return previous is null ? "Added" : wins && previous != message ? "Updated" : !wins && previous.Text != message.Text && (message.EditedAt ?? message.SentAt) == (previous.EditedAt ?? previous.SentAt) ? "Conflict" : "Duplicate";
    }

    private static async Task DeleteProviderMessageAsync(SqliteConnection c, SqliteTransaction tx, string account, long chat, long message, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            INSERT INTO telegram_deletions(account_id,chat_id,message_id) VALUES($a,$c,$m) ON CONFLICT DO NOTHING;
            DELETE FROM event_origins WHERE event_id IN (SELECT event_id FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id=$m);
            DELETE FROM events WHERE id IN (SELECT event_id FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id=$m);
            """, ct, ("$a", account), ("$c", chat), ("$m", message));
    }
    private static Task CleanConversationMetadataAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct) => ExecuteAsync(c, tx, """
        DELETE FROM telegram_chats WHERE NOT EXISTS(SELECT 1 FROM telegram_messages m WHERE m.account_id=telegram_chats.account_id AND m.chat_id=telegram_chats.chat_id);
        DELETE FROM telegram_participants WHERE NOT EXISTS(SELECT 1 FROM telegram_messages m WHERE m.account_id=telegram_participants.account_id AND json_extract(m.message_json,'$.SenderId')=telegram_participants.participant_id);
        """, ct);
    public Task<IReadOnlyList<long>> GetMessageIdsAsync(string accountId, long chatId, long after, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct); using var cmd = Command(c, null, "SELECT message_id FROM telegram_messages WHERE account_id=$a AND chat_id=$c AND message_id>$after ORDER BY message_id LIMIT 100;", ("$a", accountId), ("$c", chatId), ("$after", after));
        await using var r = await cmd.ExecuteReaderAsync(ct); var ids = new List<long>(); while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0)); return (IReadOnlyList<long>)ids;
    }, ct);
    public Task<IReadOnlyList<long>> GetLiveChatIdsAsync(string accountId, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        await using var c = await OpenAsync(ct); using var cmd = Command(c, null, "SELECT DISTINCT chat_id FROM telegram_messages WHERE account_id=$a AND json_extract(message_json,'$.Ingestion')='TDLib' LIMIT 10001;", ("$a", accountId));
        await using var r = await cmd.ExecuteReaderAsync(ct); var ids = new List<long>();
        while (await r.ReadAsync(ct)) { if (ids.Count == 10000) throw new InvalidDataException("Too many live conversations to reconcile safely."); ids.Add(r.GetInt64(0)); }
        return (IReadOnlyList<long>)ids;
    }, ct);
    public Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string? text = null, CancellationToken ct = default) => Activity.RunAsync(async () =>
    {
        if (text?.Length > 1000) throw new ArgumentException("Conversation filter is too long.");
        await using var c = await OpenAsync(ct); using var cmd = Command(c, null, """
            SELECT c.account_id||':'||c.chat_id,c.title,c.type,COUNT(*) FROM telegram_chats c JOIN telegram_messages m ON m.account_id=c.account_id AND m.chat_id=c.chat_id
            WHERE $text='' OR instr(lower(COALESCE(c.title,'')),lower($text))>0 GROUP BY c.account_id,c.chat_id ORDER BY c.title,c.chat_id LIMIT 501;
            """, ("$text", text ?? "")); await using var r = await cmd.ExecuteReaderAsync(ct); var result = new List<ConversationSummary>();
        while (await r.ReadAsync(ct)) { if (result.Count == 500) throw new InvalidOperationException("More than 500 conversations match. Narrow the title filter."); result.Add(new(r.GetString(0), Text(r, 1), r.GetString(2), r.GetInt64(3))); } return (IReadOnlyList<ConversationSummary>)result;
    }, ct);
}
