namespace Relliga.Infrastructure;

/// <summary>A temporary sibling keeps publication on the destination filesystem.</summary>
internal sealed class AtomicOutput : IDisposable
{
    public string Destination { get; }
    public string Temporary { get; }
    private readonly bool overwrite;
    private readonly (long Length, DateTime Modified)? original;
    public AtomicOutput(string destination, bool overwrite)
    {
        Destination = Path.GetFullPath(destination);
        this.overwrite = overwrite;
        if (File.Exists(Destination) && (File.GetAttributes(Destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a regular destination file; linked files cannot be replaced.");
        if (File.Exists(Destination) && !overwrite) throw new IOException("The destination exists. Confirm replacement or choose another filename.");
        if (File.Exists(Destination)) { var info = new FileInfo(Destination); original = (info.Length, info.LastWriteTimeUtc); }
        Temporary = Path.Combine(Path.GetDirectoryName(Destination)!, ".relliga-" + Guid.NewGuid().ToString("N") + ".tmp");
    }
    public void Publish(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (var file = new FileStream(Temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
        // File.Replace never truncates the original destination before the replacement is complete.
        if (original is { } expected && overwrite)
        {
            var current = new FileInfo(Destination);
            if (!current.Exists || current.Length != expected.Length || current.LastWriteTimeUtc != expected.Modified ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The destination changed during processing. Review it and choose the destination again; it was not replaced.");
            File.Replace(Temporary, Destination, null);
        }
        else File.Move(Temporary, Destination, false);
    }
    public void Dispose() { if (File.Exists(Temporary)) File.Delete(Temporary); }
}
