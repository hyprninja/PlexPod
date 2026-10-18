namespace PlexPod.Media;

/// <summary>
/// Lets only one long media operation run at a time (a sync or a Test export): both share the
/// pipeline, its conversion cache and the iPod, so running them together only causes contention.
/// </summary>
public sealed class OperationGate
{
    private readonly Lock _lock = new();
    private string? _holder;

    /// <summary>Raised whenever the gate is taken or released.</summary>
    public event EventHandler? Changed;

    /// <summary>What is running, or null when the gate is free.</summary>
    public string? Holder
    {
        get
        {
            lock (_lock)
            {
                return _holder;
            }
        }
    }

    public bool IsFree => Holder is null;

    /// <summary>Takes the gate, or returns null if something else holds it. Dispose the lease to release.</summary>
    public IDisposable? TryEnter(string operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        lock (_lock)
        {
            if (_holder is not null)
            {
                return null;
            }

            _holder = operation;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new Lease(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            _holder = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Lease(OperationGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
