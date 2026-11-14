using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlexPod.App.Services;
using PlexPod.App.ViewModels;
using PlexPod.Media;

namespace PlexPod.Mac.Services;

/// <summary>
/// Eject (Mac only, M7 plan §3): Mac users must eject before unplugging. Runs <c>diskutil eject</c> on the
/// selected iPod's volume, holding the operation gate so it can't overlap a sync, an export or Send plays now.
/// </summary>
public sealed partial class EjectViewModel : ObservableObject
{
    private readonly DeviceViewModel _device;
    private readonly OperationGate _gate;
    private readonly ILogger<EjectViewModel> _logger;

    public EjectViewModel(DeviceViewModel device, OperationGate gate, IUiThread ui, ILogger<EjectViewModel> logger)
    {
        _device = device;
        _gate = gate;
        _logger = logger;
        device.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceViewModel.SelectedDevice))
            {
                ui.Post(EjectCommand.NotifyCanExecuteChanged);
            }
        };
        gate.Changed += (_, _) => ui.Post(EjectCommand.NotifyCanExecuteChanged);
    }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    private bool CanEject() => _device.SelectedDevice is not null && _gate.IsFree;

    [RelayCommand(CanExecute = nameof(CanEject))]
    private async Task EjectAsync()
    {
        if (_device.SelectedDevice is not { } device)
        {
            return;
        }

        using var lease = _gate.TryEnter("Eject");
        if (lease is null)
        {
            Status = $"Wait for the {_gate.Holder?.ToLowerInvariant()} to finish first.";
            return;
        }

        Status = $"Ejecting {device.VolumeLabel}…";
        var start = new ProcessStartInfo("/usr/sbin/diskutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("eject");
        start.ArgumentList.Add(device.DriveRoot);
        try
        {
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Ejected {Root}", device.DriveRoot);
                Status = $"{device.VolumeLabel} ejected. You can unplug it.";
            }
            else
            {
                _logger.LogWarning("diskutil eject {Root} failed: {Error}", device.DriveRoot, error.Trim());
                Status = $"Couldn't eject: {(error.Length > 0 ? error : output).Trim()} Close anything using the iPod (Finder windows, Music) and try again.";
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "diskutil eject failed");
            Status = $"Couldn't eject: {ex.Message}";
        }
    }
}
