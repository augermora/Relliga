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
            collection.AddSingleton<ICalendarExpander, CalendarExpander>();
            collection.AddSingleton(s => new SqliteHistoryStore(string.IsNullOrWhiteSpace(dataDirectory)
                ? SqliteHistoryStore.DefaultDatabasePath : Path.Combine(Path.GetFullPath(dataDirectory), "relliga.db"),
                s.GetRequiredService<ICalendarExpander>()));
            collection.AddSingleton<IHistoryStore>(s => s.GetRequiredService<SqliteHistoryStore>());
            collection.AddSingleton<IDataImporter, ExportImporter>();
            collection.AddSingleton<ImportService>();
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
