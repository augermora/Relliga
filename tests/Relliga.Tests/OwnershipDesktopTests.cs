using Relliga.Application;
using Relliga.Desktop;
using Relliga.Importers;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class OwnershipDesktopTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    private MainViewModel vm = null!;
    public async ValueTask InitializeAsync()
    {
        store = new(Path.Combine(files.DirectoryPath, "library", "relliga.db"), new CalendarExpander());
        var importer = new ExportImporter(); vm = new(store, new ImportService(store, [importer]), importer, store);
        await vm.InitializeCommand.ExecuteAsync(null);
        vm.SelectFile = () => Task.FromResult<string?>(files.Write("[" + TestFiles.Row() + "]"));
        await vm.PickFileCommand.ExecuteAsync(null); await vm.ImportCommand.ExecuteAsync(null);
    }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }

    [Fact] public async Task ExportUsesAppliedSearchFiltersAndNativeDialogDelegates()
    {
        vm.SelectedSection = 3; if (vm.FilterCommand.ExecutionTask is { } loading) await loading;
        vm.SearchText = "Synthetic"; await vm.FilterCommand.ExecuteAsync(null);
        vm.SearchText = "Unapplied change"; vm.SelectedSection = 5;
        var output = Path.Combine(files.DirectoryPath, "export.json");
        vm.SaveFile = (name, extension) => { Assert.Equal("json", extension); return Task.FromResult<string?>(output); };
        Assert.Contains("text: Synthetic", vm.ExportScopeSummary);
        await vm.ExportDataCommand.ExecuteAsync(null); Assert.Null(vm.Error); Assert.True(File.Exists(output)); Assert.Contains("Exported 1", vm.OwnershipResult);
    }

    [Fact] public async Task DeleteRequiresConfirmationAndRefreshesAfterSuccess()
    {
        vm.SelectedRemovalSource = "Spotify"; vm.Confirm = (_, details) => { Assert.Contains("1 stored", details); return Task.FromResult(false); };
        await vm.RemoveSourceCommand.ExecuteAsync(null); Assert.Equal("1", vm.EventCount);
        vm.Confirm = (_, details) => Task.FromResult(true);
        await vm.RemoveSourceCommand.ExecuteAsync(null); Assert.Null(vm.Error); Assert.Equal("0", vm.EventCount); Assert.Empty(vm.RecentImports);
    }

    [Fact] public async Task BackupRestoreAndHealthUseActualServicesAndCancellationIsSafe()
    {
        var archive = Path.Combine(files.DirectoryPath, "backup.zip"); vm.SaveFile = (_, _) => Task.FromResult<string?>(archive);
        await vm.CreateBackupCommand.ExecuteAsync(null); Assert.Null(vm.Error); Assert.True(File.Exists(archive));
        vm.Confirm = (_, _) => Task.FromResult(true); await vm.RemoveSourceCommand.ExecuteAsync(null); Assert.Equal("0", vm.EventCount);
        vm.SelectBackup = () => Task.FromResult<string?>(archive);
        vm.Confirm = (_, details) => { Assert.Contains("Replaces the entire", details); return Task.FromResult(false); };
        await vm.RestoreBackupCommand.ExecuteAsync(null); Assert.Equal("0", vm.EventCount);
        vm.Confirm = (_, _) => Task.FromResult(true); await vm.RestoreBackupCommand.ExecuteAsync(null); Assert.Null(vm.Error); Assert.Equal("1", vm.EventCount);
        await vm.VerifyLibraryCommand.ExecuteAsync(null); Assert.Contains("passed", vm.HealthStatus);
    }

    [Fact] public async Task DestinationReplacementRequiresConfirmationAndFailureIsVisible()
    {
        var destination = files.Write("existing", "existing.json"); vm.ExportScope = 1; vm.SaveFile = (_, _) => Task.FromResult<string?>(destination);
        vm.Confirm = (_, _) => Task.FromResult(false); await vm.ExportDataCommand.ExecuteAsync(null); Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        vm.SaveFile = (_, _) => Task.FromResult<string?>(store.DatabasePath);
        vm.Confirm = (_, _) => Task.FromResult(true); await vm.ExportDataCommand.ExecuteAsync(null); Assert.Contains("outside the managed", vm.Error);
        Assert.True((await store.VerifyAsync()).Healthy);
    }
}
