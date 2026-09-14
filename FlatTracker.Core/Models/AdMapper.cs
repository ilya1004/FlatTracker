using System.Globalization;
using System.Text.Json;

namespace FlatTracker.Core.Models;

public static class AdMapper
{
    public static AdRecord ToRecord(AdItem item, string? address = null, List<AdParameter>? accountParams = null)
    {
        var paramLookup = (item.AdParameters ?? [])
            .GroupBy(p => p.Pu ?? p.P ?? "")
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var accountLookup = (accountParams ?? item.AccountParameters ?? [])
            .GroupBy(p => p.Pu ?? p.P ?? "")
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string? resolvedAddress = address
            ?? TryGetString(accountLookup, "ad")
            ?? TryGetString(accountLookup, "city");

        return new AdRecord
        {
            AdId = item.AdId,
            AdLink = item.AdLink ?? "",
            Title = item.Subject ?? "",
            PriceByn = ParsePrice(item.PriceByn),
            PriceUsd = ParsePrice(item.PriceUsd),
            Currency = string.Equals(item.Currency, "BYR", StringComparison.OrdinalIgnoreCase) ? "BYN" : item.Currency,
            Rooms = TryGetInt(paramLookup, "rms"),
            TotalArea = TryGetDouble(paramLookup, "st"),
            Floor = TryGetInt(paramLookup, "fl"),
            Region = TryGetString(paramLookup, "rgn"),
            District = TryGetString(paramLookup, "ar"),
            Metro = TryGetString(paramLookup, "mee"),
            Address = resolvedAddress,
            IsCompany = item.CompanyAd ?? false,
            ImageCount = CountImages(item.Images),
            LastUpdateTime = ParseLastUpdateTime(item.ListTime),
            FirstSeen = DateTime.Now
        };
    }

    private static DateTime ParseLastUpdateTime(string? value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date.ToLocalTime();
        return DateTime.Now;
    }

    private static decimal? ParsePrice(string? price)
    {
        if (string.IsNullOrWhiteSpace(price)) return null;
        if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return value / 100m;
    }

    private static string? TryGetString(Dictionary<string, AdParameter> lookup, string key)
    {
        if (!lookup.TryGetValue(key, out var param)) return null;
        var val = GetValue(param);
        if (!val.HasValue) return null;
        return val.Value.ValueKind switch
        {
            JsonValueKind.String => val.Value.GetString(),
            JsonValueKind.Array when val.Value.GetArrayLength() > 0 => val.Value[0].ValueKind == JsonValueKind.String
                ? val.Value[0].GetString()
                : val.Value[0].ToString(),
            _ => val.Value.ToString()
        };
    }

    private static int? TryGetInt(Dictionary<string, AdParameter> lookup, string key)
    {
        if (!lookup.TryGetValue(key, out var param)) return null;
        var val = GetValue(param);
        if (!val.HasValue) return null;
        var el = UnwrapArray(val.Value);
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt32(),
            JsonValueKind.String => int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
            _ => null
        };
    }

    private static double? TryGetDouble(Dictionary<string, AdParameter> lookup, string key)
    {
        if (!lookup.TryGetValue(key, out var param)) return null;
        var val = GetValue(param);
        if (!val.HasValue) return null;
        var el = UnwrapArray(val.Value);
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null
        };
    }

    private static JsonElement UnwrapArray(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
            return el[0];
        return el;
    }

    private static JsonElement? GetValue(AdParameter param)
    {
        var vl = param.Vl;
        var v = param.V;

        if (vl.HasValue && !IsEmptyValue(vl.Value))
            return vl;
        return v;
    }

    private static bool IsEmptyValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString()),
            JsonValueKind.Array => el.GetArrayLength() == 0
                || el.EnumerateArray().All(e => string.IsNullOrWhiteSpace(e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())),
            _ => false
        };
    }

    private static int CountImages(JsonElement? images)
    {
        if (!images.HasValue) return 0;
        if (images.Value.ValueKind == JsonValueKind.Array)
            return images.Value.GetArrayLength();
        return 0;
    }
}