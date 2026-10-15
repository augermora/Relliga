using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relliga.Domain;

namespace Relliga.Importers;

public sealed class TelegramFormatException(string message) : FormatException(message);
public sealed class TelegramJsonImporter(ImportLimits? configuredLimits = null)
{
    public string Version => "1.0.0";
    public async IAsyncEnumerable<ImportRecord> ReadJsonAsync(Stream stream, string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var limits = configuredLimits ?? new();
        await using var bounded = new BoundedReadStream(stream, limits.MaxJsonBytes);
        using var document = await JsonDocument.ParseAsync(bounded, new() { MaxDepth = 32 }, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new TelegramFormatException("Unsupported Telegram export root.");
        var attributed = root.TryGetProperty("personal_information", out var personal) && personal.TryGetProperty("user_id", out var self) && Number(self) > 0;
        var account = attributed ? "user" + Number(root.GetProperty("personal_information").GetProperty("user_id")).ToString(CultureInfo.InvariantCulture)
            : "unattributed-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.GetRawText())));
        if (!attributed) yield return new(null, source, 0, Warning: "Telegram export lacks an account identifier. Its messages remain in an isolated export namespace; live-account merging is disabled.", Provider: "Telegram", Rejected: false);
        JsonElement[] groups;
        if (root.TryGetProperty("messages", out var single) && single.ValueKind == JsonValueKind.Array) groups = [root];
        else if (root.TryGetProperty("chats", out var chats) && chats.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        { if (list.GetArrayLength() > 10000) throw new InvalidDataException("Telegram export exceeds 10,000 conversations."); groups = list.EnumerateArray().ToArray(); }
        else throw new TelegramFormatException("Unsupported JSON. Choose an official Telegram Desktop JSON chat or account export.");
        var index = 0;
        foreach (var chat in groups)
        {
            string? yieldReturnWarning = null;
            var type = Text(chat, "type");
            long id;
            try { id = ChatId(type, chat.TryGetProperty("id", out var peer) ? Number(peer) : 0); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            { yieldReturnWarning = "Unsupported or malformed Telegram conversation identity. Secret chats and verification-code collections are excluded."; id = 0; }
            if (id == 0 || id == 777000)
            { yield return new(null, source, ++index, Warning: yieldReturnWarning ?? "Telegram authentication-code conversation excluded.", Provider: "Telegram", Rejected: false); yieldReturnWarning = null; continue; }
            if (!chat.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            { yield return new(null, source, ++index, Warning: "Telegram conversation has no messages array.", Provider: "Telegram"); continue; }
            foreach (var message in messages.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested(); if (++index > limits.MaxRecords) throw new InvalidDataException("Telegram export exceeds the record limit.");
                ImportRecord record;
                try { record = Parse(message, account, id, Text(chat, "name"), type!, source, index); }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
                { record = new(null, source, index, Warning: $"Telegram record {index}: malformed message ({ex.GetType().Name}).", Provider: "Telegram"); }
                yield return record;
            }
        }
    }
    public static long ChatId(string? type, long bare)
    {
        if (bare <= 0 || bare > 999_999_999_999) throw new TelegramFormatException("Telegram conversation has an invalid identifier.");
        return type switch
        {
            "personal_chat" or "bot_chat" or "saved_messages" => bare,
            "private_group" => -bare,
            "private_supergroup" or "public_supergroup" or "private_channel" or "public_channel" => -1_000_000_000_000 - bare,
            _ => throw new TelegramFormatException("Unsupported Telegram conversation type; secret chats and verification-code collections are excluded.")
        };
    }
    private ImportRecord Parse(JsonElement item, string account, long chat, string? title, string type, string source, int index)
    {
        if (item.GetRawText().Length > 1_048_576) throw new FormatException("Oversized message.");
        if (item.TryGetProperty("self_destruct_period_seconds", out _) || item.TryGetProperty("ttl_seconds", out _))
            return new(null, source, index, Warning: $"Telegram record {index}: disappearing content excluded.", Provider: "Telegram", Rejected: false);
        var id = Number(item.GetProperty("id")); if (id <= 0 || id > int.MaxValue) throw new FormatException("Invalid cloud message ID.");
        var sent = DateTimeOffset.FromUnixTimeSeconds(Number(item.GetProperty("date_unixtime")));
        var edited = item.TryGetProperty("edited_unixtime", out var edit) ? DateTimeOffset.FromUnixTimeSeconds(Number(edit)) : (DateTimeOffset?)null;
        string? text = null;
        if (item.TryGetProperty("text", out var content)) text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : Text(p, "text"))),
            _ => throw new FormatException("Unsupported message text.")
        };
        if (text?.Length > 65536) throw new FormatException("Oversized message text.");
        var message = new ConversationMessage(account, chat, id, title, type, Text(item, "from_id") ?? Text(item, "actor_id"), Text(item, "from") ?? Text(item, "actor"),
            text, sent, edited, "JSON", item.GetRawText(), item.TryGetProperty("file", out _) || item.TryGetProperty("photo", out _) || item.TryGetProperty("media_type", out _));
        return new(null, source, index, Provider: "Telegram", ImporterVersion: Version, Message: message);
    }
    private static string? Text(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.Parse(value.GetString()!, CultureInfo.InvariantCulture);
}
