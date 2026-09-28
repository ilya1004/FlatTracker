using FlatTracker.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlatTracker.UI.Services;

public class MessagePreFilter
{
    // Ключевые слова, которые с высокой вероятностью указывают на объявление
    private static readonly string[] AdKeywords =
    [
        "сда", "сдам", "сдаётся", "сдается", "сдаю", "аренд", "квартир", "комнат",
        "студия", "однушк", "двушк", "трёшк", "трешк",
        "тыс", "к/мес", "в месяц", "/мес",
        "у.е", "у.е.", "условн", "$", "usd", "доллар", "брн", "руб", "р.",
        "залог", "депозит", "собственник", "от хозяина", "без посредников",
        "м²", "м2", "кв.м", "кв.м.", "этаж", "кухня", "балкон", "лоджия",
        "метро", "район", "минут пешком", "остановк"
    ];

    private readonly FilterOptions _options;

    public MessagePreFilter(IOptions<FilterOptions> options)
    {
        _options = options.Value;
    }

    public bool IsLikelyAd(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (message.Length < _options.MinLength || message.Length > _options.MaxLength)
            return false;

        var lower = message.ToLowerInvariant();

        var matchCount = 0;
        foreach (var keyword in AdKeywords)
        {
            if (lower.Contains(keyword))
            {
                matchCount++;
                if (matchCount >= _options.MinKeywords)
                    return true;
            }
        }

        return false;
    }
}
