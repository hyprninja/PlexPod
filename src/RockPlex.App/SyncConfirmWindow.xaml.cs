using System.Windows;
using RockPlex.App.ViewModels;

namespace RockPlex.App;

public partial class SyncConfirmWindow : Window
{
    public SyncConfirmWindow() => InitializeComponent();

    private void OnStart(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>Shows the sync plan in a modal dialog owned by the main window.</summary>
public sealed class SyncConfirmation : ISyncConfirmation
{
    public bool Confirm(string title, string summary, bool canStart)
    {
        var window = new SyncConfirmWindow { Owner = Application.Current.MainWindow };
        window.Heading.Text = title;
        window.Summary.Text = summary;
        window.StartButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        window.CloseButton.Content = canStart ? "Cancel" : "Close";
        return window.ShowDialog() == true;
    }
}
