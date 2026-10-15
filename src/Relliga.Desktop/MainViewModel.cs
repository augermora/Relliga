using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Infrastructure;

namespace Relliga.Desktop;

public partial class MainViewModel(IHistoryStore store, ImportService imports, IDataImporter importer, SqliteHistoryStore library) : ObservableObject
{
    public Func<Task<string?>>? SelectFile { get; set; }
    public string[] Sections { get; } = ["Overview", "Import data", "Timeline", "Search", "Settings & about"];
    public ObservableCollection<EventItem> Events { get; } = [];
    public ObservableCollection<ImportItem> RecentImports { get; } = [];
    public string DatabaseLocation => library.DatabasePath;
    public string Version => "0.1.0";
    private CancellationTokenSource? workCancellation;
    private CancellationTokenSource? queryCancellation;
    private int page;
    private long total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsImport), nameof(IsTimeline), nameof(IsSearch), nameof(IsAbout), nameof(IsExplorer), nameof(PageTitle))]
    private int selectedSection;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickFileCommand), nameof(ImportCommand), nameof(PreviousCommand), nameof(NextCommand), nameof(FilterCommand), nameof(ClearFiltersCommand))]
    private bool isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsInitialization))]
    [NotifyCanExecuteChangedFor(nameof(PickFileCommand), nameof(ImportCommand), nameof(FilterCommand), nameof(ClearFiltersCommand))]
    private bool isReady;
    [ObservableProperty] private bool isQuerying;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool isCancellable;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? error;
    [ObservableProperty] private string status = "Opening your local library…";
    [ObservableProperty] private string selectedFile = "No file selected";
    [ObservableProperty] private string preview = "Choose a JSON file or ZIP archive from your Spotify Extended Streaming History export.";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private string? importPath;
    [ObservableProperty] private string importResult = "Your import results and any warnings will appear here.";
    [ObservableProperty] private string eventCount = "0";
    [ObservableProperty] private string sourceCount = "0";
    [ObservableProperty] private string latestImport = "No imports yet";
    [ObservableProperty] private string sources = "No sources imported yet";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string fromDate = "";
    [ObservableProperty] private string toDate = "";
    [ObservableProperty] private string pageLabel = "No events";
    [ObservableProperty] private string emptyMessage = "Your story starts with an import. Add your Spotify export to see it here.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedDetails))] private EventItem? selectedEvent;
    [ObservableProperty] private ImportItem? selectedImport;

    public bool IsDashboard => SelectedSection == 0;
    public bool IsImport => SelectedSection == 1;
    public bool IsTimeline => SelectedSection == 2;
    public bool IsSearch => SelectedSection == 3;
    public bool IsAbout => SelectedSection == 4;
    public bool IsExplorer => IsTimeline || IsSearch;
    public bool HasError => Error is not null;
    public bool HasSelection => SelectedEvent is not null;
    public string SelectedDetails => SelectedEvent?.Details ?? "Select a listening event to inspect its metadata and provenance.";
    public bool NeedsInitialization => !IsReady;
    public string PageTitle => Sections[Math.Clamp(SelectedSection, 0, 4)];
    public bool CanWork => IsReady && !IsBusy;
    public bool CanImport => CanWork && ImportPath is not null;
    public bool CanPrevious => CanWork && page > 0;
    public bool CanNext => CanWork && (long)(page + 1) * 50 < total;

    partial void OnSelectedSectionChanged(int value)
    {
        page = 0;
        SelectedEvent = null;
        if (IsReady && IsExplorer) FilterCommand.Execute(null);
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsBusy = true;
        IsReady = false;
        Error = null;
        try
        {
            await Task.Run(() => store.InitializeAsync());
            await RefreshSummaryAsync();
            IsReady = true;
            Status = "Local library ready · Only on this device";
        }
        catch (Exception ex) { Error = "Could not open the library. Close any other Relliga window, check storage permissions, then retry.\n" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void ShowImport() => SelectedSection = 1;
    [RelayCommand] private void ShowTimeline() => SelectedSection = 2;
    [RelayCommand] private void DismissError() => Error = null;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task PickFileAsync()
    {
        Error = null;
        IsBusy = true;
        workCancellation = new();
        IsCancellable = true;
        try
        {
            var path = SelectFile is null ? null : await SelectFile();
            if (path is null) return;
            ImportPath = null;
            SelectedFile = Path.GetFileName(path);
            Status = "Inspecting file contents…";
            var token = workCancellation.Token;
            Preview = await Task.Run(async () =>
            {
                int valid = 0, warnings = 0;
                DigitalEvent? first = null;
                await foreach (var record in importer.ReadAsync(path, null, token))
                {
                    if (record.Event is not null) { valid++; first ??= record.Event; }
                    else warnings++;
                    if (valid >= 1000) break;
                }
                return $"Detected: Spotify Extended Streaming History\n{valid:N0}{(valid == 1000 ? "+" : "")} valid events in preview · {warnings:N0} sampled warnings\n" +
                    $"First sampled item: {first?.Title ?? "Title not provided"}\nFull validation runs during import.";
            }, token);
            ImportPath = path;
            Status = "Spotify export ready to import";
        }
        catch (OperationCanceledException) { Preview = "Inspection cancelled. Choose a file to try again."; Status = "Inspection cancelled"; }
        catch (Exception ex) { Preview = "This file could not be prepared for import."; Error = FriendlyError(ex); Status = "File inspection failed"; }
        finally
        {
            IsCancellable = false;
            IsBusy = false; workCancellation.Dispose(); workCancellation = null;
            if (IsExplorer && IsReady) await LoadPageAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        IsBusy = true;
        Error = null;
        workCancellation = new();
        IsCancellable = true;
        var progress = new Progress<ImportProgress>(p => Status = p.Message);
        try
        {
            Status = "Importing Spotify history…";
            var result = await Task.Run(() => imports.ImportAsync(ImportPath!, "Spotify", progress, workCancellation.Token));
            ImportResult = result.Summary;
            Status = "Import complete · Your library is up to date";
        }
        catch (OperationCanceledException) { ImportResult = "Import cancelled. No events from this operation were saved."; Status = "Import cancelled"; }
        catch (Exception ex) { Error = FriendlyError(ex); ImportResult = "Import failed. No events from this operation were saved."; Status = "Import failed"; }
        finally
        {
            IsCancellable = false;
            workCancellation.Dispose(); workCancellation = null;
            try { await RefreshSummaryAsync(); }
            catch (Exception ex) { Error = "Could not refresh the library: " + ex.Message; }
            IsBusy = false;
            if (IsExplorer) await LoadPageAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsCancellable))]
    public void Cancel()
    {
        if (workCancellation is null) return;
        workCancellation.Cancel(); IsCancellable = false; Status = "Cancelling safely…";
    }

    [RelayCommand(CanExecute = nameof(CanWork), AllowConcurrentExecutions = true)]
    private async Task FilterAsync() { page = 0; await LoadPageAsync(); }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ClearFiltersAsync() { SearchText = FromDate = ToDate = ""; page = 0; await LoadPageAsync(); }
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private async Task PreviousAsync() { page--; await LoadPageAsync(); }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync() { page++; await LoadPageAsync(); }

    private async Task LoadPageAsync()
    {
        queryCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        IsQuerying = true;
        Error = null;
        try
        {
            var from = ParseDate(FromDate);
            var to = ParseDate(ToDate);
            if (from > to) throw new FormatException("The start date must be on or before the end date.");
            var query = new EventQuery(IsSearch ? SearchText : null, from, to?.AddDays(1), page);
            var result = await Task.Run(() => store.QueryAsync(query, cancellation.Token));
            if (cancellation.IsCancellationRequested) return;
            total = result.Total;
            Events.Clear();
            foreach (var item in result.Items) Events.Add(new(item));
            SelectedEvent = null;
            PageLabel = total == 0 ? "0 events" : $"{page * 50 + 1:N0}–{Math.Min((page + 1L) * 50, total):N0} of {total:N0} events";
            EmptyMessage = "No events match these filters. Adjust your search or import more history.";
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(HasNoEvents));
            PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded queries are cancelled deliberately; only the latest request updates the view.
        }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Error = FriendlyError(ex); }
        finally
        {
            if (ReferenceEquals(queryCancellation, cancellation)) { queryCancellation = null; IsQuerying = false; }
            cancellation.Dispose();
        }
    }
    public bool HasEvents => Events.Count > 0;
    public bool HasNoEvents => !HasEvents;

    private async Task RefreshSummaryAsync()
    {
        var summary = await Task.Run(() => store.GetSummaryAsync());
        EventCount = summary.Events.ToString("N0");
        SourceCount = summary.Sources.ToString();
        Sources = summary.Sources == 0 ? "No sources imported yet" : "Spotify · Extended Streaming History";
        LatestImport = summary.LatestImport is { } last ? $"{last.Status}\n{last.StartedAt.ToLocalTime():dd MMM yyyy · HH:mm}" : "No imports yet";
        var history = await Task.Run(() => store.GetImportsAsync());
        RecentImports.Clear();
        foreach (var item in history) RecentImports.Add(new(item));
        SelectedImport = RecentImports.FirstOrDefault();
    }

    private static DateTimeOffset? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException("Enter dates as YYYY-MM-DD, for example 2025-01-31. Dates use UTC.");
        if (date == DateTime.MaxValue.Date) throw new FormatException("Choose a date before 9999-12-31.");
        return new DateTimeOffset(date, TimeSpan.Zero);
    }
    private static string FriendlyError(Exception ex) => ex is System.Text.Json.JsonException
        ? "The JSON is empty, damaged, too deeply nested, or is not a JSON array. No events were saved.\n" + ex.Message
        : ex.Message;
}
