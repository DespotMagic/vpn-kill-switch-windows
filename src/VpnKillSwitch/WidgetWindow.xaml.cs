using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace VpnKillSwitch;

/// <summary>Small always-on-top pill that shows the state and toggles the kill switch.</summary>
public partial class WidgetWindow : Window
{
    private const double EdgeMargin = 4;   // plus the 12px shadow margin inside the window
    private static App A => App.Me;

    public WidgetWindow()
    {
        InitializeComponent();
        Icon = Icons.WindowIcon;
        Topmost = A.Settings.WidgetAlwaysOnTop;
        ShowInTaskbar = A.Settings.WidgetInTaskbar;
        A.StateChanged += UpdateUi;
        Closed += (_, _) => A.StateChanged -= UpdateUi;
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();   // keep it anchored when the text width changes
        UpdateUi();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyMode();
    }

    /// <summary>Applies "always on top" and "show in taskbar" from the settings.</summary>
    public void ApplyMode()
    {
        Topmost = A.Settings.WidgetAlwaysOnTop;
        ShowInTaskbar = A.Settings.WidgetInTaskbar;

        // Without a taskbar button it is a tool window, so it has no Alt+Tab entry either.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = A.Settings.WidgetInTaskbar ? style & ~WS_EX_TOOLWINDOW : style | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Raises the widget above other windows without taking keyboard focus.</summary>
    public void BringToFront()
    {
        Topmost = true;
        Topmost = A.Settings.WidgetAlwaysOnTop;
    }

    private void UpdateUi()
    {
        var s = A.Status;
        var view = StateView.For(s, A.Busy, A.Config.AllowLan);
        Dot.Fill = new SolidColorBrush(view.Dot);
        TitleText.Text = view.WidgetTitle;
        SubText.Text = view.WidgetSub;

        MiOnTop.IsChecked = A.Settings.WidgetAlwaysOnTop;
        MiTaskbar.IsChecked = A.Settings.WidgetInTaskbar;

        Toggle.IsEnabled = !A.Busy;
        Toggle.Content = s.On ? "Turn OFF" : "Turn ON";
        Toggle.Background = new SolidColorBrush(
            A.Busy ? Color.FromRgb(0x3A, 0x3E, 0x49) : s.On ? Color.FromRgb(0x4A, 0x4F, 0x5C) : Color.FromRgb(0x1E, 0x8E, 0x4E));
        Toggle.Foreground = new SolidColorBrush(A.Busy ? Color.FromRgb(0x8A, 0x8F, 0x99) : Colors.White);
    }

    // ---------------- position ----------------

    /// <summary>Moves the widget to the corner stored in the settings.</summary>
    public void Place()
    {
        if (!IsLoaded) return;
        var screen = WinForms.Screen.AllScreens.FirstOrDefault(sc => sc.DeviceName == A.Settings.WidgetScreen)
                     ?? WinForms.Screen.PrimaryScreen;
        if (screen == null) return;

        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var wa = screen.WorkingArea;
        var topLeft = toDip.Transform(new Point(wa.Left, wa.Top));
        var bottomRight = toDip.Transform(new Point(wa.Right, wa.Bottom));

        var corner = A.Settings.WidgetCorner;
        Left = corner is WidgetCorner.TopLeft or WidgetCorner.BottomLeft
            ? topLeft.X + EdgeMargin
            : bottomRight.X - ActualWidth - EdgeMargin;
        Top = corner is WidgetCorner.TopLeft or WidgetCorner.TopRight
            ? topLeft.Y + EdgeMargin
            : bottomRight.Y - ActualHeight - EdgeMargin;
    }

    private void SnapToNearestCorner()
    {
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));   // device pixels
        var screen = WinForms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
        var wa = screen.WorkingArea;
        bool left = center.X < wa.Left + wa.Width / 2.0;
        bool top = center.Y < wa.Top + wa.Height / 2.0;

        A.Settings.WidgetScreen = screen.DeviceName;
        A.Settings.WidgetCorner = top
            ? (left ? WidgetCorner.TopLeft : WidgetCorner.TopRight)
            : (left ? WidgetCorner.BottomLeft : WidgetCorner.BottomRight);
        Place();
        A.SaveSettings();
    }

    // ---------------- input ----------------

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            A.ShowMain(0);
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SnapToNearestCorner();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (A.Status.On) await A.RequestTurnOffAsync(null);
        else await A.SetKillSwitchAsync(true);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => A.ShowMain(0);
    private void Settings_Click(object sender, RoutedEventArgs e) => A.ShowMain(2);
    private void Hide_Click(object sender, RoutedEventArgs e) => A.SetWidgetVisible(false);
    private void OnTop_Click(object sender, RoutedEventArgs e) => A.SetWidgetMode(onTop: !A.Settings.WidgetAlwaysOnTop);
    private void Taskbar_Click(object sender, RoutedEventArgs e) => A.SetWidgetMode(inTaskbar: !A.Settings.WidgetInTaskbar);

    private void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int i))
            A.MoveWidget((WidgetCorner)i);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
