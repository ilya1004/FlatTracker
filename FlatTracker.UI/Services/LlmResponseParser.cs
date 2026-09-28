using FlatTracker.Core.Configuration;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FlatTracker.UI.Services;

public sealed record ParsedAd(
    bool IsRelevant,
    int Price,
    string Currency,
    int PriceByn,
    int Rooms,
    string District,
    string Metro,
    string Address,
    string Description,
    string Reason)
{
    /// <summary>Форматирование фиксировано ru-RU, чтобы не зависеть от локали машины.</summary>
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    public string PriceText => Price <= 0
        ? "не указана"
        : Currency == CurrencyUsd
            ? $"{Price.ToString("N0", Culture)} USD ({PriceByn.ToString("N0", Culture)} BYN)"
            : $"{Price.ToString("N0", Culture)} BYN";

    public const string CurrencyUsd = "USD";
    public const string CurrencyByn = "BYN";
}

/// <summary>
/// Разбор сырого ответа LLM в доменный объект: валюта, приведение к BYN,
/// привязка района и метро к справочникам из настроек.
/// </summary>
internal static class LlmResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ParsedAd? TryParse(string? raw, LlmOptions options)
    {
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        LlmDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LlmDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null)
            return null;

        var price = dto.Price > 0 ? dto.Price : 0;
        var currency = NormalizeCurrency(dto.Currency, dto.PriceText);
        var priceByn = ConvertToByn(price, currency, options.UsdToBynRate);

        return new ParsedAd(
            dto.IsRelevant,
            price,
            currency,
            priceByn,
            dto.Rooms,
            MatchList(dto.District, options.Districts),
            MatchList(dto.Metro, options.MetroStations),
            dto.Address?.Trim() ?? string.Empty,
            dto.Description?.Trim() ?? string.Empty,
            dto.Reason?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Приводит ответ LLM к каноническому виду из справочника: без привязки к списку
    /// возвращаемое значение может отличаться от настроенного ("Серебрянка" / "серебрянка"),
    /// и фильтр по нему не сработал бы.
    /// </summary>
    internal static string MatchList(string? value, string[] dictionary)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (dictionary.Length == 0)
            return trimmed;

        foreach (var item in dictionary)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            if (string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        foreach (var item in dictionary)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            if (trimmed.Contains(item, StringComparison.OrdinalIgnoreCase) ||
                item.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return trimmed;
    }

    internal static string NormalizeCurrency(string? currency, string? priceText)
    {
        var value = currency?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            if (IsUsd(value))
                return ParsedAd.CurrencyUsd;
            if (IsByn(value))
                return ParsedAd.CurrencyByn;
        }

        // LLM иногда не заполняет currency, но пишет цену текстом.
        return DetectCurrencyFromText(priceText);
    }

    private static readonly string[] UsdMarkers =
    [
        "usd", "у.е", "у.е.", "уе", "условн", "$", "доллар", "долл", "долл."
    ];

    private static readonly string[] BynMarkers =
    [
        "byn", "брн", "б.р", "бр", "бел.руб", "бел. руб", "белорусск", "руб", "руб.", "р."
    ];

    internal static string DetectCurrencyFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lower = text.ToLowerInvariant();

        foreach (var marker in UsdMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return ParsedAd.CurrencyUsd;
        }

        foreach (var marker in BynMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return ParsedAd.CurrencyByn;
        }

        return string.Empty;
    }

    internal static int ConvertToByn(int price, string currency, double rate)
    {
        if (price <= 0)
            return 0;

        return currency == ParsedAd.CurrencyUsd && rate > 0
            ? (int)Math.Round(price * rate)
            : price;
    }

    private static bool IsUsd(string value)
    {
        var lower = value.ToLowerInvariant();
        return UsdMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static bool IsByn(string value)
    {
        var lower = value.ToLowerInvariant();
        return BynMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    internal static string ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var fenced = Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)```");
        if (fenced.Success)
            return fenced.Groups[1].Value.Trim();

        var brace = Regex.Match(raw, @"\{[\s\S]*\}", RegexOptions.Singleline);
        if (brace.Success)
            return brace.Value;

        // Модель могла не закрыть объект — закрываем сами, чтобы не терять разобранное.
        var open = raw.IndexOf('{');
        return open >= 0 ? raw[open..] + "}" : raw.Trim();
    }

    private sealed class LlmDto
    {
        [JsonPropertyName("is_relevant")] public bool IsRelevant { get; set; }

        [JsonPropertyName("price")] public int Price { get; set; }

        [JsonPropertyName("currency")] public string? Currency { get; set; }

        [JsonPropertyName("price_text")] public string? PriceText { get; set; }

        [JsonPropertyName("rooms")] public int Rooms { get; set; }

        [JsonPropertyName("district")] public string? District { get; set; }

        [JsonPropertyName("metro")] public string? Metro { get; set; }

        [JsonPropertyName("address")] public string? Address { get; set; }

        [JsonPropertyName("description")] public string? Description { get; set; }

        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
