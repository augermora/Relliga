using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Relliga.Desktop;

public sealed partial class MainWindow : Window
{
    private bool closingAfterCancellation;
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.SelectFile = PickFileAsync;
        };
        Closing += async (_, e) =>
        {
            if (closingAfterCancellation || DataContext is not MainViewModel vm || !vm.IsBusy) return;
            e.Cancel = true;
            vm.Cancel();
            if (vm.ImportCommand.ExecutionTask is { } importTask) await importTask;
            if (vm.PickFileCommand.ExecutionTask is { } previewTask) await previewTask;
            if (vm.InitializeCommand.ExecutionTask is { } initializeTask) await initializeTask;
            closingAfterCancellation = true;
            Close();
        };
    }

    private async Task<string?> PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Spotify Extended Streaming History export",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Spotify exports (JSON or ZIP)") { Patterns = ["*.json", "*.zip"] }]
        });
        if (files.Count == 0) return null;
        return files[0].TryGetLocalPath() ?? throw new IOException("Choose a file stored on this device.");
    }
}
