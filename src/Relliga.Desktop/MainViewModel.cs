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
    public string[] Sections { get; } = ["Overview", "Import data", "Timeline", "Search", "Import history", "Library & export", "Settings & about"];
    public string[] SourceOptions { get; } = ["All sources", "Spotify", "Google Calendar"];
    public string[] KindOptions { get; } = ["All event types", "Listening", "Podcast", "Calendar"];
    public ObservableCollection<EventItem> Events { get; } = [];
    public ObservableCollection<ImportItem> RecentImports { get; } = [];
    public string DatabaseLocation => library.DatabasePath;
    public string Version => "0.3.0";
    private CancellationTokenSource? workCancellation;
    private CancellationTokenSource? queryCancellation;
    private int page;
    private long total;
    private int historyPage;
    private long historyTotal;
    private DateTime? suggestedCalendarDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsImport), nameof(IsTimeline), nameof(IsSearch), nameof(IsHistory), nameof(IsLibrary), nameof(IsAbout), nameof(IsExplorer), nameof(PageTitle))]
    private int selectedSection;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickFileCommand), nameof(ImportCommand), nameof(PreviousCommand), nameof(NextCommand), nameof(FilterCommand), nameof(ClearFiltersCommand), nameof(PreviousHistoryCommand), nameof(NextHistoryCommand))]
    private bool isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsInitialization))]
    [NotifyCanExecuteChangedFor(nameof(PickFileCommand), nameof(ImportCommand), nameof(FilterCommand), nameof(ClearFiltersCommand), nameof(PreviousHistoryCommand), nameof(NextHistoryCommand))]
    private bool isReady;
    [ObservableProperty] private bool isQuerying;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool isCancellable;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? error;
    [ObservableProperty] private string status = "Opening your local library…";
    [ObservableProperty] private string selectedFile = "No file selected";
    [ObservableProperty] private string preview = "Choose Spotify history JSON, a Calendar ICS file, or a ZIP containing either format.";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ImportCommand))] private string? importPath;
    [ObservableProperty] private string importResult = "Your import results and any warnings will appear here.";
    [ObservableProperty] private string eventCount = "0";
    [ObservableProperty] private string sourceCount = "0";
    [ObservableProperty] private string successfulImportCount = "0";
    [ObservableProperty] private string sourceStatistics = "Import a source to see its statistics.";
    [ObservableProperty] private string historyPageLabel = "No import operations";
    [ObservableProperty] private string sourceFilter = "All sources";
    [ObservableProperty] private string kindFilter = "All event types";
    [ObservableProperty] private string rangeNote = "Dates use UTC for timed events; date-only and floating events retain their exported calendar date.";
    [ObservableProperty] private string latestImport = "No imports yet";
    [ObservableProperty] private string sources = "No sources imported yet";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string fromDate = "";
    [ObservableProperty] private string toDate = "";
    [ObservableProperty] private string pageLabel = "No events";
    [ObservableProperty] private string emptyMessage = "Your story starts with an import. Add a Spotify or Calendar export to see it here.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedDetails))] private EventItem? selectedEvent;
    [ObservableProperty] private ImportItem? selectedImport;

    public bool IsDashboard => SelectedSection == 0;
    public bool IsImport => SelectedSection == 1;
    public bool IsTimeline => SelectedSection == 2;
    public bool IsSearch => SelectedSection == 3;
    public bool IsHistory => SelectedSection == 4;
    public bool IsLibrary => SelectedSection == 5;
    public bool IsAbout => SelectedSection == 6;
    public bool IsExplorer => IsTimeline || IsSearch;
    public bool HasError => Error is not null;
    public bool HasSelection => SelectedEvent is not null;
    public string SelectedDetails => SelectedEvent?.Details ?? "Select an event to inspect its details and source provenance.";
    public bool NeedsInitialization => !IsReady;
    public string PageTitle => Sections[Math.Clamp(SelectedSection, 0, Sections.Length - 1)];
    public bool CanWork => IsReady && !IsBusy;
    public bool CanImport => CanWork && ImportPath is not null;
    public bool CanPrevious => CanWork && page > 0;
    public bool CanNext => CanWork && (long)(page + 1) * 50 < total;
    public bool CanPreviousHistory => CanWork && historyPage > 0;
    public bool CanNextHistory => CanWork && (long)(historyPage + 1) * 20 < historyTotal;

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
            suggestedCalendarDate = null;
            if (importer is IImportInspector inspector)
            {
                var inspection = await Task.Run(() => inspector.InspectAsync(path, token));
                Preview = inspection.Summary; suggestedCalendarDate = inspection.SuggestedCalendarDate;
            }
            else Preview = await Task.Run(async () =>
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
            Status = "Export ready to import";
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
            Status = "Importing personal history…";
            var result = await Task.Run(() => imports.ImportAsync(ImportPath!, importer.Provider, progress, workCancellation.Token));
            HealthStatus = "The library changed. Run verification for a current health report.";
            appliedQuery = null;
            OnPropertyChanged(nameof(ExportScopeSummary));
            ImportResult = result.Summary;
            if (suggestedCalendarDate is { } date)
            {
                var start = date.Date.AddDays(-7);
                FromDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ToDate = start.AddDays(89).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            Status = "Import complete · Your library is up to date";
        }
        catch (OperationCanceledException) { ImportResult = "Import cancelled. No events from this operation were saved."; Status = "Import cancelled"; }
        catch (Exception ex) { Error = FriendlyError(ex); ImportResult = "Import failed. No events from this operation were saved."; Status = "Import failed"; }
        finally
        {
            IsCancellable = false;
            workCancellation.Dispose(); workCancellation = null;
            historyPage = 0;
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
    private async Task ClearFiltersAsync()
    { SearchText = FromDate = ToDate = ""; SourceFilter = SourceOptions[0]; KindFilter = KindOptions[0]; page = 0; await LoadPageAsync(); }
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
            var query = new EventQuery(IsSearch ? SearchText : null, from, to?.AddDays(1), page,
                Provider: SourceFilter == SourceOptions[0] ? null : SourceFilter, Kind: KindFilter == KindOptions[0] ? null : KindFilter);
            var result = await Task.Run(() => store.QueryAsync(query, cancellation.Token));
            if (cancellation.IsCancellationRequested) return;
            appliedQuery = query with { Page = 0, From = result.WindowFrom ?? query.From, Until = result.WindowUntil ?? query.Until };
            OnPropertyChanged(nameof(ExportScopeSummary));
            total = result.Total;
            Events.Clear();
            foreach (var item in result.Items) Events.Add(new(item));
            SelectedEvent = null;
            PageLabel = total == 0 ? "0 events" : $"{page * 50 + 1:N0}–{Math.Min((page + 1L) * 50, total):N0} of {total:N0} events";
            RangeNote = result.WindowFrom is { } windowFrom && result.WindowUntil is { } windowUntil
                ? $"Window: {windowFrom:yyyy-MM-dd} through {windowUntil.AddDays(-1):yyyy-MM-dd} · Calendar ranges are limited to 366 days."
                : "UTC for timed events; all-day dates and floating wall-clock times are preserved. Calendar defaults to a bounded 90-day window.";
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
        AvailableSources.Clear();
        foreach (var source in summary.BySource ?? []) AvailableSources.Add(source.Provider);
        SelectedRemovalSource = AvailableSources.FirstOrDefault();
        EventCount = summary.Events.ToString("N0");
        SourceCount = summary.Sources.ToString();
        SuccessfulImportCount = summary.SuccessfulImports.ToString("N0");
        Sources = summary.Sources == 0 ? "No sources imported yet" : string.Join(" · ", summary.BySource!.Select(x => x.Provider));
        SourceStatistics = string.Join("\n", (summary.BySource ?? []).Select(x => x.Provider == "Google Calendar"
            ? $"Google Calendar · {x.Events:N0} events/series · {x.RecurringDefinitions:N0} recurring series"
            : $"{x.Provider} · {x.Events:N0} playback events · {x.DurationMs / 3_600_000d:N1} hours played"));
        if (summary.Sources == 0) SourceStatistics = "Import a source to see its statistics.";
        historyTotal = summary.TotalImports;
        LatestImport = summary.LatestImport is { } last ? $"{last.Status}\n{last.StartedAt.ToLocalTime():dd MMM yyyy · HH:mm}" : "No imports yet";
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var history = await Task.Run(() => store.GetImportsAsync(page: historyPage));
        RecentImports.Clear();
        foreach (var item in history) RecentImports.Add(new(item));
        SelectedImport = RecentImports.FirstOrDefault();
        HistoryPageLabel = historyTotal == 0 ? "No import operations" : $"{historyPage * 20 + 1:N0}–{Math.Min((historyPage + 1L) * 20, historyTotal):N0} of {historyTotal:N0} imports";
        PreviousHistoryCommand.NotifyCanExecuteChanged(); NextHistoryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPreviousHistory))]
    private async Task PreviousHistoryAsync() { historyPage--; await GuardHistoryAsync(); }
    [RelayCommand(CanExecute = nameof(CanNextHistory))]
    private async Task NextHistoryAsync() { historyPage++; await GuardHistoryAsync(); }
    private async Task GuardHistoryAsync()
    {
        try { await LoadHistoryAsync(); }
        catch (Exception ex) { Error = "Could not load import history: " + ex.Message; }
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
