using System.Security.Cryptography;
using System.Text;

namespace Relliga.Domain;

public sealed record ConversationMessage(string AccountId, long ChatId, long MessageId, string? ChatTitle,
    string ChatType, string? SenderId, string? SenderName, string? Text, DateTimeOffset SentAt,
    DateTimeOffset? EditedAt, string Ingestion, string OriginalJson, bool HasMedia = false)
{
    public string ConversationKey => AccountId + ":" + ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConversationKey + ":" + MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
}
public sealed record ConversationSummary(string Key, string? Title, string Type, long Messages);
public sealed record ConnectionRecord(string Id, string Provider, bool Automatic, string State,
    DateTimeOffset? LastSuccess = null, string? AccountId = null);
