using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlexPod.Core.Models;

namespace PlexPod.App.ViewModels;

/// <summary>One row in a picker: a playlist, artist or album with a checkbox.</summary>
public sealed partial class PickerItemViewModel(string key, string guid, string title, string searchText) : ObservableObject
{
    internal PickerListViewModel? Owner { get; set; }

    public string Key { get; } = key;

    public string Guid { get; } = guid;

    public string Title { get; } = title;

    internal string SearchText { get; } = searchText;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>E.g. "smart · 142 tracks" or "12 albums · 140 tracks".</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Estimated size on the iPod, prefixed with "≈" when conservative.</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    /// <summary>A badge such as "smart" or "hi-res → 16/44.1".</summary>
    [ObservableProperty]
    public partial string Badge { get; set; } = string.Empty;

    public ItemRef ToRef() => new(Key, Guid, Title);

    partial void OnIsCheckedChanged(bool value) => Owner?.OnItemChecked();
}

/// <summary>
/// A filterable list of picker rows with Select all / Clear. <see cref="View"/> is the rows that match
/// the filter, rebuilt when the filter or the items change; the UI virtualises it.
/// </summary>
public sealed partial class PickerListViewModel : ObservableObject
{
    private int _suppress;

    public PickerListViewModel(string header)
    {
        Header = header;
    }

    public event EventHandler? Changed;

    public string Header { get; }

    public IReadOnlyList<PickerItemViewModel> Items { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<PickerItemViewModel> View { get; private set; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int CheckedCount { get; private set; }

    public string HeaderText => CheckedCount > 0 ? $"{Header} ({CheckedCount:N0})" : Header;

    public IEnumerable<PickerItemViewModel> Checked => Items.Where(i => i.IsChecked);

    public void SetItems(IReadOnlyList<PickerItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.Owner = this;
        }

        Items = items;
        View = Filtered();
        UpdateCount();
    }

    /// <summary>Checks exactly the items whose keys are given, raising <see cref="Changed"/> once.</summary>
    public void SetChecked(IReadOnlySet<string> keys) =>
        Bulk(() =>
        {
            foreach (var item in Items)
            {
                item.IsChecked = keys.Contains(item.Key);
            }
        });

    internal void OnItemChecked()
    {
        if (_suppress == 0)
        {
            UpdateCount();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void SelectAll() =>
        Bulk(() =>
        {
            foreach (var item in View)
            {
                item.IsChecked = true;
            }
        });

    [RelayCommand]
    private void Clear() =>
        Bulk(() =>
        {
            foreach (var item in View)
            {
                item.IsChecked = false;
            }
        });

    partial void OnFilterTextChanged(string value) => View = Filtered();

    partial void OnCheckedCountChanged(int value) => OnPropertyChanged(nameof(HeaderText));

    private void Bulk(Action action)
    {
        _suppress++;
        try
        {
            action();
        }
        finally
        {
            _suppress--;
        }

        UpdateCount();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCount() => CheckedCount = Items.Count(i => i.IsChecked);

    private List<PickerItemViewModel> Filtered()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return [.. Items];
        }

        var filter = FilterText.Trim();
        return Items.Where(item => item.SearchText.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }
}
