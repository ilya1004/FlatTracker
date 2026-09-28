using System.Text;
using FlatTracker.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace FlatTracker.UI.Services;

public sealed class LlmParsingService : IAsyncDisposable
{
    private readonly OllamaApiClient _ollama;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmParsingService> _logger;

    public LlmParsingService(IOptions<LlmOptions> options, ILogger<LlmParsingService> logger)
        : this(options, logger, null)
    {
    }

    internal LlmParsingService(
        IOptions<LlmOptions> options,
        ILogger<LlmParsingService> logger,
        OllamaApiClient? client)
    {
        _options = options.Value;
        _logger = logger;
        _ollama = client ?? new OllamaApiClient(new Uri(_options.Endpoint));
    }

    public async Task<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        try
        {
            var version = await _ollama.GetVersionAsync(ct);
            _logger.LogInformation("Ollama доступен (версия {Version}), модель {Model}", version, _options.Model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Ollama недоступен по адресу {Endpoint}. Проверьте, что 'ollama serve' запущен.",
                _options.Endpoint);
            return false;
        }

        try
        {
            var names = (await _ollama.ListLocalModelsAsync(ct)).Select(m => m.Name).ToList();

            if (names.Count == 0)
            {
                _logger.LogError("В Ollama нет ни одной модели. Установите: ollama pull {Model}", _options.Model);
                return false;
            }

            if (!names.Any(n => string.Equals(n, _options.Model, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError(
                    "Модель {Model} не найдена. Доступные: {Available}. Установите: ollama pull {Model}",
                    _options.Model, string.Join(", ", names), _options.Model);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось получить список моделей Ollama");
            return false;
        }

        return true;
    }

    public async Task<ParsedAd?> ParseAdAsync(string messageText, CancellationToken ct)
    {
        var maxRetries = Math.Max(0, _options.MaxRetries);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

                var request = BuildRequest(messageText);

                var fullResponse = await CollectStreamAsync(_ollama.GenerateAsync(request, timeout.Token), timeout.Token);

                if (string.IsNullOrWhiteSpace(fullResponse))
                {
                    _logger.LogWarning("Попытка {Attempt}: LLM вернула пустой ответ", attempt + 1);
                    await DelayBeforeRetryAsync(attempt, ct);
                    continue;
                }

                var parsed = LlmResponseParser.TryParse(fullResponse, _options);
                if (parsed is null)
                {
                    _logger.LogWarning("Попытка {Attempt}: не удалось разобрать JSON из: {Raw}",
                        attempt + 1, Truncate(fullResponse, 300));
                    await DelayBeforeRetryAsync(attempt, ct);
                    continue;
                }

                return parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Попытка {Attempt}: LLM не ответила за {Seconds}с",
                    attempt + 1, _options.TimeoutSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка LLM, попытка {Attempt}", attempt + 1);
            }

            await DelayBeforeRetryAsync(attempt, ct);
        }

        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal GenerateRequest BuildRequest(string text) => new()
    {
        Model = _options.Model,
        System = "Ты отвечаешь ТОЛЬКО валидным JSON без markdown и комментариев.",
        Prompt = BuildPrompt(text),
        Stream = false,
        Format = "json",
        Think = false,
        Options = new RequestOptions
        {
            Temperature = 0.1f,
            NumCtx = _options.NumCtx,
            NumPredict = _options.NumPredict,
            TopP = 0.9f
        }
    };

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        await Task.Delay(500 * (attempt + 1), ct);
    }

    private static async Task<string> CollectStreamAsync(
        IAsyncEnumerable<GenerateResponseStream?> stream,
        CancellationToken ct)
    {
        var sb = new StringBuilder(256);

        await foreach (var chunk in stream.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(chunk?.Response))
                sb.Append(chunk.Response);

            if (chunk?.Done == true)
                break;
        }

