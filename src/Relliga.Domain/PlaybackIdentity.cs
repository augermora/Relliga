using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relliga.Domain;

public static class PlaybackIdentity
{
    // A multiset ordinal is added by persistence, preserving identical repeated occurrences.
    public static string Create(string mediaUri, DateTimeOffset ended, long milliseconds, string? account, string? device)
        => "playback-v2:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { mediaUri, ended.UtcTicks.ToString(CultureInfo.InvariantCulture), milliseconds.ToString(CultureInfo.InvariantCulture), account, device }))));
}
