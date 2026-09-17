namespace FlatTracker.Core.Abstractions;

public interface INotificationPreferencesService
{
    IReadOnlyList<string> SelectedDistricts { get; }

    Task LoadAsync(CancellationToken ct);

    Task SaveAsync(IEnumerable<string> districts, CancellationToken ct);
}