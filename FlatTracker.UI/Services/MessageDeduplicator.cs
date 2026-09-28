using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FlatTracker.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlatTracker.UI.Services;

public class MessageDeduplicator
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;
    private int _cleanupThreshold;

    public MessageDeduplicator(IOptions<FilterOptions> options)
    {
        var opts = options.Value;
        _maxSize = Math.Max(100, opts.DedupCapacity);
        _ttl = TimeSpan.FromHours(24);
        _cleanupThreshold = _maxSize;
    }

    /// <summary>
    /// Возвращает true, если сообщение новое. Иначе — дубликат.
    /// </summary>
    public bool TryMarkAsSeen(string message, out string hash)
    {
        hash = ComputeHash(Normalize(message));

        if (_seen.TryGetValue(hash, out var seenAt) && DateTime.UtcNow - seenAt < _ttl)
            return false;

        _seen[hash] = DateTime.UtcNow;

        if (_seen.Count >= _cleanupThreshold)
            Cleanup();

        return true;
    }

    public void Seed(string message)
    {
        _seen.TryAdd(ComputeHash(Normalize(message)), DateTime.UtcNow);
    }

    private static string Normalize(string s)
    {
        var parts = s.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '\u00A0'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string ComputeHash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - _ttl;

        foreach (var key in _seen.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
        {
            _seen.TryRemove(key, out _);
        }

        // Если TTL ничего не освободил, срезаем половину по порядку добавления.
        if (_seen.Count >= _maxSize)
        {
            var toRemove = _seen.Count - _maxSize / 2;
            foreach (var key in _seen.OrderBy(kvp => kvp.Value).Take(toRemove).Select(kvp => kvp.Key).ToList())
            {
                _seen.TryRemove(key, out _);
            }
        }

        _cleanupThreshold = Math.Max(_maxSize / 2, _seen.Count + _maxSize / 2);
    }
}
