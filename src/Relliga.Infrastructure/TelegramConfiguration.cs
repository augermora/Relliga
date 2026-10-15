using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relliga.Application;

namespace Relliga.Infrastructure;

public sealed record TelegramApplicationCredentials(int ApiId, string ApiHash);
public sealed class TelegramConfiguration(ICredentialVault vault, string sessionRoot, string libraryPath, string nativePath)
{
    public string NativePath { get; } = nativePath;
    private readonly string libraryScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(libraryPath))));
    public string SessionDirectory(string connectionId)
    {
        if (!Guid.TryParseExact(connectionId, "N", out _)) throw new InvalidDataException("Invalid connection identity.");
        var path = Path.Combine(sessionRoot, libraryScope, connectionId); Directory.CreateDirectory(path);
        for (DirectoryInfo? info = new(path); info is not null; info = info.Parent)
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Telegram session directories are not supported.");
        return path;
    }
    public async Task<TelegramApplicationCredentials> GetApplicationAsync(CancellationToken ct)
    {
        var id = Environment.GetEnvironmentVariable("RELLIGA_TELEGRAM_API_ID"); var hash = Environment.GetEnvironmentVariable("RELLIGA_TELEGRAM_API_HASH");
        if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(hash))
        {
            if (!int.TryParse(id, out var number) || number <= 0 || hash?.Length != 32 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Telegram application configuration is invalid. The publisher must provide a valid API ID and hash; do not enter them as account authentication.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new TelegramApplicationCredentials(number, hash));
            try { await vault.WriteAsync("telegram-application", bytes, ct); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var saved = await vault.ReadAsync("telegram-application", ct);
        if (saved is null) throw new InvalidDataException("Telegram live connection is not configured by the publisher. Developer setup requires Relliga-owned API credentials and official TDLib. Ordinary users should not obtain developer credentials. JSON import remains available.");
        try { return JsonSerializer.Deserialize<TelegramApplicationCredentials>(saved) ?? throw new InvalidDataException("Invalid protected Telegram application configuration."); }
        finally { CryptographicOperations.ZeroMemory(saved); }
    }
    private string Key(string id) => "telegram-session/" + libraryScope + "/" + id;
    public async Task<byte[]> EncryptionKeyAsync(string id, CancellationToken ct)
    {
        var key = await vault.ReadAsync(Key(id), ct);
        if (key is not null) { if (key.Length != 32) throw new InvalidDataException("Invalid protected TDLib key. Reauthorization is required."); return key; }
        var directory = SessionDirectory(id);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new InvalidDataException("TDLib session files exist without their protected key. They were preserved. Disconnect/reconnect with a new session; never recreate the key over an existing database.");
        key = RandomNumberGenerator.GetBytes(32); await vault.WriteAsync(Key(id), key, ct); return key;
    }
    public Task ForgetKeyAsync(string id, CancellationToken ct) => vault.DeleteAsync(Key(id), ct);
}
