using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Desktop;

public partial class MainViewModel
{
    public Func<string, string, Task<string?>>? SaveFile { get; set; }
    public Func<Task<string?>>? SelectBackup { get; set; }
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    public string[] ExportFormats { get; } = ["JSON · full metadata & provenance", "CSV · spreadsheet"];
    public string[] ExportScopes { get; } = ["Current applied timeline / search filters", "Spotify · all playback", "Google Calendar · date range", "All sources · date range", "Telegram · all messages"];
    public ObservableCollection<string> AvailableSources { get; } = [];
    private EventQuery? appliedQuery;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ExportScopeSummary), nameof(ExportUsesDates))] [NotifyCanExecuteChangedFor(nameof(ExportDataCommand))] private int exportScope;
    [ObservableProperty] private int exportFormat;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RemoveSourceCommand))] private string? selectedRemovalSource;
    [ObservableProperty] private string healthStatus = "Run verification to check SQLite, provenance and search indexes. Verification makes no repairs.";
    [ObservableProperty] private string ownershipResult = "Export, back up, and manage the information held in this library.";
    public bool ExportUsesDates => ExportScope is 2 or 3;
    public bool CanExport => CanWork && !IsQuerying && (ExportScope != 0 || appliedQuery is not null);
    public bool CanRemoveSource => CanWork && SelectedRemovalSource is not null;
    public bool CanRemoveImport => CanWork && SelectedImport is not null;
    private int mutationAttempts;
    public string ExportScopeSummary => ExportScope == 0
        ? appliedQuery is null ? "Apply Timeline or Search filters first. Every matching record will be exported."
            : $"All matches · {appliedQuery.Provider ?? "all sources"} · {appliedQuery.Kind ?? "all types"} · text: {appliedQuery.Text ?? "none"} · {appliedQuery.From?.ToString("u") ?? "start"} → {appliedQuery.Until?.ToString("u") ?? "end"} (exclusive)"
        : ExportScope == 1 ? "All Spotify playback, without a date limit. JSON retains all import origins; CSV includes selected provenance and source metadata."
        : ExportScope == 4 ? "All locally stored Telegram messages, without a date limit, including metadata and import origins. Session secrets are excluded."
        : "All matching Calendar occurrences / playback in the dates below. Blank dates use Timeline's 90-day Calendar default. Maximum Calendar window: 366 days.";

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanWork)); NotifyLibraryCommands(); }
    partial void OnIsReadyChanged(bool value) { OnPropertyChanged(nameof(CanWork)); NotifyLibraryCommands(); }
    private void PrepareLibraryMutation()
    {
        mutationAttempts++; appliedQuery = null;
        HealthStatus = "The library may have changed. Run verification for a current health report.";
        OnPropertyChanged(nameof(ExportScopeSummary)); ExportDataCommand.NotifyCanExecuteChanged();
    }
    private void NotifyLibraryCommands()
    {
        ExportDataCommand.NotifyCanExecuteChanged(); CreateBackupCommand.NotifyCanExecuteChanged(); RestoreBackupCommand.NotifyCanExecuteChanged();
        VerifyLibraryCommand.NotifyCanExecuteChanged(); RemoveSourceCommand.NotifyCanExecuteChanged(); RemoveImportCommand.NotifyCanExecuteChanged();
        DiscardRecoveryCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> AskAsync(string title, string message) => Confirm is not null && await Confirm(title, message);
    private async Task<(string Path, bool Overwrite)?> DestinationAsync(string name, string extension)
    {
        var path = SaveFile is null ? null : await SaveFile(name, extension);
        if (path is null) return null;
        var exists = File.Exists(path);
        if (exists && !await AskAsync("Replace destination?", $"Replace {path}? The existing file will remain intact if this operation fails before publication.")) return null;
        return (path, exists);
    }

    private async Task LibraryWorkAsync(string initial, Func<CancellationToken, IProgress<OperationProgress>, Task<string>> operation)
    {
        if (!CanWork) return;
        queryCancellation?.Cancel(); IsBusy = true; IsCancellable = true; Error = null;
        workCancellation = new(); Status = initial;
        var beforeMutation = mutationAttempts;
        try
        {
            var operationCancellation = workCancellation;
            OwnershipResult = await operation(workCancellation.Token, new Progress<OperationProgress>(p =>
            {
                if (ReferenceEquals(workCancellation, operationCancellation)) Status = p.Message;
            }));
            Status = OwnershipResult;
        }
        catch (OperationCanceledException) { Status = "Operation cancelled safely"; OwnershipResult = "Cancelled. No final output or library replacement was published."; }
        catch (Exception ex) { Error = ex.Message; Status = "Library operation failed · Review the error above"; }
        finally
        {
            IsCancellable = false; workCancellation.Dispose(); workCancellation = null;
            var refresh = mutationAttempts != beforeMutation;
            if (refresh)
            {
                historyPage = 0;
                try { await RefreshSummaryAsync(); }
                catch (Exception ex) { Error = (Error is null ? "" : Error + "\n") + "Could not refresh the library: " + ex.Message; }
                Events.Clear(); SelectedEvent = null;
            }
            OnPropertyChanged(nameof(HasEvents)); OnPropertyChanged(nameof(HasNoEvents)); OnPropertyChanged(nameof(ExportScopeSummary));
            IsBusy = false;
            if (refresh && IsExplorer) await LoadPageAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportDataAsync() => LibraryWorkAsync("Preparing export…", async (ct, progress) =>
    {
        var query = ExportScope switch
        {
            0 => appliedQuery ?? throw new InvalidOperationException("Apply Timeline or Search filters before exporting current results."),
            1 => new EventQuery(Provider: "Spotify"),
            4 => new EventQuery(Provider: "Telegram", Kind: "Message"),
            2 or 3 => new EventQuery(From: ParseDate(FromDate), Until: ParseDate(ToDate)?.AddDays(1), Provider: ExportScope == 2 ? "Google Calendar" : null),
            _ => throw new InvalidOperationException("Choose an export scope.")
        };
        var format = ExportFormat == 0 ? Application.ExportFormat.Json : Application.ExportFormat.Csv;
        var destination = await DestinationAsync("Relliga-events", format == Application.ExportFormat.Json ? "json" : "csv");
        if (destination is null) return "Export cancelled";
        var result = await Task.Run(() => library.ExportAsync(new(destination.Value.Path, format, query, destination.Value.Overwrite), progress, ct));
        return $"Exported {result.Records:N0} records to {result.Destination}";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task CreateBackupAsync() => LibraryWorkAsync("Preparing offline backup…", async (ct, progress) =>
    {
        var destination = await DestinationAsync("Relliga-backup-" + DateTime.Now.ToString("yyyy-MM-dd"), "zip");
        if (destination is null) return "Backup cancelled";
        await Task.Run(() => library.CreateAsync(destination.Value.Path, destination.Value.Overwrite, progress, ct));
        return "Backup verified and saved to " + destination.Value.Path;
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RestoreBackupAsync() => LibraryWorkAsync("Select a Relliga backup…", async (ct, progress) =>
    {
        var path = SelectBackup is null ? null : await SelectBackup();
        if (path is null) return "Restore cancelled";
        Status = "Validating backup structure, checksums and database…";
        var preview = await Task.Run(() => library.InspectAsync(path, ct));
        if (!await AskAsync("Replace the current library?", preview.Description)) return "Restore cancelled · Current library unchanged";
        if (synchronization is not null) await synchronization.PauseAsync(ct);
        PrepareLibraryMutation();
        await Task.Run(() => library.RestoreAsync(preview, progress, ct));
        return "Library restored and reopened. The previous library is retained in relliga.db.previous.";
    });

    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private Task RemoveSourceAsync() => RemoveAsync(() => new(Provider: SelectedRemovalSource ?? throw new InvalidOperationException("Select an imported source.")));
    [RelayCommand(CanExecute = nameof(CanRemoveImport))]
    private Task RemoveImportAsync() => RemoveAsync(() => new(ImportId: SelectedImport?.Record.Id ?? throw new InvalidOperationException("Select a batch in Import history first.")));
    private Task RemoveAsync(Func<DeletionTarget> target) => LibraryWorkAsync("Preparing deletion preview…", async (ct, _) =>
    {
        var selected = target();
        if ((selected.Provider == "Telegram" || selected.ImportId is not null && SelectedImport?.Record.Provider.Contains("Telegram", StringComparison.Ordinal) == true) &&
            await library.GetConnectionAsync("Telegram", ct) is { Automatic: true }) throw new InvalidOperationException("Pause or disconnect Telegram in Connected accounts before deleting its local history, so automatic synchronization cannot repopulate it.");
        var preview = await Task.Run(() => library.PreviewDeletionAsync(selected, ct));
        if (!await AskAsync("Remove imported data?", preview.Explanation)) return "Deletion cancelled · Library unchanged";
        PrepareLibraryMutation();
        await Task.Run(() => library.DeleteAsync(preview, ct));
        return $"Removed {preview.RemovedRecords:N0} stored records; retained {preview.SharedRecords:N0} shared records.";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task VerifyLibraryAsync() => LibraryWorkAsync("Verifying library health…", async (ct, _) =>
    {
        var result = await Task.Run(() => library.VerifyAsync(ct)); HealthStatus = result.Description;
        return result.Healthy ? "Library health checks passed" : "Verification found problems. Review Library health in Settings.";
    });
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task DiscardRecoveryAsync() => LibraryWorkAsync("Review recovery copy…", async (ct, _) =>
    {
        if (!await AskAsync("Discard the previous library?", "Permanently remove Relliga's local restore recovery copy (if present)? The active library and your external backup archives remain. This is logical deletion, not secure erasure.")) return "Recovery copy retained";
        await Task.Run(() => library.DiscardRecoveryAsync(ct)); return "Previous library recovery copy removed (if present)";
    });

    public async Task WaitForLibraryWorkAsync()
    {
        foreach (var command in new IAsyncRelayCommand[] { ExportDataCommand, CreateBackupCommand, RestoreBackupCommand,
            RemoveSourceCommand, RemoveImportCommand, VerifyLibraryCommand, DiscardRecoveryCommand })
            if (command.ExecutionTask is { } task) await task;
        await library.Activity.RunAsync(() => Task.CompletedTask);
    }
    public void StopBackgroundQueries() => queryCancellation?.Cancel();
}
