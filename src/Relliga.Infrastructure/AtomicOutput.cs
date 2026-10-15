namespace Relliga.Infrastructure;

/// <summary>A temporary sibling keeps publication on the destination filesystem.</summary>
internal sealed class AtomicOutput : IDisposable
{
    public string Destination { get; }
    public string Temporary { get; }
    private readonly bool overwrite;
    public AtomicOutput(string destination, bool overwrite)
    {
        Destination = Path.GetFullPath(destination);
        this.overwrite = overwrite;
        if (File.Exists(Destination) && (File.GetAttributes(Destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a regular destination file; linked files cannot be replaced.");
        if (File.Exists(Destination) && !overwrite) throw new IOException("The destination exists. Confirm replacement or choose another filename.");
        Temporary = Path.Combine(Path.GetDirectoryName(Destination)!, ".relliga-" + Guid.NewGuid().ToString("N") + ".tmp");
    }
    public void Publish(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (var file = new FileStream(Temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
        // File.Replace never truncates the original destination before the replacement is complete.
        if (File.Exists(Destination) && overwrite) File.Replace(Temporary, Destination, null);
        else File.Move(Temporary, Destination, false);
    }
    public void Dispose() { if (File.Exists(Temporary)) File.Delete(Temporary); }
}
