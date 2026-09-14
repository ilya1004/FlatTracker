using System.Text.Json;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace FlatTracker.Infrastructure.Services;

public sealed class ScraperService(
    IOptions<ScraperOptions> options,
    UserAgentPool userAgentPool,
    ILogger<ScraperService> logger) : IScraperService
{
    public async Task<IReadOnlyList<string>> ScrapeAsync(CancellationToken ct)
    {
        var opt = options.Value;
        var timeoutMs = (opt.TimeoutSeconds <= 0 ? 30 : opt.TimeoutSeconds) * 1000d;

        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = opt.Headless
        });

        try
        {
            await using var context = await browser.NewContextAsync(BuildContextOptions(opt, userAgentPool));
            var page = await context.NewPageAsync();

            page.Response += (_, response) =>
            {
                if (!response.Url.Contains(opt.ApiUrlPattern, StringComparison.OrdinalIgnoreCase))
                    return;

                var status = response.Status;
                if (status is 403 or 429 or 503)
                    logger.LogWarning("Suspicious HTTP status {Status} from {Url} — protection may have triggered", status, response.Url);
            };

            if (!string.IsNullOrWhiteSpace(opt.TargetUrl))
            {
                logger.LogInformation("Loading page {Url}", opt.TargetUrl);
                await page.GotoAsync(opt.TargetUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = (float)(timeoutMs + 5000)
                });
            }

            var bodies = new List<string>();

            var firstBody = await CaptureBodyAsync(page, opt, timeoutMs, ct);
            bodies.Add(firstBody);

            if (!string.IsNullOrWhiteSpace(opt.PaginationNextSelector))
            {
                var (total, seen) = GetPageSummary(firstBody);

                while (seen < total && bodies.Count < Math.Max(1, opt.MaxPages))
                {
                    var nextBody = await TryCaptureNextPageAsync(page, opt, timeoutMs, ct);
                    if (nextBody is null)
                        break;

                    var pageCount = GetPageSummary(nextBody).Seen;
                    if (pageCount == 0)
                        break;

                    bodies.Add(nextBody);
                    seen += pageCount;
                }

                if (seen < total)
                    logger.LogInformation("Pagination finished early: collected {Seen}/{Total} ads across {Pages} pages", seen, total, bodies.Count);
            }

            return bodies;
        }
        finally
        {
            await browser.DisposeAsync();
        }
    }

    private async Task<string> CaptureBodyAsync(
        IPage page,
        ScraperOptions opt,
        double timeoutMs,
        CancellationToken ct)
    {
        try
        {
            var response = await TriggerAndCaptureAsync(page, opt, timeoutMs, ct);
            return await ReadBodyAsync(response);
        }
        catch (Exception ex) when (IsTimeoutException(ex))
        {
            if (string.IsNullOrWhiteSpace(opt.ApiRequest))
                throw new InvalidOperationException("API response was not intercepted and ApiRequest is not configured", ex);

            logger.LogInformation("Page did not fire the expected API call, requesting API directly: {Url}", opt.ApiRequest);

            var directResponse = await page.GotoAsync(opt.ApiRequest, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)(timeoutMs + 5000)
            }) ?? throw new InvalidOperationException("Direct API navigation returned no response");

            return await ReadBodyAsync(directResponse);
        }
    }

    private async Task<string?> TryCaptureNextPageAsync(
        IPage page,
        ScraperOptions opt,
        double timeoutMs,
        CancellationToken ct)
    {
        var locator = page.Locator(opt.PaginationNextSelector).First;

        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 3000
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Pagination next link not found: {Message}", ex.Message);
            return null;
        }

        var waitTask = page.WaitForResponseAsync(response =>
            response.Url.Contains(opt.ApiUrlPattern, StringComparison.OrdinalIgnoreCase),
            new PageWaitForResponseOptions { Timeout = (float)timeoutMs });

        try
        {
            await locator.ClickAsync(new LocatorClickOptions
            {
                Timeout = 10_000,
                Force = true
            });

            var response = await waitTask.WaitAsync(ct);
            var body = await ReadBodyAsync(response);
            logger.LogInformation("Captured next page ({Length} chars)", body.Length);
            return body;
        }
        catch (Exception ex) when (IsTimeoutException(ex) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Failed to capture next page: {Message}", ex.Message);
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(IResponse response)
    {
        var body = await response.TextAsync();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("API response body was empty");
        return body;
    }

    private static (int Total, int Seen) GetPageSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (0, 0);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var total = root.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
            ? totalEl.GetInt32()
            : 0;

        var seen = root.TryGetProperty("ads", out var adsEl) && adsEl.ValueKind == JsonValueKind.Array
            ? adsEl.GetArrayLength()
            : 0;

        return (total, seen);
    }

    private async Task<IResponse> TriggerAndCaptureAsync(
        IPage page,
        ScraperOptions opt,
        double timeoutMs,
        CancellationToken ct)
    {
        Func<IResponse, bool> predicate = response =>
            response.Url.Contains(opt.ApiUrlPattern, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(opt.ClickSelector))
        {
            return await page.WaitForResponseAsync(predicate, new PageWaitForResponseOptions
            {
                Timeout = (float)timeoutMs
            }).WaitAsync(ct);
        }

        var clickTimeout = TimeSpan.FromSeconds(
            opt.ClickTimeoutSeconds <= 0 ? 15 : opt.ClickTimeoutSeconds).TotalMilliseconds;

        await page.WaitForTimeoutAsync(2000);
        await DismissOverlaysAsync(page, opt, (float)clickTimeout, ct);

        var waitTask = page.WaitForResponseAsync(predicate, new PageWaitForResponseOptions
        {
            Timeout = (float)timeoutMs
        });

        await ClickSelectorWithRetryAsync(page, opt.ClickSelector, (float)clickTimeout, 5, ct);

        return await waitTask.WaitAsync(ct);
    }

    private async Task DismissOverlaysAsync(
        IPage page,
        ScraperOptions opt,
        float timeoutMs,
        CancellationToken ct)
    {
        if (opt.DismissSelectors.Length == 0)
            return;

        foreach (var selector in opt.DismissSelectors)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 3000
                });

                await locator.ClickAsync(new LocatorClickOptions
                {
                    Timeout = timeoutMs,
                    Force = true
                });
                await page.WaitForTimeoutAsync(500);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug("Overlay dismiss skipped for {Selector}: {Message}", selector, ex.Message);
            }
        }
    }

    private async Task ClickSelectorWithRetryAsync(
        IPage page,
        string selector,
        float clickTimeoutMs,
        int attempts,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = clickTimeoutMs
                });
                await locator.ClickAsync(new LocatorClickOptions
                {
                    Timeout = clickTimeoutMs,
                    Force = attempt > 1
                });
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            logger.LogWarning("Could not click {Selector}: {Message}", selector, lastError.Message);
    }

    private static bool IsTimeoutException(Exception ex)
    {
        if (ex is TimeoutException or TaskCanceledException)
            return true;
        return ex is PlaywrightException pe && pe.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static BrowserNewContextOptions BuildContextOptions(ScraperOptions opt, UserAgentPool ua)
    {
        var extraHeaders = new Dictionary<string, string>
        {
            ["Accept-Language"] = "ru-RU,ru;q=0.9,en;q=0.8",
            ["Referer"] = string.IsNullOrWhiteSpace(opt.TargetUrl)
                ? "https://re.kufar.by/"
                : new Uri(opt.TargetUrl).GetLeftPart(UriPartial.Path)
        };

        var ctxOpts = new BrowserNewContextOptions
        {
            UserAgent = ua.Next(),
            Locale = "ru-RU",
            ExtraHTTPHeaders = extraHeaders
        };

        if (!string.IsNullOrWhiteSpace(opt.Proxy))
        {
            if (Uri.TryCreate(opt.Proxy, UriKind.Absolute, out var proxyUri))
            {
                var userPass = proxyUri.UserInfo.Split(':', 2);
                ctxOpts.Proxy = new Proxy
                {
                    Server = $"{proxyUri.Scheme}://{proxyUri.Host}:{proxyUri.Port}",
                    Username = userPass.Length > 0 ? Uri.UnescapeDataString(userPass[0]) : null,
                    Password = userPass.Length > 1 ? Uri.UnescapeDataString(userPass[1]) : null
                };
            }
            else
            {
                ctxOpts.Proxy = new Proxy { Server = opt.Proxy };
            }
        }

        return ctxOpts;
    }
}