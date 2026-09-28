using FlatTracker.Core.Models;

namespace FlatTracker.Core.Abstractions;

public interface INotificationService
{
    Task NotifyScrapeCompletedAsync(ScrapeReport report, CancellationToken ct = default);
    Task SendTextAsync(string text, CancellationToken ct = default);
    Task TestConnectionAsync();
}
