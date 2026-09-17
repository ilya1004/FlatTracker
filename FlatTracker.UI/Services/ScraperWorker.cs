using System.Globalization;
using System.Text.Json;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Configuration;
using FlatTracker.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace FlatTracker.UI.Services;

public sealed class ScraperWorker(
    IScraperService scraperService,
    IStorageService storageService,
    INotificationService notificationService,
    INotificationPreferencesService notificationPreferences,
    IOptions<ScraperOptions> options,
    ScrapeTrigger trigger,
    ILogger<ScraperWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ResiliencePipeline _retryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(args.AttemptNumber switch
            {
                0 => TimeSpan.FromSeconds(10),
                1 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromSeconds(90)
            }),
            ShouldHandle = new PredicateBuilder()
                .Handle<Exception>()
                .Handle<OperationCanceledException>(),
            OnRetry = args =>
            {
                logger.LogWarning("Retry {Attempt}/3 after {Delay}s: {Exception}",
                    args.AttemptNumber + 1,
                    args.RetryDelay.TotalSeconds,
                    args.Outcome.Exception?.Message ?? "unknown");
                return ValueTask.CompletedTask;
            }
        })
        .Build();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await storageService.EnsureInitializedAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _retryPipeline.ExecuteAsync(async token =>
                {
                    logger.LogInformation("=== Starting scrape run ===");

                    var rawBodies = await scraperService.ScrapeAsync(token);

                    var records = new List<AdRecord>();
                    foreach (var rawBody in rawBodies)
                    {
                        var response = JsonSerializer.Deserialize<KufarSearchResponse>(rawBody, JsonOpts);
                        if (response?.Ads is { Count: > 0 })
                            records.AddRange(response.Ads.Select(item => AdMapper.ToRecord(item)));
                    }

                    if (records.Count == 0)
                    {
                        logger.LogWarning("No ads found in response");
                        return;
                    }

                    var result = await storageService.SaveAdsAsync(records, token);
                    await SendReportAsync(FilterBySelectedDistricts(result), token);

                    logger.LogInformation("=== Scrape complete: {Count} ads across {Pages} pages ===", records.Count, rawBodies.Count);

                    foreach (var ad in records)
                        PrintAd(ad);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scrape run failed after all retries");
            }

            await WaitUntilNextRunAsync(ct);
        }

        logger.LogInformation("Scraper worker shutting down");
    }

    private SaveAdsResult FilterBySelectedDistricts(SaveAdsResult result)
    {
        var selected = notificationPreferences.SelectedDistricts;
        if (selected.Count == 0)
            return result;

        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);

        static string? DistrictOf(AdRecord ad) => ad.District ?? ad.Region;

        var newAds = result.NewAds
            .Where(ad => set.Contains(DistrictOf(ad) ?? string.Empty))
            .ToList();
        var updatedAds = result.UpdatedAds
            .Where(u => set.Contains(DistrictOf(u.Ad) ?? string.Empty))
            .ToList();

        return new SaveAdsResult { NewAds = newAds, UpdatedAds = updatedAds };
    }

    private async Task SendReportAsync(SaveAdsResult result, CancellationToken ct)
    {
        if (result.NewAds.Count == 0 && result.UpdatedAds.Count == 0)
            return;

        await notificationService.NotifyScrapeCompletedAsync(new ScrapeReport
        {
            GeneratedAt = DateTime.Now,
            NewAds = result.NewAds,
            UpdatedAds = result.UpdatedAds
        }, ct);
    }

    private void PrintAd(AdRecord ad)
    {
        var price = ad.Currency == "USD"
            ? $"{ad.PriceUsd?.ToString("F2", CultureInfo.InvariantCulture)} USD"
            : $"{ad.PriceByn?.ToString("F2", CultureInfo.InvariantCulture)} BYN";

        logger.LogInformation(
            "[{AdId}] {Price} | {Rooms} комн. | {Area} м² | {Address} | {Link}",
            ad.AdId,
            price,
            ad.Rooms?.ToString() ?? "-",
            ad.TotalArea?.ToString("F1") ?? "-",
            ad.Address ?? ad.Title,
            ad.AdLink);
    }

    private async Task WaitUntilNextRunAsync(CancellationToken ct)
    {
        var opt = options.Value;
        var minutes = Random.Shared.Next(opt.MinIntervalMinutes, opt.MaxIntervalMinutes + 1);
        var seconds = Random.Shared.Next(0, 60);
        var delay = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);

        logger.LogInformation("Next scrape in {Minutes}m {Seconds}s", minutes, seconds);

        var delayTask = Task.Delay(delay, ct);
        var triggerTask = trigger.WaitAsync(ct);

        await Task.WhenAny(delayTask, triggerTask);
    }
}
