using System.Text.Json;
using FlatTracker.Core.Abstractions;

namespace FlatTracker.Infrastructure.Services;

public sealed class JsonNotificationPreferencesService : INotificationPreferencesService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string> _selectedDistricts = new();

    public JsonNotificationPreferencesService(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<string> SelectedDistricts => _selectedDistricts;

    public async Task LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                _selectedDistricts = new List<string>();
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var dto = JsonSerializer.Deserialize<PreferencesDto>(json);
            _selectedDistricts = dto?.Districts
                ?.Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<string> districts, CancellationToken ct)
    {
        var normalized = districts
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _gate.WaitAsync(ct);
        try
        {
            _selectedDistricts = normalized;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var dto = new PreferencesDto { Districts = normalized };
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(dto), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class PreferencesDto
    {
        public List<string> Districts { get; set; } = new();
    }
}