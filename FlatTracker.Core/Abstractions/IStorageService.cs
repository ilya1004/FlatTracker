using FlatTracker.Core.Models;

namespace FlatTracker.Core.Abstractions;

public interface IStorageService
{
    event EventHandler? AdsSaved;
    Task EnsureInitializedAsync(CancellationToken ct);
    Task<SaveAdsResult> SaveAdsAsync(IReadOnlyList<AdRecord> ads, CancellationToken ct);
    Task<IReadOnlyList<AdRecord>> GetAdsAsync(string? search, CancellationToken ct);
    Task<int> GetCountAsync(CancellationToken ct);
}
