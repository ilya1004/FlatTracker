using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FlatTracker.UI;

public sealed class UiLogSink
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly object _lock = new();
    private Action<string>? _subscribers;

    public event Action<string>? MessageReceived
    {
        add { lock (_lock) { _subscribers += value; } }
        remove { lock (_lock) { _subscribers -= value; } }
    }

    public IEnumerable<string> GetBuffer() => _messages.ToArray();

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

public sealed class UiLoggerProvider : ILoggerProvider
{
    private readonly UiLogSink _sink;

    public UiLoggerProvider(UiLogSink sink)
    {
        _sink = sink;
    }

    public ILogger CreateLogger(string categoryName) => new UiLogger(_sink, categoryName);

    public void Dispose()
    {
    }

    private sealed class UiLogger : ILogger
    {
        private static readonly string[] LogLevels = { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL" };

        private readonly UiLogSink _sink;
        private readonly string _categoryName;

        public UiLogger(UiLogSink sink, string categoryName)
        {
            _sink = sink;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel < LogLevel.Information)
            {
                return false;
            }

            if (logLevel <= LogLevel.Information
                && _categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = LogLevels[(int)logLevel];
            var line = $"{DateTime.Now:HH:mm:ss} [{level}] {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _sink.Write(line);
        }
    }
}