using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VpnKillSwitch;

public partial class App : Application
{
    public static App Me => (App)Current;

    public AppSettings Settings { get; private set; } = new();
    public EngineConfig Config { get; set; } = new();
    public KillSwitchStatus Status { get; private set; } = new();
    public bool Busy { get; private set; }
    public string? LastError { get; private set; }
    public bool TasksRegistered { get; private set; }
    public bool InstalledScriptOutdated { get; private set; }
    public bool Exiting { get; private set; }

    /// <summary>Raised on the UI thread whenever anything shown in the UI may have changed.</summary>
    public event Action? StateChanged;

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _miStatus, _miOn, _miOff, _miWidget, _miExit;

    /// <summary>When the VPN went down while the kill switch was on (null while not blocking).</summary>
    public DateTime? BlockingSince { get; private set; }
    private MainWindow? _main;
    private WidgetWindow? _widget;
    private DispatcherTimer? _timer;
    private bool _refreshing;
    private bool _statusKnown;
    private ProtectionState _lastState;
    private string? _lastAutoCoverKey;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Developer helper: "VpnKillSwitch.exe --export-icon app.ico" regenerates the exe icon.
        if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
        {
            Icons.ExportIco(e.Args[1]);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "VpnKillSwitch.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: ask that instance to show its window.
            try { EventWaitHandle.OpenExisting("VpnKillSwitch.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "VpnKillSwitch.Show");
        new Thread(() =>
        {
            while (true)
            {
                _showSignal.WaitOne();
                Dispatcher.BeginInvoke(new Action(() => ShowMain()));
            }
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "VPN Kill Switch", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Settings = AppSettings.Load();
        Config = EngineConfig.Load();

        CreateTray();
        _main = CreateMain();
        if (!Settings.StartMinimized) _main.Show();
        UpdateWidgetVisibility();

        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        await RefreshTasksAsync();
        await RefreshAsync();
        if (Settings.EnableOnStart && !Status.On) await SetKillSwitchAsync(true);
    }

    // ---------------- state ----------------

    public async Task RefreshAsync()
    {
        if (_refreshing || Exiting) return;
        _refreshing = true;
        try
        {
            var pattern = Config.VpnAdapterPattern;
            Status = await Task.Run(() => StatusReader.Read(pattern));

            if (_statusKnown && !Busy && _lastState == ProtectionState.Protected && Status.State == ProtectionState.Blocking)
                Notify("VPN disconnected", "The kill switch is blocking all traffic until the VPN reconnects.", WinForms.ToolTipIcon.Warning);
            if (Status.State == ProtectionState.Blocking) BlockingSince ??= DateTime.Now;
            else BlockingSince = null;
            _lastState = Status.State;
            _statusKnown = true;
            RaiseChanged();
        }
        finally { _refreshing = false; }

        // A new adapter (USB Wi-Fi, tethering) is not covered until the rules are re-applied.
        if (Status.On && Status.Uncovered.Count > 0)
        {
            var key = string.Join("|", Status.Uncovered.OrderBy(n => n));
            if (key != _lastAutoCoverKey && Settings.AutoCoverNewAdapters && TasksRegistered && !Busy)
            {
                _lastAutoCoverKey = key;
                await SetKillSwitchAsync(true);
            }
        }
        else _lastAutoCoverKey = null;
    }

    public async Task SetKillSwitchAsync(bool on)
    {
        if (Busy) return;
        Busy = true;
        LastError = null;
        RaiseChanged();
        EngineResult result;
        try { result = await Engine.RunAsync(on ? "on" : "off", TasksRegistered); }
        finally { Busy = false; }
        if (!result.Success && !result.Cancelled)
        {
            LastError = result.Error;
            Notify("Kill switch error", result.Error ?? "Unknown error", WinForms.ToolTipIcon.Error);
        }
        await RefreshAsync();
        RaiseChanged();
    }

    /// <summary>Turning protection off is the one destructive action, so it always asks first.</summary>
    public async Task RequestTurnOffAsync(Window? owner)
    {
        if (Busy || !Status.On) return;
        bool ok = ConfirmDialog.Ask(owner, "Turn off the kill switch?",
            "Apps will use your normal connection whenever the VPN is down." +
            (TasksRegistered ? "" : " Windows will ask for administrator approval."),
            "Turn off");
        if (ok) await SetKillSwitchAsync(false);
    }

    /// <summary>Creates or removes the scheduled tasks used for switching without a UAC prompt.</summary>
    public async Task SetPromptFreeAsync(bool enable)
    {
        if (Busy) return;
        Busy = true;
        LastError = null;
        RaiseChanged();
        EngineResult result;
        try { result = await Engine.RunAsync(enable ? "register" : "unregister", useTasks: false); }
        finally { Busy = false; }
        if (!result.Success && !result.Cancelled) LastError = result.Error;
        await RefreshTasksAsync();
        RaiseChanged();
    }

    private async Task RefreshTasksAsync()
    {
        (TasksRegistered, InstalledScriptOutdated) =
            await Task.Run(() => (Engine.TasksRegistered(), Engine.InstalledScriptOutdated()));
    }

    public void SaveSettings()
    {
        Settings.Save();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        UpdateTray();
        StateChanged?.Invoke();
    }

    // ---------------- windows ----------------

    public void ShowMain(int tab = -1)
    {
        _main ??= CreateMain();
        if (tab >= 0) _main.SelectTab(tab);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void SetWidgetVisible(bool visible)
    {
        Settings.ShowWidget = visible;
        UpdateWidgetVisibility();
        SaveSettings();
    }

    private MainWindow CreateMain()
    {
        var w = new MainWindow();
        w.IsVisibleChanged += (_, _) => UpdateWidgetVisibility();
        w.StateChanged += (_, _) => UpdateWidgetVisibility();
        return w;
    }

    /// <summary>
    /// The widget stands in for the main window: it is shown only while that window is
    /// hidden in the tray or minimized, and only if the widget is enabled in the settings.
    /// </summary>
    private void UpdateWidgetVisibility()
    {
        if (Exiting) return;
        bool mainOpen = _main is { IsVisible: true } && _main.WindowState != WindowState.Minimized;
        if (Settings.ShowWidget && !mainOpen) ShowWidget();
        else if (_widget != null) { _widget.Close(); _widget = null; }
    }

    public void SetWidgetMode(bool? onTop = null, bool? inTaskbar = null)
    {
        if (onTop.HasValue) Settings.WidgetAlwaysOnTop = onTop.Value;
        if (inTaskbar.HasValue) Settings.WidgetInTaskbar = inTaskbar.Value;
        _widget?.ApplyMode();
        SaveSettings();
    }

    public void MoveWidget(WidgetCorner corner)
    {
        Settings.WidgetCorner = corner;
        _widget?.Place();
        SaveSettings();
    }

    private void ShowWidget()
    {
        if (_widget != null) return;
        _widget = new WidgetWindow();
        _widget.Closed += (_, _) => _widget = null;
        _widget.Show();
    }

    public void ExitApp()
    {
        Exiting = true;
        _timer?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    // ---------------- tray ----------------

    private void CreateTray()
    {
        var font = new System.Drawing.Font("Segoe UI", 9.75f);
        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = font,
            BackColor = System.Drawing.Color.FromArgb(0xF9, 0xF9, 0xF9),
            Renderer = new WinForms.ToolStripProfessionalRenderer(new TrayMenuColors()) { RoundedEdges = true },
            Padding = new WinForms.Padding(4),
        };

        _miStatus = new WinForms.ToolStripMenuItem("VPN Kill Switch") { Enabled = false, Font = new System.Drawing.Font(font, System.Drawing.FontStyle.Bold) };
        var open = new WinForms.ToolStripMenuItem("Open VPN Kill Switch", null, (s, e) => ShowMain(0));
        _miOn = new WinForms.ToolStripMenuItem("Re-apply rules", null, (s, e) => { _ = SetKillSwitchAsync(true); });
        _miWidget = new WinForms.ToolStripMenuItem("Hide desktop widget", null, (s, e) => SetWidgetVisible(!Settings.ShowWidget));
        _miOff = new WinForms.ToolStripMenuItem("Turn off kill switch...", null, (s, e) => { _ = RequestTurnOffAsync(null); })
        {
            ForeColor = System.Drawing.Color.FromArgb(0xB1, 0x26, 0x19),
        };
        _miExit = new WinForms.ToolStripMenuItem("Exit", null, (s, e) => ExitApp());

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _miStatus, new WinForms.ToolStripSeparator(),
            open, _miOn, _miWidget, new WinForms.ToolStripSeparator(),
            _miOff, _miExit,
        });
        foreach (WinForms.ToolStripItem item in menu.Items)
            if (item is WinForms.ToolStripMenuItem) item.Padding = new WinForms.Padding(6, 5, 6, 5);

        _tray = new WinForms.NotifyIcon { Text = "VPN Kill Switch", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (s, e) => ShowMain(0);
        // A single click raises the widget, for when it is not always-on-top and got covered.
        _tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) _widget?.BringToFront(); };
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray == null || Exiting) return;
        var view = StateView.For(Status, Busy, Config.AllowLan);
        _tray.Icon = Icons.Get(view.Visual);
        _tray.Text = $"VPN Kill Switch: {view.WidgetTitle}";

        var detail = view.WidgetSub;
        if (!Busy && Status.State == ProtectionState.Protected && Status.Uncovered.Count == 0)
        {
            detail = string.Join(", ", Status.VpnAdapters.Where(a => a.Up).Select(a => a.Name));
            if (DateTime.TryParse(Status.EnabledAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var since))
                detail += $" · since {since:HH:mm}";
        }
        _miStatus!.Text = $"{view.WidgetTitle}  -  {detail}";
        _miOn!.Text = Status.On ? "Re-apply rules" : "Turn on kill switch";
        _miOn.Enabled = !Busy;
        _miOff!.Visible = Status.On;
        _miOff.Enabled = !Busy;
        _miWidget!.Text = Settings.ShowWidget ? "Hide desktop widget" : "Show desktop widget";
        _miExit!.Text = Status.On ? "Exit (rules stay active)" : "Exit";
    }

    private sealed class TrayMenuColors : WinForms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0xF9, 0xF9, 0xF9);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0xEA, 0xEA, 0xEA);
        public override System.Drawing.Color ToolStripDropDownBackground => Bg;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemBorder => Hover;
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(0xD6, 0xD6, 0xD6);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(0xE5, 0xE5, 0xE5);
        public override System.Drawing.Color SeparatorLight => Bg;
        public override System.Drawing.Color ImageMarginGradientBegin => Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => Bg;
    }

    private void Notify(string title, string text, WinForms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(5000, title, text, icon);
}