        return sb.ToString();
    }

    internal string BuildPrompt(string text)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Проанализируй объявление об аренде квартиры в Минске и верни ТОЛЬКО валидный JSON.");
        sb.AppendLine();
        sb.AppendLine("Схема ответа:");
        sb.AppendLine("{");
        sb.AppendLine("  \"is_relevant\": true или false,");
        sb.AppendLine("  \"price\": целое число, как указана в объявлении (0 если цены нет),");
        sb.AppendLine("  \"currency\": \"BYN\" или \"USD\" (что указано в объявлении),");
        sb.AppendLine("  \"price_text\": цена текстом, как она написана в объявлении,");
        sb.AppendLine("  \"rooms\": целое число, количество комнат (0 если не указано),");
        sb.AppendLine("  \"district\": район Минска или \"\",");
        sb.AppendLine("  \"metro\": станция метро или \"\",");
        sb.AppendLine("  \"address\": адрес или \"\",");
        sb.AppendLine("  \"description\": краткое описание квартиры,");
        sb.AppendLine("  \"reason\": краткое объяснение решения");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("РАЗБОР ЦЕНЫ:");
        sb.AppendLine("В объявлениях цена может быть указана в белорусских рублях или в долларах.");
        sb.AppendLine("Все эти варианты означают USD:");
        sb.AppendLine("  \"500 у.е.\", \"у.е.\", \"у.е\", \"у.е. в месяц\", \"500 условных единиц\",");
        sb.AppendLine("  \"500 у.е\", \"500$\", \"500 $\", \"500$\", \"300 usd\", \"300 USD\", \"300 долларов\", \"$300\"");
        sb.AppendLine("Все эти варианты означают BYN:");
        sb.AppendLine("  \"1200 BYN\", \"1200 бр\", \"1200 бел. руб.\", \"1200 руб.\", \"1200 р.\", \"1200р\"");
        sb.AppendLine("Правила разбора цены:");
        sb.AppendLine("- В price подставь только число, без знаков валют и пробелов-разделителей.");
        sb.AppendLine("- В currency поставь BYN или USD соответственно тому, как цена указана в тексте.");
        sb.AppendLine("- Если цена указана в USD, в price пиши доллары, а не рубли.");
        sb.AppendLine("- Если в объявлении несколько цен (например, \"1200 BYN / 400 USD\"),");
        sb.AppendLine("  в price и currency возьми ПЕРВУЮ, а вторую сохрани в price_text.");
        sb.AppendLine("- В price_text скопируй цену из текста объявления без изменений.");
        sb.AppendLine("- Если цены нет, поставь price = 0 и currency = \"\".");
        sb.AppendLine();
        sb.AppendLine("РАЗБОР РАЙОНА И МЕТРО:");
        sb.AppendLine("Район и метро НЕ влияют на is_relevant. Заполняй их только если они прямо названы в объявлении.");
        sb.AppendLine("- district: район Минска из текста объявления. Указывай только название района без слова \"район\".");
        sb.AppendLine("- metro: ближайшая станция метро, если она упомянута.");
        sb.AppendLine("  Указывай только название станции без слова \"метро\".");
        sb.AppendLine("- Если район или метро не упомянуты или их нельзя определить, поставь \"\".");
        sb.AppendLine("  Отсутствие района или метро НЕ является причиной поставить is_relevant = false.");
        AppendDictionary(sb, "Допустимые районы", _options.Districts, "Не подставляй район, которого нет в этом списке.");
        AppendDictionary(sb, "Допустимые станции метро", _options.MetroStations, "Не подставляй станцию, которой нет в этом списке.");
        sb.AppendLine();
        sb.AppendLine("КРИТЕРИИ (is_relevant = true только если ВСЕ выполнены):");
        sb.AppendLine($"- Цена: до {_options.MaxPriceByn} BYN в месяц ИЛИ до {_options.MaxPriceUsd} USD в месяц.");
        sb.AppendLine($"  Сравнивай с валютой из объявления. {_options.MaxPriceUsd} USD примерно равно {_options.MaxPriceByn} BYN.");
        sb.AppendLine("- 1 или 2 комнаты (студия считается 1 комнатой).");

        if (_options.Districts.Length > 0)
            sb.AppendLine("- Если район упомянут, он должен быть из списка допустимых районов. Если район не упомянут — критерий не проверяется.");

        if (_options.MetroStations.Length > 0)
            sb.AppendLine("- Если станция метро упомянута, она должна быть из списка допустимых станций. Если метро не упомянуто — критерий не проверяется.");

        sb.AppendLine("Если хотя бы один критерий не выполнен — is_relevant = false.");
        sb.AppendLine("В поле reason кратко объясни, какие критерии выполнены, а какие нет.");
        sb.AppendLine();
        sb.AppendLine("Объявление:");
        sb.AppendLine(text);

        return sb.ToString();
    }

    private static void AppendDictionary(StringBuilder sb, string title, string[] items, string note)
    {
        if (items.Length == 0)
            return;

        var clean = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        if (clean.Length == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"{title} (выбери ровно один из списка, напиши его точно так же):");
        sb.AppendLine(string.Join(", ", clean));
        sb.AppendLine(note);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
