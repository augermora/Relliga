using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Relliga.Application;
using Relliga.Importers;
using Relliga.Infrastructure;

namespace Relliga.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var collection = new ServiceCollection();
            var dataDirectory = Environment.GetEnvironmentVariable("RELLIGA_DATA_DIRECTORY");
            string databasePath;
            try { databasePath = AppDataPaths.ResolveDatabasePath(dataDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                var close = new Avalonia.Controls.Button { Content = "Close Relliga", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
                var failure = new Avalonia.Controls.Window
                {
                    Title = "Relliga — Library location unavailable", Width = 640, Height = 350,
                    Content = new Avalonia.Controls.StackPanel
                    {
                        Margin = new Thickness(28), Spacing = 20, Children =
                        {
                            new Avalonia.Controls.TextBlock { Text = "Choose a safe library location", FontSize = 23 },
                            new Avalonia.Controls.TextBlock { Text = ex.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close
                        }
                    }
                };
                close.Click += (_, _) => failure.Close(); desktop.MainWindow = failure;
                System.Diagnostics.Trace.TraceError(ex.ToString());
                base.OnFrameworkInitializationCompleted(); return;
            }
            collection.AddSingleton<ICalendarExpander, CalendarExpander>();
            collection.AddSingleton(s => new SqliteHistoryStore(databasePath, s.GetRequiredService<ICalendarExpander>()));
            collection.AddSingleton<IHistoryStore>(s => s.GetRequiredService<SqliteHistoryStore>());
            collection.AddSingleton<IDataImporter, ExportImporter>();
            collection.AddSingleton<ImportService>();
            var connectionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relliga", "Connections");
            var native = Environment.GetEnvironmentVariable("RELLIGA_TDLIB_PATH") ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tdjson.dll" : OperatingSystem.IsMacOS() ? "libtdjson.dylib" : "libtdjson.so");
            collection.AddSingleton<ICredentialVault>(new WindowsCredentialVault(Path.Combine(connectionDirectory, "Vault")));
            collection.AddSingleton(s => new TelegramConfiguration(s.GetRequiredService<ICredentialVault>(), Path.Combine(connectionDirectory, "TDLib"), databasePath, native));
            collection.AddSingleton<ITdClientFactory>(new NativeTdClientFactory(native));
            collection.AddSingleton<TelegramProvider>();
            collection.AddSingleton<IConnectionStore>(s => s.GetRequiredService<SqliteHistoryStore>());
            collection.AddSingleton(s => new SynchronizationService(s.GetRequiredService<IConnectionStore>(), s.GetRequiredService<TelegramProvider>()));
            collection.AddSingleton<MainViewModel>();
            services = collection.BuildServiceProvider();
            var window = new MainWindow { DataContext = services.GetRequiredService<MainViewModel>() };
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await ((MainViewModel)window.DataContext!).InitializeCommand.ExecuteAsync(null);
            desktop.Exit += (_, _) => services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
