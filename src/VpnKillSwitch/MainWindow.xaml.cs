using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VpnKillSwitch;

public sealed record AdapterRow(string Name, string StatusText, Geometry? Icon);
public sealed record ServerRow(string Host, string Ip, string Source);

public partial class MainWindow : Window
{
    private static App A => App.Me;
    private bool _loading;
    private string? _listSignature;

    // working copy of the rules, saved with the save bar
    private readonly ObservableCollection<string> _allowed = new();
    private readonly ObservableCollection<string> _servers = new();

    public MainWindow()
    {
        InitializeComponent();
        Icon = Icons.WindowIcon;
        TitleIcon.Source = Icons.WindowIcon;
        AboutIcon.Source = Icons.WindowIcon;

        var v = typeof(App).Assembly.GetName().Version?.ToString(3);
        NavVersion.Text = $"Version {v}";
        AboutVersion.Text = $"Version {v}";
        AboutFiles.Text = $"Script:            {(File.Exists(Paths.InstalledScript) ? Paths.InstalledScript : Paths.BundledScript)}\n" +
                          $"Rule settings:     {Paths.ConfigFile}\n" +
                          $"Program settings:  {Paths.SettingsFile}";

        AllowedList.ItemsSource = _allowed;
        ExtraServerList.ItemsSource = _servers;
        LoadAppSettings();
        LoadRules();
        A.StateChanged += UpdateUi;
        UpdateUi();
    }

