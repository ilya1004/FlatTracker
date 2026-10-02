using System.Diagnostics;
using System.Threading.Channels;
using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Models;
using FlatTracker.UI.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlatTracker.UI.Workers;

public sealed class TelegramMonitorPipelineWorker(
    Channel<QueuedMessage> queue,
    LlmParsingService llm,
    INotificationService notifier,
    MessagePreFilter preFilter,
    MessageDeduplicator dedup,
    ILogger<TelegramMonitorPipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Пайплайн обработки сообщений запущен");

        if (!await llm.EnsureAvailableAsync(stoppingToken))
        {
            logger.LogError("Пайплайн остановлен: LLM недоступна");
            return;
        }

        var processed = 0;

        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (await ProcessMessageAsync(message, stoppingToken))
                {
                    processed++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Необработанная ошибка при разборе сообщения");
            }
        }

        logger.LogInformation("Пайплайн обработал {Count} релевантных объявлений и остановлен", processed);
    }

    private async Task<bool> ProcessMessageAsync(QueuedMessage message, CancellationToken ct)
    {
        var preview = Preview(message.Text);

        logger.LogInformation("Из очереди: {Source}, {Chars} симв. — {Preview}",
            SourceLabel(message.Source), message.Text.Length, preview);

        if (!preFilter.IsLikelyAd(message.Text))
        {
            logger.LogDebug("Префильтр отбросил сообщение: {Preview}", preview);
            return false;
        }

        if (!dedup.TryMarkAsSeen(message.Text, out var hash))
        {
            logger.LogDebug("Дубликат, повторно в LLM не отправляем: {Preview}", preview);
            return false;
        }

        var sw = Stopwatch.StartNew();
        var parsed = await llm.ParseAdAsync(message.Text, ct);
        sw.Stop();

        logger.LogInformation("LLM обработала сообщение за {Ms} мс, is_relevant={Relevant}, {Reason}",
            sw.ElapsedMilliseconds, parsed?.IsRelevant, parsed?.Reason ?? "нет результата");

        if (parsed is null)
        {
            logger.LogWarning("LLM не дала результата за {Ms} мс (хэш {Hash})", sw.ElapsedMilliseconds, hash[..8]);
            return false;
        }

        if (!parsed.IsRelevant)
        {
            logger.LogInformation("Отклонено за {Ms} мс: {Reason}", sw.ElapsedMilliseconds, parsed.Reason);
            return false;
        }

        logger.LogInformation(
            "✅ Подходит за {Ms} мс: {Source}, {Price}, {Rooms} комн., {District}, метро {Metro}",
            sw.ElapsedMilliseconds, SourceLabel(message.Source), parsed.PriceText, parsed.Rooms,
            parsed.District.Length > 0 ? parsed.District : "—",
            parsed.Metro.Length > 0 ? parsed.Metro : "—");

        await notifier.SendTextAsync(FormatNotification(parsed, message), ct);
        return true;
    }

    public static string SourceLabel(AdSource source) => source switch
    {
        AdSource.Kufar => "🟡 Куфар",
        AdSource.TelegramGroup => "🔵 Группа",
        _ => "⚪ Источник неизвестен"
    };

    /// <summary>Короткий предпросмотр текста, чтобы в логе было видно, о чём речь.</summary>
    private static string Preview(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static string FormatNotification(ParsedAd ad, QueuedMessage message)
    {
        var original = message.Text;
        var location = new List<string>();

        if (ad.District.Length > 0)
            location.Add($"📍 {ad.District}");

        if (ad.Metro.Length > 0)
            location.Add($"🚇 {ad.Metro}");

        if (ad.Address.Length > 0)
            location.Add($"🏢 {ad.Address}");

        var locationText = location.Count > 0 ? string.Join('\n', location) : "📍 район не указан";

        return $"""
                🔥 НОВОЕ ОБЪЯВЛЕНИЕ
                Источник: {SourceLabel(message.Source)}
                💰 {ad.PriceText}
                🏠 {ad.Rooms} комн.
                {locationText}

                💬 {ad.Description}

                ─────────────
                Оригинал:
                {original}
                """;
    }
}
