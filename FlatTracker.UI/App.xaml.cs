using System.IO;
using System.Windows;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Configuration;
using FlatTracker.Infrastructure.Configuration;
using FlatTracker.Infrastructure.Data;
using FlatTracker.Infrastructure.Services;
using FlatTracker.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace FlatTracker.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        Log.Logger = BuildLoggerConfiguration().CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
            })
            .UseSerilog((context, services, configuration) => BuildLoggerConfiguration(), writeToProviders: true)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.Services.AddSingleton<ILoggerProvider, UiLoggerProvider>();
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton<UiLogSink>();
                services.Configure<ScraperOptions>(
                    ctx.Configuration.GetSection(ScraperOptions.SectionName));
                services.Configure<TelegramOptions>(
                    ctx.Configuration.GetSection(TelegramOptions.SectionName));

                var outputDir = ctx.Configuration
                    .GetSection(ScraperOptions.SectionName)["OutputDirectory"];
                var outputPath = Path.GetFullPath(outputDir ?? "./output");
                Directory.CreateDirectory(outputPath);
                var dbPath = Path.Combine(outputPath, "flats.db");

                services.AddDbContextFactory<AppDbContext>(o =>
                    o.UseSqlite($"Data Source={dbPath}"));

                var preferencesPath = Path.Combine(outputPath, "notification-preferences.json");
                services.AddSingleton<INotificationPreferencesService>(
                    new JsonNotificationPreferencesService(preferencesPath));

                services.AddSingleton<UserAgentPool>();
                services.AddSingleton<ScrapeTrigger>();
                services.AddSingleton<IScraperService, ScraperService>();
                services.AddSingleton<IStorageService, SqliteStorageService>();
                services.AddSingleton<INotificationService, TelegramNotificationService>();
                services.AddHostedService<ScraperWorker>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.Services.GetRequiredService<INotificationPreferencesService>()
            .LoadAsync(CancellationToken.None);

        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        var opts = _host.Services.GetRequiredService<IOptions<ScraperOptions>>().Value;
        logger.LogInformation("FlatTracker started. Target: {Url}", opts.TargetUrl);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static LoggerConfiguration BuildLoggerConfiguration() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .WriteTo.Console(
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u4}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File("logs/flattracker-.log", rollingInterval: RollingInterval.Day);
}
