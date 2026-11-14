namespace PlexPod.App.Services;

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

        // Cancel the previous run but don't dispose its source: it may still be inside its action,
        // using the token. Each run disposes its own source when it finishes.
        _pending?.Cancel();
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
        finally
        {
            if (ReferenceEquals(_pending, cts))
            {
                _pending = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Cancels the pending run; it disposes its own source as it finishes.</summary>
    public void Dispose()
    {
        _pending?.Cancel();
        _pending = null;
    }
}
