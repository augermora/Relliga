using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace Relliga.Importers;

internal sealed record InputMember(string Name, Stream Stream, bool InArchive);

internal static class ArchiveInputs
{
    public static async IAsyncEnumerable<InputMember> OpenAsync(string path, ImportLimits limits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var magic = new byte[4];
        var count = await file.ReadAsync(magic, ct); file.Position = 0;
        if (count >= 2 && magic[0] == 'P' && magic[1] == 'K')
        {
            if (file.Length > limits.MaxArchiveBytes) throw new InvalidDataException("ZIP exceeds the archive size limit.");
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries) throw new InvalidDataException("ZIP contains too many entries.");
            long declared = 0, actual = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains("..") || name.Length > 1024 || !names.Add(name))
                    throw new InvalidDataException("ZIP contains an unsafe or duplicate entry path.");
                declared = checked(declared + entry.Length);
                if (declared > limits.MaxExpandedBytes || entry.Length > limits.MaxJsonBytes ||
                    entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio))
                    throw new InvalidDataException("ZIP exceeds safe size or compression ratio limits.");
            }
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                await using var stream = entry.Open();
                await using var bounded = new BoundedReadStream(stream, limits.MaxJsonBytes, n =>
                {
                    actual += n;
                    if (actual > limits.MaxExpandedBytes) throw new InvalidDataException("ZIP exceeds the actual expanded byte limit.");
                });
                yield return new(entry.FullName, bounded, true);
            }
        }
        else
        {
            if (file.Length > limits.MaxJsonBytes) throw new InvalidDataException("Input exceeds the 64 MiB file limit.");
            yield return new(Path.GetFileName(path), file, false);
        }
    }
}

internal sealed class PrefixReadStream(byte[] prefix, int length, Stream inner) : Stream
{
    private int position;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position >= length) return inner.Read(buffer, offset, count);
        var take = Math.Min(count, length - position); prefix.AsSpan(position, take).CopyTo(buffer.AsSpan(offset)); position += take; return take;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (position >= length) return await inner.ReadAsync(buffer, ct);
        var take = Math.Min(buffer.Length, length - position); prefix.AsMemory(position, take).CopyTo(buffer); position += take; return take;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
