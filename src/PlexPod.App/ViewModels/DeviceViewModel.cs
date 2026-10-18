using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PlexPod.App.Services;
using PlexPod.Core.Models;
using PlexPod.Device;

namespace PlexPod.App.ViewModels;

/// <summary>The connected Rockbox player(s). Read-only: M1 never writes to the device.</summary>
public sealed partial class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly DeviceWatcher _watcher;

    public DeviceViewModel(DeviceWatcher watcher)
    {
        _watcher = watcher;
        _watcher.DeviceArrived += OnWatcherEvent;
        _watcher.DeviceRemoved += OnWatcherEvent;
        _watcher.DeviceChanged += OnWatcherEvent;
    }

    /// <summary>Raised on the UI thread whenever the selected device or its free space changes.</summary>
    public event EventHandler? CapacityChanged;

    public ObservableCollection<RockboxDevice> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescriptionText), nameof(WarningText), nameof(HasDevice))]
    public partial RockboxDevice? SelectedDevice { get; set; }

    public bool HasDevice => SelectedDevice is not null;

    public DeviceCapacity? Capacity => SelectedDevice?.Capacity;

    public string DescriptionText => SelectedDevice is { } d
        ? $"iPod: {d.DriveRoot.TrimEnd('\\')} \"{d.VolumeLabel}\" · Rockbox {d.RockboxTarget ?? "?"} {d.RockboxVersion ?? string.Empty} · {Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)}"
        : "No Rockbox iPod connected. Plug it in (disk mode) and it will appear here.";

    public string WarningText => SelectedDevice is { IsSupportedTarget: false } d
        ? $"Target \"{d.RockboxTarget ?? "unknown"}\" isn't an iPod Video (ipodvideo); other Rockbox players are untested."
        : string.Empty;

    /// <summary>
    /// The watcher's first scan runs straight away on a background thread. Scanning on the UI thread
    /// would freeze the window while a spun-down iPod disk wakes up.
    /// </summary>
    public void Start() => _watcher.Start();

    public void Dispose()
    {
        _watcher.DeviceArrived -= OnWatcherEvent;
        _watcher.DeviceRemoved -= OnWatcherEvent;
        _watcher.DeviceChanged -= OnWatcherEvent;
    }

    partial void OnSelectedDeviceChanged(RockboxDevice? value) => CapacityChanged?.Invoke(this, EventArgs.Empty);

    private void OnWatcherEvent(object? sender, RockboxDeviceEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(Sync);

    private void Sync()
    {
        var current = _watcher.Devices.OrderBy(d => d.DriveRoot, StringComparer.OrdinalIgnoreCase).ToList();
        var selectedRoot = SelectedDevice?.DriveRoot;
        Devices.Clear();
        foreach (var device in current)
        {
            Devices.Add(device);
        }

        // Keep the same drive selected (with fresh free-space numbers), else prefer a supported iPod.
        SelectedDevice = current.FirstOrDefault(d => string.Equals(d.DriveRoot, selectedRoot, StringComparison.OrdinalIgnoreCase))
            ?? current.FirstOrDefault(d => d.IsSupportedTarget)
            ?? current.FirstOrDefault();
    }
}
