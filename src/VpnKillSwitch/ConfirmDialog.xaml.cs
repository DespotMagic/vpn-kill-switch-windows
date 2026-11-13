using System.Windows;
using System.Windows.Input;

namespace VpnKillSwitch;

/// <summary>Small modal dialog in the app's own style: a destructive confirmation or a plain notice.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(Window? owner, string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>Returns true when the user confirmed the destructive action.</summary>
    public static bool Ask(Window? owner, string title, string body, string confirmText)
    {
        var d = new ConfirmDialog(owner, title, body);
        d.BtnConfirm.Content = confirmText;
        return d.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string body)
    {
        var d = new ConfirmDialog(owner, title, body);
        d.BtnConfirm.Visibility = Visibility.Collapsed;
        d.Buttons.Columns = 1;
        d.BtnCancel.Content = "OK";
        d.BtnCancel.Margin = new Thickness(0);
        d.ShowDialog();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