    private static SolidColorBrush B(Color c) => new(c);
    private static SolidColorBrush B(uint rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // ---------------- navigation ----------------

    /// <summary>0 = Status, 1 = Rules, 2 = App and widget, 3 = About</summary>
    public void SelectTab(int index)
    {
        var nav = index switch { 1 => NavRules, 2 => NavApp, 3 => NavAbout, _ => NavStatus };
        nav.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageAbout == null) return;   // still loading
        PageStatus.Visibility = NavStatus.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageRules.Visibility = NavRules.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageApp.Visibility = NavApp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TitleChip.Visibility = NavStatus.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GoRules_Click(object sender, RoutedEventArgs e) => SelectTab(1);
    private void GoStatus_Click(object sender, RoutedEventArgs e) => SelectTab(0);

    // ---------------- status ----------------

    private void UpdateUi()
    {
        var s = A.Status;
        var v = StateView.For(s, A.Busy, A.Config.AllowLan);
        var main = B(v.Main);
        var tint = B(v.Tint);

        // title-bar chip (shown on the other pages)
        TitleChip.Background = tint;
        TitleChipDot.Fill = main;
        TitleChipText.Foreground = main;
        TitleChipText.Text = v.Visual == VisualState.Off ? "Not protected" : v.WidgetTitle;
        TitleChip.Visibility = NavStatus.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

        // hero
        HeroCard.BorderBrush = B(v.Border);
        HeroCircle.Fill = tint;
        HeroGlyph.Stroke = main;
        HeroGlyph.Data = (Geometry)FindResource(v.Visual switch
        {
            VisualState.Protected => "GlyphProtected",
            VisualState.Blocking => "GlyphBlocking",
            VisualState.Attention => "GlyphAttention",
            VisualState.Busy => "GlyphBusy",
            _ => "GlyphOff",
        });
        HeroTitle.Text = v.Title;
        HeroDesc.Text = v.Description;

        bool showVpnChip = !A.Busy && s.On;
        ChipVpn.Visibility = showVpnChip ? Visibility.Visible : Visibility.Collapsed;
        if (showVpnChip && s.VpnUp)
        {
            ChipVpn.Background = B(0xDFF6DD);
            ChipVpnDot.Fill = B(0x0F7B0F);
            ChipVpnText.Foreground = B(0x0E6B0E);
            ChipVpnText.Text = "VPN connected · " + string.Join(", ", s.VpnAdapters.Where(a => a.Up).Select(a => a.Name));
        }
        else if (showVpnChip)
        {
            ChipVpn.Background = B(0xFFF4CE);
            ChipVpnDot.Fill = B(0x9D5D00);
            ChipVpnText.Foreground = B(0x8A5300);
            var since = A.BlockingSince;
            ChipVpnText.Text = since.HasValue ? $"Waiting {(int)(DateTime.Now - since.Value).TotalMinutes}:{(DateTime.Now - since.Value).Seconds:00}" : "VPN is down";
        }

        DateTime enabledAt = default;
        bool hasSince = s.On && s.EnabledAt != null &&
                        DateTime.TryParse(s.EnabledAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out enabledAt);
        ChipSince.Visibility = hasSince ? Visibility.Visible : Visibility.Collapsed;
        if (hasSince) ChipSinceText.Text = $"On since {enabledAt:HH:mm} · {Duration(DateTime.Now - enabledAt)}";
        ChipRow.Visibility = ChipVpn.Visibility == Visibility.Visible || hasSince ? Visibility.Visible : Visibility.Collapsed;

        bool attention = v.Visual == VisualState.Attention;
        KsLabel.Visibility = KsSwitch.Visibility = attention ? Visibility.Collapsed : Visibility.Visible;
        BtnReapplyNow.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
        BtnReapplyNow.IsEnabled = !A.Busy;
        KsSwitch.IsChecked = s.On;
        KsSwitch.IsEnabled = !A.Busy;

        BtnReapply.Visibility = s.On ? Visibility.Visible : Visibility.Collapsed;
        BtnReapply.IsEnabled = !A.Busy;
        FooterText.Text = s.On
            ? (hasSince ? $"Firewall rules last applied {enabledAt:HH:mm:ss} · " : "") +
              $"{Plural(s.BlockedAdapters.Count, "adapter")} blocked · {Plural(s.Servers.Count, "VPN server address", "VPN server addresses")} allowed"
            : "No firewall rules are active.";

        var errors = new List<string>();
        if (A.LastError != null) errors.Add("Error: " + A.LastError);
        if (s.ReadError != null) errors.Add(s.ReadError);
        ErrorText.Text = string.Join(Environment.NewLine, errors);
        ErrorBar.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateLists(s);

        // Rules page: info bar and save button
        if (!A.TasksRegistered)
        {
            InfoBar.Visibility = Visibility.Visible;
            InfoBarBold.Text = "Windows asks for administrator approval every time you switch.";
            InfoBarText.Text = " Turn on prompt-free switching to approve once - it also lets rules re-apply automatically.";
            InfoBarBtn.Content = "Turn on...";
        }
        else if (A.InstalledScriptOutdated)
        {
            InfoBar.Visibility = Visibility.Visible;
            InfoBarBold.Text = "The installed script is out of date.";
            InfoBarText.Text = " Update it so prompt-free switching uses the current version.";
            InfoBarBtn.Content = "Update";
        }
        else InfoBar.Visibility = Visibility.Collapsed;
        InfoBarBtn.IsEnabled = !A.Busy;
        BtnSave.Content = s.On ? "Save and re-apply" : "Save";
        BtnSave.IsEnabled = !A.Busy;

        // App and widget page: values that can change from outside this window
        _loading = true;
        SwWidget.IsChecked = A.Settings.ShowWidget;
        CmbCorner.SelectedIndex = (int)A.Settings.WidgetCorner;
        SwWidgetTop.IsChecked = A.Settings.WidgetAlwaysOnTop;
        SwWidgetTaskbar.IsChecked = A.Settings.WidgetInTaskbar;
        SwPromptFree.IsChecked = A.TasksRegistered;
        SwPromptFree.IsEnabled = !A.Busy;
        BtnUpdateScript.Visibility = A.TasksRegistered && A.InstalledScriptOutdated ? Visibility.Visible : Visibility.Collapsed;
        BtnUpdateScript.IsEnabled = !A.Busy;
        _loading = false;
    }

    private void UpdateLists(KillSwitchStatus s)
    {
        var cfg = A.Config;
        var sig = string.Join("|", s.On, cfg.AllowLan, cfg.BlockLanDns, cfg.AllowedDestinations.Length,
            string.Join(",", s.VpnAdapters.Select(a => a.Name + a.Up)),
            string.Join(",", s.BlockedAdapters), string.Join(",", s.Servers), string.Join(",", s.Exceptions));
        if (sig == _listSignature) return;
        _listSignature = sig;

        ActiveVpnList.ItemsSource = s.VpnAdapters.Where(a => a.Up).Select(a => new AdapterRow(a.Name, "Active", null)).ToList();
        IdleVpnList.ItemsSource = s.VpnAdapters.Where(a => !a.Up).Select(a => new AdapterRow(a.Name, "Idle", null)).ToList();
        VpnNone.Visibility = s.VpnAdapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        BlockedList.ItemsSource = s.BlockedAdapters.Select(n => new AdapterRow(n, "Blocked", AdapterIcon(n))).ToList();
        BlockedSub.Text = !s.On ? "Nothing is blocked while the kill switch is off"
            : cfg.AllowLan ? "Only VPN servers and the local network are reachable"
            : "Only VPN servers are reachable";

        var rows = s.Servers.Select(x =>
        {
            var p = x.Split(" -> ");
            var host = p[0].Trim();
            var ip = p.Length > 1 ? p[1].Trim() : host;
            bool extra = cfg.ExtraVpnServers.Contains(host, StringComparer.OrdinalIgnoreCase);
            return new ServerRow(host, ip, extra ? "Added by you" : "Pritunl");
        }).ToList();
        ServerList.ItemsSource = rows;
        ServerHeader.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerNone.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        int pritunl = rows.Where(r => r.Source == "Pritunl").Select(r => r.Host).Distinct().Count();
        PritunlCount.Text = s.On ? $"{Plural(pritunl, "server")} found automatically"
                                 : "Read from the Pritunl profiles when the kill switch is turned on";

        TileLan.Text = cfg.AllowLan ? "Reachable" : "Blocked";
        TileDns.Text = !cfg.AllowLan || cfg.BlockLanDns ? "Blocked (no leaks)" : "Allowed";
        int allowed = s.On ? s.Exceptions.Count : cfg.AllowedDestinations.Length;
        TileAllowed.Text = allowed == 0 ? "None" : Plural(allowed, "address", "addresses");
        ExceptionsTitle.Text = s.On ? "Exceptions in effect" : "Exceptions when turned on";
    }

    private Geometry AdapterIcon(string name)
    {
        string key = Regex.IsMatch(name, "wi-?fi|wireless|wlan", RegexOptions.IgnoreCase) ? "IcoWifi"
            : name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ? "IcoBluetooth"
            : name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ? "IcoMonitor"
            : name.Contains("vpn", StringComparison.OrdinalIgnoreCase) ? "IcoLock"
            : "IcoEthernet";
        return (Geometry)FindResource(key);
    }

    private static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    private static string Duration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "just now";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }

