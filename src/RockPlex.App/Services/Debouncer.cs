namespace RockPlex.App.Services;

/// <summary>Runs the latest scheduled action once things go quiet for <c>delay</c>; earlier calls are dropped.</summary>
public sealed class Debouncer(TimeSpan delay) : IDisposable
{
    private CancellationTokenSource? _pending;

    /// <summary>Call on the UI thread; <paramref name="action"/> resumes on the same context.</summary>
    public async void Schedule(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = _pending = new CancellationTokenSource();
        try
        {
            await Task.Delay(delay, cts.Token);
            await action(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer call.
        }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
