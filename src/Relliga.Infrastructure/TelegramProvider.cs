using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Infrastructure;

public sealed class TelegramProvider(ITdClientFactory factory, TelegramConfiguration configuration) : IConnectedProvider, IDisposable
{
    public string Name => "Telegram";
    public event Action<ConnectionStatus>? StatusChanged;
    private ITdClient? client;
    private string? connectionId;
    private string? account;
    private string authState = "";
    private volatile bool networkReady;
    private volatile bool windowActive;
    private readonly SemaphoreSlim refresh = new(0, 1);
    private readonly ConcurrentDictionary<long, JsonElement> chats = new();
    private readonly ConcurrentDictionary<long, string> users = new();
    private bool disposed;
    private void Publish(string state, string description, string? input = null) => StatusChanged?.Invoke(new(state, description, input));
    public void Refresh() { if (refresh.CurrentCount == 0) { try { refresh.Release(); } catch (SemaphoreFullException) { } } }
    public async Task RunAsync(ConnectionRecord connection, IConnectionStore store, CancellationToken ct, bool once = false)
    {
        connectionId = connection.Id; account = null; networkReady = false; chats.Clear(); users.Clear();
        var application = await configuration.GetApplicationAsync(ct);
        var key = await configuration.EncryptionKeyAsync(connection.Id, ct);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ITdClient created;
        try { created = factory.Create(); } catch { CryptographicOperations.ZeroMemory(key); throw; }
        await using var td = created; client = td;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = ReceiveAsync(td, connection.Id, store, application, key, ready, session.Token);
        Exception? failure = null;
        try
        {
            Publish("Connecting", "Connecting through the official Telegram TDLib client…");
            await ready.Task.WaitAsync(ct);
            await td.CallAsync("setOption", new { name = "online", value = new Dictionary<string, object> { ["@type"] = "optionValueBoolean", ["value"] = windowActive } }, ct);
            var me = await td.CallAsync("getMe", null, ct); var userId = Long(me,"id"); if (userId <= 0) throw new InvalidDataException("Telegram did not supply a valid authorized account identity."); account = "user" + userId.ToString(CultureInfo.InvariantCulture);
            var saved = await store.GetConnectionAsync(Name, ct);
            if (saved is null || saved.Id != connection.Id) throw new InvalidOperationException("Connection changed.");
            if (saved.AccountId is not null && saved.AccountId != account) throw new ProviderFailure("This session belongs to a different Telegram account. Disconnect before connecting another account.", true);
            await store.SaveConnectionAsync(saved with { State = once ? "Synchronizing" : "Connected", AccountId = account }, ct);
            while (!ct.IsCancellationRequested)
            {
                while (!networkReady) { if (receiving.IsCompleted) await receiving; await Task.Delay(250, ct); }
                await SynchronizeAsync(td, connection.Id, store, ct);
                if (!networkReady) continue;
                saved = await store.GetConnectionAsync(Name, ct);
                if (saved is null || saved.Id != connection.Id || !once && !saved.Automatic) return;
                await store.SaveConnectionAsync(saved with { LastSuccess = DateTimeOffset.UtcNow, State = once ? "Paused" : "Connected" }, ct);
                if (once) { Publish("Paused", "Manual synchronization completed. Automatic synchronization remains paused."); return; }
                Publish("Connected", "Up to date for supported cloud chats. Live updates are active; history reconciliation runs every minute. Channels, secret chats, protected and disappearing content are excluded.");
                var signaled = refresh.WaitAsync(TimeSpan.FromMinutes(1), ct);
                if (await Task.WhenAny(signaled, receiving) == receiving) { await receiving; throw new ProviderFailure("Telegram closed the live session. Reconnect to authorize or resume.", true); }
                await signaled;
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            session.Cancel();
            try { await receiving; } catch (OperationCanceledException) when (session.IsCancellationRequested) { }
            catch (Exception ex) { failure ??= ex; }
            client = null; CryptographicOperations.ZeroMemory(key);
        }
        if (failure is TdRequestException request) throw new ProviderFailure(request.Code == 401 ? "Telegram authorization is no longer valid. Disconnect and authorize again." : $"Telegram synchronization request failed (code {request.Code}).", request.Code == 401, request.RetryAfter);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task ReceiveAsync(ITdClient td, string id, IConnectionStore store, TelegramApplicationCredentials app, byte[] key, TaskCompletionSource ready, CancellationToken ct)
    {
        try
        {
            await foreach (var update in td.Updates(ct))
            {
                switch (Type(update))
                {
                    case "updateAuthorizationState":
                        var state = update.GetProperty("authorization_state"); authState = Type(state);
                        switch (authState)
                        {
                            case "authorizationStateWaitTdlibParameters":
                                await td.CallAsync("setTdlibParameters", new { use_test_dc = false, database_directory = configuration.SessionDirectory(id), files_directory = configuration.SessionDirectory(id), database_encryption_key = Convert.ToBase64String(key),
                                    use_file_database = false, use_chat_info_database = true, use_message_database = true, use_secret_chats = false,
                                    api_id = app.ApiId, api_hash = app.ApiHash, system_language_code = "en", device_model = "Relliga desktop", system_version = Environment.OSVersion.VersionString, application_version = AppInfo.Version }, ct); break;
                            case "authorizationStateReady": ready.TrySetResult(); break;
                            case "authorizationStateWaitPhoneNumber": Publish("Authorization required", "Enter your phone number with country code. Relliga uses the Telegram API. Opening a live conversation sends normal read receipts; history synchronization does not open chats.", "Phone number"); break;
                            case "authorizationStateWaitCode": Publish("Verification required", "Enter the verification code delivered by Telegram. Codes are used only in memory.", "Verification code"); break;
                            case "authorizationStateWaitPassword": Publish("Two-step verification", "Enter your Telegram two-step verification password. It is never saved or logged.", "Password"); break;
                            case "authorizationStateWaitEmailAddress": Publish("Email required", "Telegram requires an email address for this authorization.", "Email address"); break;
                            case "authorizationStateWaitEmailCode": Publish("Email verification", "Enter the code Telegram sent to your authorization email.", "Email code"); break;
                            case "authorizationStateWaitOtherDeviceConfirmation": Publish("Device confirmation", "Confirm this login in your existing Telegram app. Use the official app's device confirmation flow. Relliga does not intercept sessions."); break;
                            case "authorizationStateWaitRegistration": throw new ProviderFailure("This account must be registered in an official Telegram app first. Relliga does not create accounts.", true);
                            case "authorizationStateWaitPremiumPurchase": throw new ProviderFailure("Telegram requires a purchase for this authorization. Complete it in an official Telegram app; Relliga does not process purchases.", true);
                            case "authorizationStateLoggingOut": Publish("Disconnecting", "Telegram is revoking this session…"); break;
                            case "authorizationStateClosing": break;
                            case "authorizationStateClosed": ready.TrySetException(new ProviderFailure("Telegram closed the session. Reauthorization may be required.", true)); return;
                            default: throw new ProviderFailure("Telegram requested an unsupported authorization step. Use an official client and retry; no credentials were stored for this step.", true);
                        }
                        break;
                    case "updateConnectionState":
                        var network = Type(update.GetProperty("state"));
                        networkReady = network == "connectionStateReady";
                        if (network != "connectionStateReady") Publish("Offline / reconnecting", "TDLib is reconnecting (" + network + "). Your local library remains available offline.");
                        else if (account is not null) { Publish("Connected", "Telegram connection restored; checking for missed updates…"); Refresh(); }
                        break;
                    case "updateUser":
                        var user = update.GetProperty("user");
                        if (users.Count >= 100000) throw new InvalidDataException("Telegram participant cache limit reached. Pause and narrow the connected account scope.");
                        var display = (String(user, "first_name") + " " + String(user, "last_name")).Trim(); if (display.Length > 512) throw new InvalidDataException("Telegram participant name exceeds the safety limit.");
                        users[Long(user, "id")] = display; break;
                    case "updateNewChat":
                        var chat = update.GetProperty("chat"); if (chats.Count >= 10000) throw new InvalidDataException("Telegram conversation limit reached (10,000).");
                        chats[Long(chat, "id")] = KeepChat(chat); break;
                    case "updateNewMessage":
                        if (account is not null) await ApplyMessageAsync(td, store, id, update.GetProperty("message"), ct); break;
                    case "updateMessageContent":
                    case "updateMessageEdited":
                        if (account is not null) await RefreshMessageAsync(td, store, id, Long(update, "chat_id"), Long(update, "message_id"), ct); break;
                    case "updateDeleteMessages":
                        if (account is not null && !Bool(update, "from_cache"))
                        {
                            var ids = update.GetProperty("message_ids").EnumerateArray().Select(v => v.GetInt64()).ToArray();
                            foreach (var batch in ids.Chunk(100)) await DeleteAsync(store, id, Long(update, "chat_id"), batch, ct);
                        }
                        break;
                    case "updateChatMessageAutoDeleteTime":
                    case "updateChatHasProtectedContent":
                    case "updateChatTitle":
                        var changed = Long(update, "chat_id");
                        var refreshed = KeepChat(await td.CallAsync("getChat", new { chat_id = changed }, ct)); chats[changed] = refreshed;
                        if (account is not null && PrivacyBlocked(refreshed)) await PurgeChatAsync(store, id, changed, ct);
                        break;
                }
            }
        }
        catch (Exception ex) { ready.TrySetException(ex); throw; }
    }
    public async Task AuthorizeAsync(string input, CancellationToken ct)
    {
        var td = client ?? throw new InvalidOperationException("Start the Telegram connection first.");
        if (input.Length is 0 or > 1024) throw new ArgumentException("Enter a nonempty authentication value of at most 1,024 characters.");
        try
        {
            _ = authState switch
            {
                "authorizationStateWaitPhoneNumber" => await td.CallAsync("setAuthenticationPhoneNumber", new { phone_number = input }, ct),
                "authorizationStateWaitCode" => await td.CallAsync("checkAuthenticationCode", new { code = input }, ct),
                "authorizationStateWaitPassword" => await td.CallAsync("checkAuthenticationPassword", new { password = input }, ct),
                "authorizationStateWaitEmailAddress" => await td.CallAsync("setAuthenticationEmailAddress", new { email_address = input }, ct),
                "authorizationStateWaitEmailCode" => await td.CallAsync("checkAuthenticationEmailCode", new { code = new Dictionary<string, object> { ["@type"] = "emailAddressAuthenticationCode", ["code"] = input } }, ct),
                _ => throw new InvalidOperationException("Telegram is not waiting for typed authentication. Follow the displayed authorization state.")
            };
        }
        catch (TdRequestException ex) { throw new InvalidOperationException($"Telegram rejected the authorization request (code {ex.Code}). Check the requested input; retry after any Telegram rate limit."); }
    }
    private async Task SynchronizeAsync(ITdClient td, string id, IConnectionStore store, CancellationToken ct)
    {
        Publish("Synchronizing", "Loading accessible cloud chat lists and bounded history pages…");
        var chatIds = new HashSet<long>();
        foreach (var listName in new[] { "chatListMain", "chatListArchive" })
        {
            var list = new Dictionary<string, object> { ["@type"] = listName };
            var finished = false;
            for (var i = 0; i < 101; i++)
            {
                try { await td.CallAsync("loadChats", new { chat_list = list, limit = 100 }, ct); }
                catch (TdRequestException ex) when (ex.Code == 404) { finished = true; break; }
            }
            if (!finished) throw new InvalidDataException("Telegram chat-list loading exceeded its bounded limit.");
            var result = await td.CallAsync("getChats", new { chat_list = list, limit = 10000 }, ct);
            foreach (var item in result.GetProperty("chat_ids").EnumerateArray()) chatIds.Add(item.GetInt64());
        }
        if (chatIds.Count > 10000) throw new InvalidDataException("Telegram supports at most 10,000 cloud chats in this iteration.");
        foreach (var known in await store.GetLiveChatIdsAsync(account!, ct)) chatIds.Add(known);
        foreach (var chatId in chatIds.Order())
        {
            ct.ThrowIfCancellationRequested();
            JsonElement chat;
            try { chat = await td.CallAsync("getChat", new { chat_id = chatId }, ct); }
            catch (TdRequestException ex) when (ex.Code is 403 or 404) { await PurgeChatAsync(store, id, chatId, ct); continue; }
            chat = KeepChat(chat); chats[chatId] = chat;
            if (!Allowed(chat)) { if (PrivacyBlocked(chat)) await PurgeChatAsync(store, id, chatId, ct); continue; }
            var scope = "history/" + chatId.ToString(CultureInfo.InvariantCulture);
            var checkpoint = await store.GetCheckpointAsync(id, scope, ct);
            long from = checkpoint is not null && checkpoint.StartsWith("older:", StringComparison.Ordinal) ? long.Parse(checkpoint[6..], CultureInfo.InvariantCulture) : 0;
            var latestKnown = checkpoint is not null && checkpoint.StartsWith("head:", StringComparison.Ordinal) ? long.Parse(checkpoint[5..], CultureInfo.InvariantCulture) : 0;
            long newest = latestKnown; var count = 0;
            while (true)
            {
                var history = await td.CallAsync("getChatHistory", new { chat_id = chatId, from_message_id = from, offset = 0, limit = 25, only_local = false }, ct);
                var raw = history.GetProperty("messages").EnumerateArray().Where(m => Long(m, "id") != from).ToArray();
                if (raw.Length == 0) break;
                var next = raw.Min(m => Long(m, "id")); newest = Math.Max(newest, raw.Max(m => Long(m, "id")));
                if (from != 0 && next >= from) throw new InvalidDataException("Telegram history did not advance. Checkpoint preserved.");
                var messages = new List<ConversationMessage>();
                foreach (var message in raw)
                {
                    if (++count > 1_000_000) throw new InvalidDataException("A Telegram conversation exceeds the per-pass one-million-message budget. Progress is checkpointed; resume to continue.");
                    var parsed = ParseMessage(message, chat); if (parsed is not null) messages.Add(parsed);
                    else await DeleteAsync(store, id, chatId, [Long(message, "id")], ct);
                }
                await store.ApplySyncPageAsync(new(id, scope, "older:" + next.ToString(CultureInfo.InvariantCulture), messages), ct);
                Publish("Synchronizing", $"Synchronizing cloud conversations · {count:N0} messages processed in the current chat.");
                if (latestKnown > 0 && next <= latestKnown) break;
                from = next;
            }
            // Reconcile every persisted message in bounded batches, including edits/deletions missed while stopped.
            long after = 0;
            while (true)
            {
                var ids = await store.GetMessageIdsAsync(account!, chatId, after, ct); if (ids.Count == 0) break;
                after = ids[^1]; var nativeIds = ids.Select(ToTdId).ToArray();
                var response = await td.CallAsync("getMessages", new { chat_id = chatId, message_ids = nativeIds }, ct);
                var messages = new List<ConversationMessage>(); var deleted = new List<(string, long, long)>();
                var returned = response.GetProperty("messages").EnumerateArray().ToArray();
                if (returned.Length != ids.Count) throw new InvalidDataException("Telegram reconciliation returned an inconsistent message count.");
                for (var i = 0; i < returned.Length; i++)
                { var parsed = returned[i].ValueKind == JsonValueKind.Null ? null : ParseMessage(returned[i], chat); if (parsed is null) deleted.Add((account!, chatId, ids[i])); else messages.Add(parsed); }
                await store.ApplySyncPageAsync(new(id, "reconcile/" + chatId, after.ToString(CultureInfo.InvariantCulture), messages, deleted), ct);
            }
            await store.ApplySyncPageAsync(new(id, scope, "head:" + newest.ToString(CultureInfo.InvariantCulture), []), ct);
        }
    }
    private bool Allowed(JsonElement chat)
    {
        var id = Long(chat, "id"); var type = chat.GetProperty("type");
        return id != 777000 && !Bool(chat, "has_protected_content") && Long(chat, "message_auto_delete_time") == 0 &&
            (Type(type) is "chatTypePrivate" or "chatTypeBasicGroup" || Type(type) == "chatTypeSupergroup" && !Bool(type, "is_channel"));
    }
    private static bool PrivacyBlocked(JsonElement chat) => Long(chat,"id") == 777000 || Bool(chat,"has_protected_content") || Long(chat,"message_auto_delete_time") > 0;
    private static JsonElement KeepChat(JsonElement chat)
    {
        var title = String(chat,"title"); if (title?.Length > 1024) throw new InvalidDataException("Telegram conversation title exceeds the safety limit.");
        var type = chat.GetProperty("type");
        return JsonSerializer.SerializeToElement(new { id=Long(chat,"id"), title, type=new Dictionary<string,object>{["@type"]=Type(type),["is_channel"]=Bool(type,"is_channel")}, has_protected_content=Bool(chat,"has_protected_content"),message_auto_delete_time=Long(chat,"message_auto_delete_time") });
    }
    internal ConversationMessage? ParseMessage(JsonElement message, JsonElement chat)
    {
        if (!message.TryGetProperty("can_be_saved", out var savable) || savable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("TDLib message schema is incompatible. Use the documented official source revision; no message deletion or checkpoint was applied for this response.");
        var nativeId = Long(message, "id");
        if (!Allowed(chat) || nativeId <= 0 || nativeId % (1 << 20) != 0 || !Bool(message, "can_be_saved") || Bool(message, "is_channel_post") ||
            Long(message, "date") <= 0 || Number(message, "self_destruct_in") > 0 || Number(message, "auto_delete_in") > 0 ||
            HasObject(message, "self_destruct_type") || HasObject(message, "ephemeral_content") || HasObject(message, "sending_state") || HasObject(message, "scheduling_state")) return null;
        var content = message.GetProperty("content"); string? body = null;
        if (content.TryGetProperty("text", out var text)) body = String(text, "text");
        else if (content.TryGetProperty("caption", out var caption)) body = String(caption, "text");
        if (body?.Length > 65536 || content.GetRawText().Length > 1_000_000) throw new InvalidDataException("Telegram message exceeds the text/metadata budget.");
        var sender = message.GetProperty("sender_id"); var user = Long(sender, "user_id"); var senderId = user != 0 ? "user" + user : "chat" + Long(sender, "chat_id");
        var name = user != 0 ? users.GetValueOrDefault(user) : chats.TryGetValue(Long(sender, "chat_id"), out var senderChat) ? String(senderChat, "title") : null;
        var original = JsonSerializer.Serialize(new { id = nativeId, chat_id = Long(message, "chat_id"), date = Long(message, "date"), edit_date = Long(message, "edit_date"), sender_id = sender, content });
        return new(account ?? throw new InvalidOperationException("No authorized account identity."), Long(message, "chat_id"), nativeId >> 20, String(chat, "title"), Type(chat.GetProperty("type")), senderId, name, body,
            DateTimeOffset.FromUnixTimeSeconds(Long(message, "date")), Long(message, "edit_date") is var edit && edit > 0 ? DateTimeOffset.FromUnixTimeSeconds(edit) : null, "TDLib", original, Type(content) != "messageText");
    }
    private async Task ApplyMessageAsync(ITdClient td, IConnectionStore store, string id, JsonElement message, CancellationToken ct)
    {
        var chatId = Long(message, "chat_id");
        var chat = chats.TryGetValue(chatId, out var known) ? known : await td.CallAsync("getChat", new { chat_id = chatId }, ct);
        var parsed = ParseMessage(message, chat);
        if (parsed is null) await DeleteAsync(store, id, chatId, [Long(message, "id")], ct);
        else await store.ApplySyncPageAsync(new(id, "updates", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), [parsed]), ct);
    }
    private async Task RefreshMessageAsync(ITdClient td, IConnectionStore store, string id, long chat, long message, CancellationToken ct)
    { try { await ApplyMessageAsync(td, store, id, await td.CallAsync("getMessage", new { chat_id = chat, message_id = message }, ct), ct); } catch (TdRequestException ex) when (ex.Code == 404) { await DeleteAsync(store, id, chat, [message], ct); } }
    private Task DeleteAsync(IConnectionStore store, string id, long chat, IEnumerable<long> nativeIds, CancellationToken ct) => store.ApplySyncPageAsync(new(id, "deletions", null, [], nativeIds.Where(n => n > 0 && n % (1 << 20) == 0).Select(n => (account!, chat, n >> 20)).ToArray()), ct);
    private async Task PurgeChatAsync(IConnectionStore store, string id, long chat, CancellationToken ct)
    {
        long after = 0;
        while (true) { var ids = await store.GetMessageIdsAsync(account!, chat, after, ct); if (ids.Count == 0) return; after = ids[^1]; await store.ApplySyncPageAsync(new(id, "privacy/" + chat, null, [], ids.Select(m => (account!, chat, m)).ToArray()), ct); }
    }
    public async Task MarkViewedAsync(string conversationKey, IEnumerable<long> ids, CancellationToken ct)
    {
        if (client is null || account is null || !conversationKey.StartsWith(account + ":", StringComparison.Ordinal)) return;
        var chat = long.Parse(conversationKey[(account.Length + 1)..], CultureInfo.InvariantCulture);
        await client.CallAsync("viewMessages", new { chat_id = chat, message_ids = ids.Take(100).Select(ToTdId).ToArray(), source = new Dictionary<string, string> { ["@type"] = "messageSourceChatHistory" }, force_read = true }, ct);
    }
    public async Task SetWindowActiveAsync(bool active,CancellationToken ct = default)
    {
        windowActive=active;
        if (client is { } td && account is not null) await td.CallAsync("setOption",new { name="online",value=new Dictionary<string,object>{["@type"]="optionValueBoolean",["value"]=active}},ct);
    }
    public async Task<string> DisconnectAsync(ConnectionRecord? connection, CancellationToken ct)
    {
        connectionId = connection?.Id ?? connectionId;
        if (connectionId is null) return "No local active session.";
        if (client is { } td) { await td.CallAsync("logOut", null, ct); await configuration.ForgetKeyAsync(connectionId, ct); return "Telegram accepted logout; protected local session key removed."; }
        await configuration.ForgetKeyAsync(connectionId, ct);
        return "Local session key removed. Server logout could not be confirmed; revoke the Relliga device in Telegram Settings → Devices.";
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public void Dispose() { if (!disposed) { disposed = true; refresh.Dispose(); } }
    private static long ToTdId(long id) => checked(id * (1 << 20));
    private static string Type(JsonElement value) => String(value, "@type") ?? "";
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.TryGetInt64(out var number) ? number : 0;
    private static double Number(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.TryGetDouble(out var number) ? number : 0;
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    private static bool HasObject(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null;
}
