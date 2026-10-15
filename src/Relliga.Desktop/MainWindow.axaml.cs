using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Relliga.Desktop;

public sealed partial class MainWindow : Window
{
    private bool closingAfterCancellation;
    public MainWindow()
    {
        InitializeComponent();
        Activated += async (_,_) => { if (DataContext is MainViewModel vm) await vm.SetConnectionPresenceAsync(true); };
        Deactivated += async (_,_) => { if (DataContext is MainViewModel vm) await vm.SetConnectionPresenceAsync(false); };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectFile = PickFileAsync;
                vm.SaveFile = SaveFileAsync;
                vm.SelectBackup = PickBackupAsync;
                vm.Confirm = ConfirmAsync;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.SelectedSection) && vm.IsSearch)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => SearchBox.Focus());
                };
            }
        };
        Closing += async (_, e) =>
        {
            if (closingAfterCancellation || DataContext is not MainViewModel vm) return;
            e.Cancel = true;
            await vm.StopConnectionsAsync();
            vm.Cancel();
            vm.StopBackgroundQueries();
            if (vm.ImportCommand.ExecutionTask is { } importTask) await importTask;
            if (vm.PickFileCommand.ExecutionTask is { } previewTask) await previewTask;
            if (vm.InitializeCommand.ExecutionTask is { } initializeTask) await initializeTask;
            await vm.WaitForLibraryWorkAsync();
            closingAfterCancellation = true;
            Close();
        };
    }

    private async Task<string?> SaveFileAsync(string name, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Relliga " + extension.ToUpperInvariant(), SuggestedFileName = name + "." + extension,
            DefaultExtension = extension, ShowOverwritePrompt = false,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }]
        });
        return file is null ? null : file.TryGetLocalPath() ?? throw new IOException("Choose a local destination.");
    }
    private async Task<string?> PickBackupAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Relliga backup", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Relliga backup (ZIP)") { Patterns = ["*.zip"] }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new IOException("Choose a backup stored on this device.");
    }
    private async Task<bool> ConfirmAsync(string title, string explanation)
    {
        var dialog = new Window
        {
            Title = title, Width = 620, Height = 440, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Avalonia.Media.Brush.Parse("#141D2B")
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var action = title.StartsWith("Disconnect", StringComparison.Ordinal) ? "Disconnect account" : title.StartsWith("Remove", StringComparison.Ordinal) ? "Remove data" :
            title.Contains("current library", StringComparison.Ordinal) ? "Replace library" :
            title.StartsWith("Discard", StringComparison.Ordinal) ? "Discard recovery copy" : "Replace file";
        var confirm = new Button { Content = action, Classes = { "primary" } };
        Avalonia.Automation.AutomationProperties.SetHelpText(confirm, "This action requires explicit confirmation. Cancel is the initial keyboard focus.");
        cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
        var layout = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"), Margin = new Avalonia.Thickness(24), RowSpacing = 20 };
        layout.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var scroll = new ScrollViewer { Content = new TextBlock { Text = explanation, TextWrapping = Avalonia.Media.TextWrapping.Wrap, LineHeight = 24 } };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(confirm); Grid.SetRow(actions, 2); layout.Children.Add(actions);
        dialog.Content = layout;
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<string?> PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Spotify, Calendar or Telegram export",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Personal data exports (JSON, ICS or ZIP)") { Patterns = ["*.json", "*.ics", "*.zip"] }]
        });
        if (files.Count == 0) return null;
        return files[0].TryGetLocalPath() ?? throw new IOException("Choose a file stored on this device.");
    }
}
