using System.Globalization;
using System.Net;
using System.Text;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Models;
using FlatTracker.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace FlatTracker.Infrastructure.Services;

public sealed class TelegramNotificationService : INotificationService
{
    private readonly TelegramBotClient _botClient;
    private readonly long _chatId;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(IOptions<TelegramOptions> options, ILogger<TelegramNotificationService> logger)
    {
        _logger = logger;
        _botClient = new TelegramBotClient(options.Value.BotToken);
        _chatId = options.Value.ChatId;
    }

    public async Task NotifyScrapeCompletedAsync(ScrapeReport report, CancellationToken ct = default)
    {
        if (report.NewAds.Count == 0 && report.UpdatedAds.Count == 0)
            return;

        try
        {
            var message = BuildMessage(report);

            await _botClient.SendMessage(
                chatId: _chatId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            _logger.LogInformation(
                "Отчёт отправлен в Telegram: {New} новых, {Updated} обновлённых",
                report.NewAds.Count,
                report.UpdatedAds.Count);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogError(ex, "Ошибка отправки сообщения в Telegram: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неожиданная ошибка при отправке уведомления в Telegram");
        }
    }

    public async Task TestConnectionAsync()
    {
        try
        {
            var me = await _botClient.GetMe();
            _logger.LogInformation("Подключено к Telegram боту: {Username}", me.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось подключиться к Telegram API");
        }
    }

    private static string BuildMessage(ScrapeReport report)
    {
        const int maxPerSection = 20;

        var builder = new StringBuilder();
        builder.AppendLine("🏠 <b>Новые и обновлённые объявления</b>");
        builder.AppendLine($"Время: {report.GeneratedAt:dd.MM.yyyy HH:mm:ss}");
        builder.AppendLine();

        if (report.NewAds.Count > 0)
        {
            builder.AppendLine($"🆕 <b>Новые: {report.NewAds.Count}</b>");
            AppendAdList(builder, report.NewAds, maxPerSection);
            builder.AppendLine();
        }

        if (report.UpdatedAds.Count > 0)
        {
            builder.AppendLine($"🔄 <b>Обновлены: {report.UpdatedAds.Count}</b>");
            AppendUpdateList(builder, report.UpdatedAds, maxPerSection);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendAdList(
        StringBuilder builder,
        IReadOnlyList<AdRecord> ads,
        int max)
    {
        var shown = Math.Min(ads.Count, max);

        for (var i = 0; i < shown; i++)
        {
            var ad = ads[i];
            builder.AppendLine($"• <b>{Escape(ad.Title)}</b>");
            builder.AppendLine($"  {FormatPrice(ad, null)} · {AdLine(ad)}");
            builder.AppendLine($"  <a href=\"{Escape(ad.AdLink)}\">Открыть</a>");
        }

        if (ads.Count > max)
            builder.AppendLine($"...и ещё {ads.Count - max}");
    }

    private static void AppendUpdateList(
        StringBuilder builder,
        IReadOnlyList<AdUpdate> updates,
        int max)
    {
        var shown = Math.Min(updates.Count, max);

        for (var i = 0; i < shown; i++)
        {
            var upd = updates[i];
            builder.AppendLine($"• <b>{Escape(upd.Ad.Title)}</b>");
            builder.AppendLine($"  {FormatPrice(upd.Ad, upd)} · {AdLine(upd.Ad)}");
            builder.AppendLine($"  <a href=\"{Escape(upd.Ad.AdLink)}\">Открыть</a>");
        }

        if (updates.Count > max)
            builder.AppendLine($"...и ещё {updates.Count - max}");
    }

    private static string AdLine(AdRecord ad)
    {
        var parts = new[]
        {
            ad.Rooms is null ? null : $"{ad.Rooms} комн.",
            ad.TotalArea is null ? null : $"{ad.TotalArea:0.#} м²",
            ad.Metro,
            ad.District ?? ad.Region,
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string FormatPrice(AdRecord ad, AdUpdate? update)
    {
        if (string.Equals(ad.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            var current = ad.PriceUsd?.ToString("F2", CultureInfo.InvariantCulture);
            var old = update?.OldCurrency == "USD"
                ? update.OldPriceUsd?.ToString("F2", CultureInfo.InvariantCulture)
                : null;
            return FormatPriceText("$", current, old);
        }

        var currentByn = ad.PriceByn?.ToString("F2", CultureInfo.InvariantCulture);
        var oldByn = update?.OldCurrency is not "USD"
            ? update?.OldPriceByn?.ToString("F2", CultureInfo.InvariantCulture)
            : null;
        return FormatPriceText("BYN", currentByn, oldByn);
    }

    private static string FormatPriceText(string symbol, string? current, string? old)
    {
        if (current is null)
            return "—";

        var prefix = symbol == "$" ? "$ " : "";

        if (old is not null && old != current)
            return $"{prefix}{current} (было {prefix}{old})";

        return $"{prefix}{current}";
    }

    private static string Escape(string value)
        => WebUtility.HtmlEncode(value);
}