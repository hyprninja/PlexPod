using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlexPod.Mac;

/// <summary>The sync plan, with Start sync / Cancel (or just Close when there's nothing to start). Returns true only for Start.</summary>
public sealed partial class SyncConfirmWindow : Window
{
    public SyncConfirmWindow()
        : this(string.Empty, string.Empty, canStart: false)
    {
    }

    public SyncConfirmWindow(string title, string summary, bool canStart)
    {
        InitializeComponent();
        Heading.Text = title;
        Summary.Text = summary;
        StartButton.IsVisible = canStart;
        CloseButton.Content = canStart ? "Cancel" : "Close";
        CloseButton.IsDefault = !canStart;
    }

    private void OnStart(object? sender, RoutedEventArgs e) => Close(true);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>A plain message with an OK button, for errors.</summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        var panel = new DockPanel { Margin = new Avalonia.Thickness(20) };
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 16) });
        Content = panel;
    }

    public static Task ShowAsync(Window owner, string title, string message) => new MessageWindow(title, message).ShowDialog(owner);
}
