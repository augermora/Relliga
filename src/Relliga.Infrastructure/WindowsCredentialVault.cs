using System.Security.Cryptography;
using System.Text;
using Relliga.Application;

namespace Relliga.Infrastructure;

// DPAPI CurrentUser: encrypted blobs only, in a separate per-user connection directory.
public sealed class WindowsCredentialVault(string directory) : ICredentialVault
{
    private string FileFor(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live account storage currently requires Windows DPAPI. macOS Keychain and Linux Secret Service are not implemented; offline browsing and imports remain available.");
        var full = Path.GetFullPath(directory); Directory.CreateDirectory(full);
        for (DirectoryInfo? info = new(full); info is not null; info = info.Parent)
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked credential directories are not supported.");
        var file = Path.Combine(full, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dpapi");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked credential files are not supported.");
        return file;
    }
    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var file = FileFor(key); if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Credential blob exceeds its limit.");
        var encrypted = await File.ReadAllBytesAsync(file, ct);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("Relliga/v1/" + key), DataProtectionScope.CurrentUser);
    }
    public async Task WriteAsync(string key, byte[] secret, CancellationToken ct = default)
    {
        if (secret.Length > 16384) throw new ArgumentException("Credential exceeds its limit.");
        var file = FileFor(key); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(secret, Encoding.UTF8.GetBytes("Relliga/v1/" + key), DataProtectionScope.CurrentUser);
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            using (var flushed = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flushed.Flush(true);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task DeleteAsync(string key, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); File.Delete(FileFor(key)); return Task.CompletedTask; }
}
