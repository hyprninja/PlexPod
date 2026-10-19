using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlexPod.App.Services;
using PlexPod.Core.Stats;
using PlexPod.Core.Tagging;
using PlexPod.Media.Ffmpeg;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Stats;

namespace PlexPod.App.ViewModels;

/// <summary>One row of the "Tags written to the iPod" checklist.</summary>
public sealed partial class TagFieldViewModel(TagField field, bool isOn, Action changed) : ObservableObject
{
    public TagField Field { get; } = field;

    public string DisplayName => Field.DisplayName;

    public bool IsCore => Field.IsCore;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;

    partial void OnIsOnChanged(bool value) => changed();
}

/// <summary>ffmpeg location, conversion-cache cap, the per-field tag policy (D4) and stats return (D10, D11). Saved on change.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly FfmpegLocator _locator;
    private readonly ConversionCache _cache;
    private bool _loading;

    public SettingsViewModel(SettingsStore settings, FfmpegLocator locator, ConversionCache cache)
    {
        _settings = settings;
        _locator = locator;
        _cache = cache;

        foreach (var field in TagPolicy.Fields)
        {
            (field.Source == TagFieldSource.Plex ? PlexFields : FileFields).Add(new TagFieldViewModel(field, isOn: false, OnPolicyChanged));
        }

        Reload();
    }

    /// <summary>
    /// Shows the settings as saved now, including changes another PlexPod window made since this one
    /// opened. Called when the Settings window opens.
    /// </summary>
    public void Reload()
    {
        var current = _settings.Current;
        _loading = true;
        FfmpegPath = current.FfmpegPath ?? string.Empty;
        CacheCapGb = current.ConversionCacheCapGb.ToString(CultureInfo.CurrentCulture);
        _cache.CapBytes = Math.Max(1, current.ConversionCacheCapGb) * 1024L * 1024 * 1024;
        StatsReturn = current.StatsReturn;
        RatingsFillGapsOnly = current.RatingsFromIpod == RatingMode.FillGapsOnly;
        foreach (var row in PlexFields.Concat(FileFields))
        {
            row.IsOn = row.Field.Get(current.TagPolicy);
        }

        _loading = false;
        UpdateCoreWarning();
    }

    /// <summary>Raised when ffmpeg is (re)detected or the tag policy changes.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<TagFieldViewModel> PlexFields { get; } = [];

    public ObservableCollection<TagFieldViewModel> FileFields { get; } = [];

    /// <summary>The tag policy as saved (by this window or another PlexPod), which is what a sync uses.</summary>
    public TagPolicy Policy => _settings.Current.TagPolicy;

    /// <summary>The policy the checklist shows; saved on every change.</summary>
    private TagPolicy CheckedPolicy => PlexFields.Concat(FileFields).Aggregate(TagPolicy.Default, (p, row) => row.Field.Set(p, row.IsOn));

    public FfmpegInstall? Ffmpeg { get; private set; }

    [ObservableProperty]
    public partial string FfmpegPath { get; set; }

    [ObservableProperty]
    public partial string FfmpegStatus { get; private set; } = "Looking for ffmpeg…";

    [ObservableProperty]
    public partial string CacheCapGb { get; set; }

    [ObservableProperty]
    public partial string CacheUsage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CoreWarning { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatsReturn { get; set; }

    /// <summary>D10. The two radio buttons bind to this and <see cref="RatingsIpodWins"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingsIpodWins))]
    public partial bool RatingsFillGapsOnly { get; set; }

    public bool RatingsIpodWins
    {
        get => !RatingsFillGapsOnly;
        set => RatingsFillGapsOnly = !value;
    }

    /// <summary>D10, as saved.</summary>
    public RatingMode RatingMode => _settings.Current.RatingsFromIpod;

    /// <summary>What a sync sends to Plex first, as saved; null when stats return is switched off.</summary>
    public StatsOptions? StatsOptions => _settings.Current.StatsReturn ? new StatsOptions(RatingMode) : null;

    [RelayCommand]
    public async Task DetectAsync()
    {
        FfmpegStatus = "Looking for ffmpeg…";
        Ffmpeg = await _locator.LocateAsync(FfmpegPath, CancellationToken.None);
        FfmpegStatus = Ffmpeg is null
            ? "ffmpeg not found. Install it (winget install Gyan.FFmpeg) or enter the path to ffmpeg.exe."
            : $"{Ffmpeg.Description} — {Ffmpeg.Path}";
        UpdateCacheUsage();
        await _settings.UpdateAsync(s => s with { FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim() }, CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        _loading = true;
        foreach (var row in PlexFields.Concat(FileFields))
        {
            row.IsOn = row.Field.Get(TagPolicy.Default);
        }

        _loading = false;
        await SavePolicyAsync();
    }

    public void UpdateCacheUsage() =>
        CacheUsage = $"Using {Format.Bytes(_cache.SizeBytes())} of the cap.";

    partial void OnCacheCapGbChanged(string value)
    {
        if (_loading || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var gb) || gb < 1)
        {
            return;
        }

        _cache.CapBytes = gb * 1024L * 1024 * 1024;
        _ = _settings.UpdateAsync(s => s with { ConversionCacheCapGb = gb }, CancellationToken.None);
    }

    partial void OnStatsReturnChanged(bool value)
    {
        if (!_loading)
        {
            _ = _settings.UpdateAsync(s => s with { StatsReturn = value }, CancellationToken.None);
        }
    }

    partial void OnRatingsFillGapsOnlyChanged(bool value)
    {
        if (!_loading)
        {
            var mode = value ? RatingMode.FillGapsOnly : RatingMode.IpodWins;
            _ = _settings.UpdateAsync(s => s with { RatingsFromIpod = mode }, CancellationToken.None);
        }
    }

    private async void OnPolicyChanged()
    {
        if (!_loading)
        {
            await SavePolicyAsync();
        }
    }

    private async Task SavePolicyAsync()
    {
        UpdateCoreWarning();
        var policy = CheckedPolicy;
        await _settings.UpdateAsync(s => s with { TagPolicy = policy }, CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCoreWarning()
    {
        var off = PlexFields.Where(r => r.IsCore && !r.IsOn).Select(r => r.DisplayName).ToList();
        CoreWarning = off.Count == 0
            ? string.Empty
            : $"{string.Join(", ", off)} {(off.Count == 1 ? "is" : "are")} off: Rockbox's database will file these tracks under <Untagged>.";
    }
}
