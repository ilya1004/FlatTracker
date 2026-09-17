namespace FlatTracker.Core.Abstractions;

public sealed class ScrapeRunTracker
{
    private readonly object _lock = new();
    private bool _isRunning;

    public bool IsRunning
    {
        get { lock (_lock) return _isRunning; }
    }

    public bool TryBeginRun()
    {
        lock (_lock)
        {
            if (_isRunning) return false;
            _isRunning = true;
            return true;
        }
    }

    public void EndRun()
    {
        lock (_lock)
        {
            _isRunning = false;
        }
    }
}