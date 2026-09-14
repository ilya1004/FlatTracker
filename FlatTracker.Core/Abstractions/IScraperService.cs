namespace FlatTracker.Core.Abstractions;

public interface IScraperService
{
    Task<IReadOnlyList<string>> ScrapeAsync(CancellationToken ct);
}
