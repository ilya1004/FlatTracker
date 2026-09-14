namespace FlatTracker.Core.Models;

public sealed class SaveAdsResult
{
    public required IReadOnlyList<AdRecord> NewAds { get; init; }

    public required IReadOnlyList<AdUpdate> UpdatedAds { get; init; }
}

public sealed class AdUpdate
{
    public required AdRecord Ad { get; init; }

    public string? OldCurrency { get; init; }

    public decimal? OldPriceByn { get; init; }

    public decimal? OldPriceUsd { get; init; }
}