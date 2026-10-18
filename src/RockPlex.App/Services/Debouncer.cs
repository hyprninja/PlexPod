namespace RockPlex.App.Services;

/// <summary>
/// Runs the latest scheduled action once things go quiet for <c>delay</c>; earlier calls are dropped.
/// <paramref name="onError"/> receives anything the action throws: <see cref="Schedule"/> is
/// <c>async void</c>, so an escaped exception would otherwise take the app down.
/// </summary>
public sealed class Debouncer(TimeSpan delay, Action<Exception> onError) : IDisposable
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
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
