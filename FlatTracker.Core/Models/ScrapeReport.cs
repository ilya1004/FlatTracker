namespace FlatTracker.Core.Models;

public sealed class ScrapeReport
{
    public required DateTime GeneratedAt { get; init; }

    public required IReadOnlyList<AdRecord> NewAds { get; init; }

    public required IReadOnlyList<AdUpdate> UpdatedAds { get; init; }
}