namespace Relliga.Infrastructure;

public static class AppDataPaths
{
    public static string ResolveDatabasePath(string? overrideDirectory = null, string? applicationDirectory = null, string? userDataDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(userDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relliga")
            : overrideDirectory;
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The library directory must be an absolute per-user path. Set RELLIGA_DATA_DIRECTORY to an absolute directory outside the application folder.");
        directory = Path.GetFullPath(directory);
        var binaries = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directory.Equals(binaries, comparison) || directory.StartsWith(binaries + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Personal libraries cannot be stored alongside application binaries. Choose a separate per-user data directory.");
        return Path.Combine(directory, "relliga.db");
    }
}
