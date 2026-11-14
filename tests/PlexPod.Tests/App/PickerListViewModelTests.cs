using PlexPod.App.ViewModels;

namespace PlexPod.Tests.App;

public sealed class PickerListViewModelTests
{
    private static PickerListViewModel List(params string[] titles)
    {
        var list = new PickerListViewModel("Artists");
        list.SetItems(titles.Select((t, i) => new PickerItemViewModel(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "g" + i, t, t)).ToList());
        return list;
    }

    [Fact]
    public void Filter_NarrowsTheView_CaseInsensitively_AndClearingItShowsAll()
    {
        var list = List("Sigur Rós", "Björk", "Sigrid");
        Assert.Equal(3, list.View.Count);

        list.FilterText = "  sig ";
        Assert.Equal(["Sigur Rós", "Sigrid"], list.View.Select(i => i.Title));

        list.FilterText = string.Empty;
        Assert.Equal(3, list.View.Count);
    }

    [Fact]
    public void SelectAllAndClear_OnlyTouchTheVisibleRows_AndRaiseChangedOnce()
    {
        var list = List("Sigur Rós", "Björk", "Sigrid");
        var changes = 0;
        list.Changed += (_, _) => changes++;

        list.FilterText = "sig";
        list.SelectAllCommand.Execute(null);
        Assert.Equal(1, changes);
        Assert.Equal(2, list.CheckedCount);
        Assert.False(list.Items.Single(i => i.Title == "Björk").IsChecked);
        Assert.Equal("Artists (2)", list.HeaderText);

        list.FilterText = "rós";
        list.ClearCommand.Execute(null);
        Assert.Equal(["Sigrid"], list.Checked.Select(i => i.Title));
    }
}
