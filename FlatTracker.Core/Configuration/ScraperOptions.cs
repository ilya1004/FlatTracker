namespace FlatTracker.Core.Configuration;

public sealed class ScraperOptions
{
    public const string SectionName = "Scraper";

    public required string TargetUrl { get; set; }
    public required string ApiRequest { get; set; }
    public required string ApiUrlPattern { get; set; }
    public required string ClickSelector { get; set; }
    public int ClickTimeoutSeconds { get; set; }
    public required string[] DismissSelectors { get; set; }
    public int MaxPages { get; set; }
    public required string PaginationNextSelector { get; set; }
    public bool Headless { get; set; }
    public int TimeoutSeconds { get; set; }
    public int MinIntervalMinutes { get; set; }
    public int MaxIntervalMinutes { get; set; }
    public string Proxy { get; set; }
    public string OutputDirectory { get; set; }
}
