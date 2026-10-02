using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace FlatTracker.UI;

/// <summary>
/// Приёмник логов для окна приложения
/// </summary>
public sealed class UiLogSink : ILogEventSink
{
    private static readonly string[] LogLevels = { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL" };

    private readonly ConcurrentQueue<string> _messages = new();
    private readonly object _lock = new();
    private Action<string>? _subscribers;

    public event Action<string>? MessageReceived
    {
        add { lock (_lock) { _subscribers += value; } }
        remove { lock (_lock) { _subscribers -= value; } }
    }

    public IEnumerable<string> GetBuffer() => _messages.ToArray();

    public void Emit(LogEvent logEvent)
    {
        var line = $"{logEvent.Timestamp.ToLocalTime():HH:mm:ss} [{LogLevels[(int)logEvent.Level]}] {logEvent.RenderMessage()}";

        if (logEvent.Exception is not null)
            line += Environment.NewLine + logEvent.Exception;

        Write(line);
    }

    public void Write(string message)
    {
        _messages.Enqueue(message);

        while (_messages.Count > 2000)
        {
            _messages.TryDequeue(out _);
        }

        Action<string>? subscribers;
        lock (_lock)
        {
            subscribers = _subscribers;
        }

        subscribers?.Invoke(message);
    }
}