    private async void KsSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (KsSwitch.IsChecked == true) await A.SetKillSwitchAsync(true);
        else
        {
            KsSwitch.IsChecked = true;   // stays on until confirmed
            await A.RequestTurnOffAsync(this);
        }
    }

    private async void Reapply_Click(object sender, RoutedEventArgs e) => await A.SetKillSwitchAsync(true);

    // ---------------- rules ----------------

    private void LoadRules()
    {
        _loading = true;
        var cfg = A.Config;
        SwAllowLan.IsChecked = cfg.AllowLan;
        SwBlockDns.IsChecked = cfg.BlockLanDns;
        _allowed.Clear();
        foreach (var x in cfg.AllowedDestinations) _allowed.Add(x);
        _servers.Clear();
        foreach (var x in cfg.ExtraVpnServers) _servers.Add(x);
        TxtPattern.Text = cfg.VpnAdapterPattern;
        TxtAllowed.Text = TxtServer.Text = "";
        _loading = false;
        RulesChanged();
    }

    private bool RulesDirty()
    {
        var cfg = A.Config;
        return SwAllowLan.IsChecked != cfg.AllowLan
            || SwBlockDns.IsChecked != cfg.BlockLanDns
            || TxtPattern.Text.Trim() != cfg.VpnAdapterPattern
            || !_allowed.SequenceEqual(cfg.AllowedDestinations)
            || !_servers.SequenceEqual(cfg.ExtraVpnServers);
    }

    private void RulesChanged()
    {
        if (_loading) return;
        AllowedEmpty.Visibility = _allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveBarText.Text = "You have unsaved changes";
        SaveBar.Visibility = RulesDirty() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RuleChanged(object sender, RoutedEventArgs e) => RulesChanged();

    private void TxtPattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PatternMatches == null) return;
        try
        {
            var re = new Regex(TxtPattern.Text.Trim().Length == 0 ? EngineConfig.DefaultPattern : TxtPattern.Text.Trim(), RegexOptions.IgnoreCase);
            var names = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => !Regex.IsMatch(n.Name, @"-\d{4}$") && (re.IsMatch(n.Name) || re.IsMatch(n.Description)))
                .Select(n => n.Name).OrderBy(n => n).ToList();
            PatternMatches.Foreground = names.Count > 0 ? B(0x0E6B0E) : B(0x8A5300);
            PatternMatches.Text = names.Count > 0
                ? $"Matches {Plural(names.Count, "adapter")}: {string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", ..." : "")}"
                : "Matches no adapter on this PC right now.";
        }
        catch (ArgumentException)
        {
            PatternMatches.Foreground = B(0xC42B1C);
            PatternMatches.Text = "Not a valid regular expression.";
        }
        RulesChanged();
    }

    private void AddEntries(TextBox box, ObservableCollection<string> target, bool allowRanges)
    {
        var parts = box.Text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var x = raw.Trim();
            bool bad = x.Contains('*') || x.Contains("://") || (!allowRanges && x.Contains('/'));
            if (bad)
            {
                ConfirmDialog.Info(this, "That entry can't be used",
                    $"\"{x}\" is not supported. Enter a plain host name (example.com), an IP address" +
                    (allowRanges ? " or a range (198.51.100.0/24)." : ".") + " Wildcards and URLs do not work.");
                return;
            }
            if (!target.Contains(x, StringComparer.OrdinalIgnoreCase)) target.Add(x);
        }
        box.Text = "";
        RulesChanged();
    }

    private void AddAllowed_Click(object sender, RoutedEventArgs e) => AddEntries(TxtAllowed, _allowed, allowRanges: true);
    private void AddServer_Click(object sender, RoutedEventArgs e) => AddEntries(TxtServer, _servers, allowRanges: false);
    private void TxtAllowed_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddEntries(TxtAllowed, _allowed, true); }
    private void TxtServer_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddEntries(TxtServer, _servers, false); }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string item } btn) return;
        DependencyObject? d = btn;
        while (d != null && d is not ItemsControl) d = VisualTreeHelper.GetParent(d);
        (ReferenceEquals(d, ExtraServerList) ? _servers : _allowed).Remove(item);
        RulesChanged();
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => LoadRules();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var pattern = TxtPattern.Text.Trim();
        if (pattern.Length == 0) pattern = EngineConfig.DefaultPattern;
        try { _ = new Regex(pattern); }
        catch (ArgumentException ex)
        {
            ConfirmDialog.Info(this, "The adapter pattern is not valid", ex.Message);
            return;
        }

        var cfg = new EngineConfig
        {
            AllowLan = SwAllowLan.IsChecked == true,
            BlockLanDns = SwBlockDns.IsChecked == true,
            VpnAdapterPattern = pattern,
            ExtraVpnServers = _servers.ToArray(),
            AllowedDestinations = _allowed.ToArray(),
        };
        try { cfg.Save(); }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "Could not save the rules", $"{Paths.ConfigFile}\n{ex.Message}");
            return;
        }
        A.Config = cfg;
        _listSignature = null;
        LoadRules();

        if (A.Status.On) await A.SetKillSwitchAsync(true);
        else await A.RefreshAsync();
    }

    private async void InfoBarBtn_Click(object sender, RoutedEventArgs e) => await A.SetPromptFreeAsync(true);

    // ---------------- app and widget ----------------

    private void LoadAppSettings()
    {
        _loading = true;
        var st = A.Settings;
        SwAutostart.IsChecked = Autostart.IsEnabled();
        SwStartMin.IsChecked = st.StartMinimized;
        SwCloseTray.IsChecked = st.CloseToTray;
        SwMinTray.IsChecked = st.MinimizeToTray;
        SwEnableOnStart.IsChecked = st.EnableOnStart;
        SwAutoCover.IsChecked = st.AutoCoverNewAdapters;
        _loading = false;
    }

    private void AppSetting_Click(object sender, RoutedEventArgs e)
    {
        var st = A.Settings;
        st.StartMinimized = SwStartMin.IsChecked == true;
        st.CloseToTray = SwCloseTray.IsChecked == true;
        st.MinimizeToTray = SwMinTray.IsChecked == true;
        st.EnableOnStart = SwEnableOnStart.IsChecked == true;
        st.AutoCoverNewAdapters = SwAutoCover.IsChecked == true;
        try { Autostart.Set(SwAutostart.IsChecked == true); }
        catch (Exception ex) { ConfirmDialog.Info(this, "Could not change the startup setting", ex.Message); }
        A.SaveSettings();
    }

    private void SwWidget_Click(object sender, RoutedEventArgs e) => A.SetWidgetVisible(SwWidget.IsChecked == true);

    private void WidgetMode_Click(object sender, RoutedEventArgs e) =>
        A.SetWidgetMode(SwWidgetTop.IsChecked == true, SwWidgetTaskbar.IsChecked == true);

    private void CmbCorner_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbCorner.SelectedIndex < 0) return;
        A.MoveWidget((WidgetCorner)CmbCorner.SelectedIndex);
    }

    private async void SwPromptFree_Click(object sender, RoutedEventArgs e) => await A.SetPromptFreeAsync(SwPromptFree.IsChecked == true);
    private async void UpdateScript_Click(object sender, RoutedEventArgs e) => await A.SetPromptFreeAsync(true);

    // ---------------- window chrome and tray behaviour ----------------

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!A.Exiting)
        {
            e.Cancel = true;
            if (A.Settings.CloseToTray) Hide();
            else A.ExitApp();
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        // A maximized window with custom chrome extends past the screen edge by the resize border.
        Root.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Minimized && A.Settings.MinimizeToTray) Hide();
        base.OnStateChanged(e);
    }
}
