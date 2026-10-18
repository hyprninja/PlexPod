namespace RockPlex.Device;

public sealed class RockboxDeviceEventArgs(RockboxDevice device) : EventArgs
{
    public RockboxDevice Device { get; } = device;
}

/// <summary>
/// Polls for Rockbox devices (every 2 s by default) and raises arrival/removal events.
/// <see cref="DeviceChanged"/> fires when a known device's free space or info changes.
/// Events are raised on a thread-pool thread.
/// </summary>
public sealed class DeviceWatcher(IRockboxDeviceLocator locator, TimeProvider time) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private Dictionary<string, RockboxDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<RockboxDeviceEventArgs>? DeviceArrived;

    public event EventHandler<RockboxDeviceEventArgs>? DeviceRemoved;

    public event EventHandler<RockboxDeviceEventArgs>? DeviceChanged;

    public TimeSpan Interval { get; init; } = DefaultInterval;

    public IReadOnlyList<RockboxDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices.Values];
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Task.Run, not Task.Yield: called from the UI thread, Task.Yield would resume there and
            // the first (possibly slow) drive scan would freeze the window.
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    /// <summary>One scan; compares with the last one and raises events. Used by the loop and by tests.</summary>
    public void Poll()
    {
        var found = locator.FindDevices().ToDictionary(d => d.DriveRoot, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RockboxDevice> previous;
        lock (_gate)
        {
            previous = _devices;
            _devices = found;
        }

        foreach (var (root, device) in previous)
        {
            if (!found.ContainsKey(root))
            {
                DeviceRemoved?.Invoke(this, new RockboxDeviceEventArgs(device));
            }
        }

        foreach (var (root, device) in found)
        {
            if (!previous.TryGetValue(root, out var old))
            {
                DeviceArrived?.Invoke(this, new RockboxDeviceEventArgs(device));
            }
            else if (old != device)
            {
                DeviceChanged?.Invoke(this, new RockboxDeviceEventArgs(device));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            (loop, cts, _loop, _cts) = (_loop, _cts, null, null);
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            Poll();
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
}
