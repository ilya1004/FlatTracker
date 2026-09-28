namespace FlatTracker.Core.Configuration;

public sealed class FilterOptions
{
    public const string SectionName = "Filter";

    public int MinLength { get; set; } = 40;

    public int MaxLength { get; set; } = 2000;

    public int MinKeywords { get; set; } = 2;

    public int MaxQueueSize { get; set; } = 200;

    public int DedupCapacity { get; set; } = 20000;
}
