using System.Reflection;
using System.Xml.Linq;
using Relliga.Application;
using Relliga.Desktop;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class DistributionTests
{
    [Fact] public void SectionShortcutsUseTheNumberRowKeys()
    {
        var view = XDocument.Load(Path.Combine(Repository, "src/Relliga.Desktop/MainWindow.axaml"));
        var shortcuts = view.Descendants().Where(e => e.Name.LocalName == "KeyBinding")
            .Where(e => ((string?)e.Attribute("Gesture"))?.StartsWith("Alt+", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(9, shortcuts.Length);
        for (var i = 0; i < shortcuts.Length; i++)
        {
            var gesture = Avalonia.Input.KeyGesture.Parse(shortcuts[i].Attribute("Gesture")!.Value);
            Assert.Equal((Avalonia.Input.Key)((int)Avalonia.Input.Key.D1 + i), gesture.Key);
            Assert.Equal(Avalonia.Input.KeyModifiers.Alt, gesture.KeyModifiers);
        }
    }

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Relliga.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Run source-configuration tests from a repository build.");
        }
    }
    [Fact] public void VersionPublisherAndAssemblyNamesAgreeWithDistributionConfiguration()
    {
        var props = XDocument.Load(Path.Combine(Repository, "Directory.Build.props"));
        var version = props.Descendants("Version").Single().Value;
        Assert.Equal(version, AppInfo.Version); Assert.Equal("Relliga", typeof(MainViewModel).Assembly.GetName().Name);
        Assert.Equal("Relliga contributors", typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company);
        var manifest = XDocument.Load(Path.Combine(Repository, "src/Relliga.Desktop/app.manifest"));
        Assert.Equal(version + ".0", manifest.Descendants().Single(x => x.Name.LocalName == "assemblyIdentity").Attribute("version")!.Value);
        Assert.Equal("asInvoker", manifest.Descendants().Single(x => x.Name.LocalName == "requestedExecutionLevel").Attribute("level")!.Value);
    }

    [Fact] public void PortableProfileIncludesRuntimeWithoutRiskySizeOptimizations()
    {
        var profile = XDocument.Load(Path.Combine(Repository, "src/Relliga.Desktop/Properties/PublishProfiles/Portable.pubxml"));
        Assert.Equal("true", profile.Descendants("SelfContained").Single().Value);
        foreach (var option in new[] { "PublishTrimmed", "PublishAot", "PublishSingleFile" }) Assert.Equal("false", profile.Descendants(option).Single().Value);
        var project = XDocument.Load(Path.Combine(Repository, "src/Relliga.Desktop/Relliga.Desktop.csproj"));
        Assert.Contains(project.Descendants("None"), e => (string?)e.Attribute("Link") == "LICENSE" && (string?)e.Attribute("CopyToPublishDirectory") == "PreserveNewest");
        Assert.True(File.Exists(Path.Combine(Repository, "src/Relliga.Desktop/Assets/Relliga.ico")));
    }

    [Fact] public void DefaultLibraryLocationIsPerUserAndNeverTheApplicationDirectory()
    {
        var path = AppDataPaths.ResolveDatabasePath(userDataDirectory: Path.Combine(Path.GetTempPath(), "Synthetic User Data"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Synthetic User Data", "Relliga", "relliga.db"), path);
        Assert.False(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Theory] [InlineData("relative")] [InlineData("../relative")]
    public void RelativeLibraryOverridesAreRejected(string path) => Assert.Throws<ArgumentException>(() => AppDataPaths.ResolveDatabasePath(path));

    [Fact] public void LibrariesCannotBePlacedInsidePortableBinaries()
    {
        var app = Path.Combine(Path.GetTempPath(), "Relliga portable é");
        Assert.Throws<ArgumentException>(() => AppDataPaths.ResolveDatabasePath(app, app));
        Assert.Throws<ArgumentException>(() => AppDataPaths.ResolveDatabasePath(Path.Combine(app, "data"), app));
        Assert.EndsWith("relliga.db", AppDataPaths.ResolveDatabasePath(Path.Combine(Path.GetTempPath(), "Données privées 日本語"), app));
    }
}
