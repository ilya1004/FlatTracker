using System.IO;
using System.Threading.Channels;
using System.Windows;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Configuration;
using FlatTracker.Core.Models;
using FlatTracker.Infrastructure.Configuration;
using FlatTracker.Infrastructure.Data;
using FlatTracker.Infrastructure.Services;
using FlatTracker.UI.Services;
using FlatTracker.UI.Workers;
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

        Log.Logger = BuildLoggerConfiguration().CreateLogger();

        try
        {
            await StartHostAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Не удалось запустить приложение");
            await Log.CloseAndFlushAsync();

            MessageBox.Show(
                $"Не удалось запустить FlatTracker.\n\n{ex.Message}",
                "FlatTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private async Task StartHostAsync()
    {
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
                services.Configure<LlmOptions>(
                    ctx.Configuration.GetSection(LlmOptions.SectionName));
                services.Configure<FilterOptions>(
                    ctx.Configuration.GetSection(FilterOptions.SectionName));

                var filterOptions = ctx.Configuration
                    .GetSection(FilterOptions.SectionName).Get<FilterOptions>() ?? new FilterOptions();

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

                services.AddSingleton(new MonitorStateStore(Path.Combine(outputPath, "monitor-state.json")));

                // Ограниченная очередь: LLM медленнее Telegram, без backpressure
                // сообщения копятся в памяти без предела.
                services.AddSingleton(_ => Channel.CreateBounded<QueuedMessage>(
                    new BoundedChannelOptions(filterOptions.MaxQueueSize)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    }));

                services.AddSingleton<UserAgentPool>();
                services.AddSingleton<ScrapeTrigger>();
                services.AddSingleton<ScrapeRunTracker>();
                services.AddSingleton<IScraperService, ScraperService>();
                services.AddSingleton<IStorageService, SqliteStorageService>();
                services.AddSingleton<INotificationService, TelegramNotificationService>();

                services.AddSingleton<LlmParsingService>();
                services.AddSingleton<MessageDeduplicator>();
                services.AddSingleton<MessagePreFilter>();
                services.AddSingleton<TelegramClientService>();

                services.AddHostedService<KufarScraperWorker>();
                services.AddHostedService<TelegramMonitorWorker>();
                services.AddHostedService<TelegramMonitorPipelineWorker>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.Services.GetRequiredService<INotificationPreferencesService>()
            .LoadAsync(CancellationToken.None);

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        var opts = _host.Services.GetRequiredService<IOptions<ScraperOptions>>().Value;
        logger.LogInformation("FlatTracker started. Target: {Url}", opts.TargetUrl);

        // Схему БД создаём до показа окна: MainWindow при загрузке сразу читает
        // районы из Ads, и без таблицы падает с "no such table: Ads".
        await _host.Services.GetRequiredService<IStorageService>()
            .EnsureInitializedAsync(CancellationToken.None);

        // Окно показываем ДО старта хоста: WTelegram запрашивает код подтверждения
        // синхронно из фонового потока, и ему нужен работающий WPF message loop.
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(10));

                var telegram = _host.Services.GetService<TelegramClientService>();
                if (telegram is not null)
                    await telegram.DisposeAsync();

                var llm = _host.Services.GetService<LlmParsingService>();
                if (llm is not null)
                    await llm.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка при корректном завершении");
            }

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
