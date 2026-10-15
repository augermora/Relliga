using Relliga.Application;
using Relliga.Desktop;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

// Exercise the actual desktop commands against real files, parser and SQLite.
// Native picker/window rendering are verified separately on a graphical desktop.
public sealed class DesktopFlowTests : IDisposable
{
    private readonly TestFiles files = new();
    private readonly SqliteHistoryStore store;
    private readonly MainViewModel vm;
    public DesktopFlowTests()
    {
        store = new(Path.Combine(files.DirectoryPath, "desktop.db"));
        var importer = new SpotifyImporter();
        vm = new(store, new ImportService(store, [importer]), importer, store);
    }
    public void Dispose() { store.Dispose(); files.Dispose(); }

    [Fact] public async Task ImportPreviewTimelineSearchInspectAndReimport()
    {
        var path = files.Write($"[{TestFiles.Row("Aurora")},{TestFiles.Row("Nebula", "2025-02-01T12:00:00Z")}]");
        vm.SelectFile = () => Task.FromResult<string?>(path);
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.Null(vm.Error); Assert.True(vm.IsReady);
        await vm.PickFileCommand.ExecuteAsync(null);
        Assert.Contains("Detected: Spotify", vm.Preview); Assert.True(vm.ImportCommand.CanExecute(null));
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Null(vm.Error); Assert.Equal("2", vm.EventCount); Assert.Equal("1", vm.SourceCount);
        vm.SelectedSection = 2;
        if (vm.FilterCommand.ExecutionTask is { } loading) await loading;
        Assert.Equal(2, vm.Events.Count); Assert.Equal("Nebula", vm.Events[0].Title);
        vm.SelectedEvent = vm.Events[0]; Assert.Contains("input.json", vm.SelectedDetails); Assert.Contains("test-device", vm.SelectedDetails);
        vm.SelectedSection = 3;
        if (vm.FilterCommand.ExecutionTask is { } searchLoading) await searchLoading;
        vm.SearchText = "Auro";
        await vm.FilterCommand.ExecuteAsync(null);
        Assert.Equal("Aurora", Assert.Single(vm.Events).Title);
        vm.FromDate = "2025-02-01"; vm.ToDate = "2025-02-28";
        await vm.FilterCommand.ExecuteAsync(null); Assert.Empty(vm.Events);
        await vm.ClearFiltersCommand.ExecuteAsync(null); Assert.Equal(2, vm.Events.Count);
        vm.SelectedSection = 1;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Contains("2 already present", vm.ImportResult); Assert.Equal("2", vm.EventCount);
        Assert.False(vm.IsBusy);
    }

    [Fact] public async Task UnsupportedFileAndInvalidDateShowActionableErrors()
    {
        await vm.InitializeCommand.ExecuteAsync(null);
        vm.SelectFile = () => Task.FromResult<string?>(files.Write("[{\"endTime\":\"2025\",\"msPlayed\":1}]"));
        await vm.PickFileCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.Null(vm.ImportPath); Assert.False(vm.ImportCommand.CanExecute(null));
        vm.DismissErrorCommand.Execute(null); Assert.False(vm.HasError);
        vm.SelectedSection = 2;
        if (vm.FilterCommand.ExecutionTask is { } loading) await loading;
        vm.FromDate = "not-a-date";
        await vm.FilterCommand.ExecuteAsync(null);
        Assert.Contains("YYYY-MM-DD", vm.Error!); Assert.False(vm.IsQuerying);
    }
}
