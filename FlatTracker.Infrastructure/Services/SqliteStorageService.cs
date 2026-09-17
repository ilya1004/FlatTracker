using FlatTracker.Core.Abstractions;
using FlatTracker.Core.Models;
using FlatTracker.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlatTracker.Infrastructure.Services;

public sealed class SqliteStorageService(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<SqliteStorageService> logger) : IStorageService
{
    public event EventHandler? AdsSaved;

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
    }

    public async Task<SaveAdsResult> SaveAdsAsync(IReadOnlyList<AdRecord> ads, CancellationToken ct)
    {
        var newAds = new List<AdRecord>();
        var updatedAds = new List<AdUpdate>();

        if (ads.Count == 0)
            return new SaveAdsResult { NewAds = newAds, UpdatedAds = updatedAds };

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        foreach (var ad in ads)
        {
            var exists = await db.Ads.AnyAsync(a => a.AdId == ad.AdId, ct);

            if (exists)
            {
                var existing = await db.Ads.FirstAsync(a => a.AdId == ad.AdId, ct);

                var oldCurrency = existing.Currency;
                var oldPriceByn = existing.PriceByn;
                var oldPriceUsd = existing.PriceUsd;

                var changed = existing.Title != ad.Title
                    || oldPriceByn != ad.PriceByn
                    || oldPriceUsd != ad.PriceUsd
                    || oldCurrency != ad.Currency
                    || existing.Rooms != ad.Rooms
                    || existing.TotalArea != ad.TotalArea
                    || existing.Floor != ad.Floor
                    || existing.Region != ad.Region
                    || existing.District != ad.District
                    || existing.Metro != ad.Metro
                    || existing.Address != ad.Address
                    || existing.IsCompany != ad.IsCompany;

                existing.Title = ad.Title;
                existing.PriceByn = ad.PriceByn;
                existing.PriceUsd = ad.PriceUsd;
                existing.Currency = ad.Currency;
                existing.Rooms = ad.Rooms;
                existing.TotalArea = ad.TotalArea;
                existing.Floor = ad.Floor;
                existing.Region = ad.Region;
                existing.District = ad.District;
                existing.Metro = ad.Metro;
                existing.Address = ad.Address;
                existing.IsCompany = ad.IsCompany;
                existing.ImageCount = ad.ImageCount;
                existing.LastUpdateTime = ad.LastUpdateTime;

                if (changed)
                {
                    updatedAds.Add(new AdUpdate
                    {
                        Ad = existing,
                        OldCurrency = oldCurrency,
                        OldPriceByn = oldPriceByn,
                        OldPriceUsd = oldPriceUsd,
                    });
                }
            }
            else
            {
                var record = new AdRecord
                {
                    AdId = ad.AdId,
                    AdLink = ad.AdLink,
                    Title = ad.Title,
                    PriceByn = ad.PriceByn,
                    PriceUsd = ad.PriceUsd,
                    Currency = ad.Currency,
                    Rooms = ad.Rooms,
                    TotalArea = ad.TotalArea,
                    Floor = ad.Floor,
                    Region = ad.Region,
                    District = ad.District,
                    Metro = ad.Metro,
                    Address = ad.Address,
                    IsCompany = ad.IsCompany,
                    ImageCount = ad.ImageCount,
                    LastUpdateTime = ad.LastUpdateTime,
                    FirstSeen = DateTime.Now
                };
                db.Ads.Add(record);
                newAds.Add(record);
            }
        }

        var changes = await db.SaveChangesAsync(ct);

        logger.LogInformation("Upserted {Count} ads via EF Core ({New} new, {Updated} updated, {Changes} DbChanges)",
            ads.Count, newAds.Count, updatedAds.Count, changes);
        AdsSaved?.Invoke(this, EventArgs.Empty);

        return new SaveAdsResult { NewAds = newAds, UpdatedAds = updatedAds };
    }

    public async Task<IReadOnlyList<AdRecord>> GetAdsAsync(string? search, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var query = db.Ads.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.Title.Contains(search) || a.Address!.Contains(search));

        return await query
            .OrderByDescending(a => a.LastUpdateTime)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);
    }

    public async Task<int> GetCountAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Ads.CountAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetDistrictsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var values = await db.Ads
            .Select(a => a.District ?? a.Region)
            .Where(d => d != null)
            .ToListAsync(ct);

        return values
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}