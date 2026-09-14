using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlatTracker.Core.Models;

public sealed class KufarSearchResponse
{
    [JsonPropertyName("ads")]
    public List<AdItem> Ads { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("pagination")]
    public PaginationInfo? Pagination { get; set; }
}

public sealed class PaginationInfo
{
    [JsonPropertyName("pages")]
    public List<PageInfo>? Pages { get; set; }
}

public sealed class PageInfo
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("num")]
    public int Num { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class AdItem
{
    [JsonPropertyName("ad_id")]
    public long AdId { get; set; }

    [JsonPropertyName("ad_link")]
    public string? AdLink { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("list_time")]
    public string? ListTime { get; set; }

    [JsonPropertyName("category")]
    public JsonElement? Category { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("price_byn")]
    public string? PriceByn { get; set; }

    [JsonPropertyName("price_usd")]
    public string? PriceUsd { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("company_ad")]
    public bool? CompanyAd { get; set; }

    [JsonPropertyName("ad_parameters")]
    public List<AdParameter>? AdParameters { get; set; }

    [JsonPropertyName("account_parameters")]
    public List<AdParameter>? AccountParameters { get; set; }

    [JsonPropertyName("images")]
    public JsonElement? Images { get; set; }
}

public sealed class AdParameter
{
    [JsonPropertyName("pl")]
    public string? Pl { get; set; }

    [JsonPropertyName("vl")]
    public JsonElement? Vl { get; set; }

    [JsonPropertyName("p")]
    public string? P { get; set; }

    [JsonPropertyName("v")]
    public JsonElement? V { get; set; }

    [JsonPropertyName("pu")]
    public string? Pu { get; set; }
}