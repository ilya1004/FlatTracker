using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace FlatTracker.UI.Services;

/// <summary>
/// Персистентное состояние монитора: последний обработанный id сообщения по каждому чату.
/// Без этого при каждом старте LLM заново прогонял бы всю свежую историю.
/// </summary>
public sealed class MonitorStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _lastMessageIds = new();

    public MonitorStateStore(string path)
    {
        _path = path;
        Load();
    }

    public long GetLastMessageId(string peerKey) =>
        _lastMessageIds.TryGetValue(peerKey, out var id) ? id : 0;

    public async Task SetLastMessageIdAsync(string peerKey, long messageId, CancellationToken ct = default)
    {
        if (messageId <= 0)
            return;

        _lastMessageIds[peerKey] = messageId;
        await SaveAsync(ct);
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? [];
            foreach (var pair in data)
                _lastMessageIds[pair.Key] = pair.Value;
        }
        catch (Exception)
        {
            // Повреждённый файл состояния не должен мешать старту.
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_lastMessageIds.ToDictionary(k => k.Key, v => v.Value), JsonOptions);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Состояние — вспомогательное, ошибка записи не критична.
        }
        finally
        {
            _gate.Release();
        }
    }
}
