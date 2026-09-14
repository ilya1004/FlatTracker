namespace FlatTracker.Core.Abstractions;

public sealed class ScrapeTrigger
{
    private readonly object _lock = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken ct)
    {
        lock (_lock)
            return _tcs.Task.WaitAsync(ct);
    }

    public void Signal()
    {
        lock (_lock)
        {
            _tcs.TrySetResult();
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
