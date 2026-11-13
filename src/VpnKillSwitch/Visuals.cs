using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace VpnKillSwitch;

public enum VisualState { Off, Protected, Blocking, Attention, Busy }

/// <summary>Texts and colours for one state, shared by the window, the widget and the tray.</summary>
public sealed record StateView(
    VisualState Visual,
    string Title, string Description,       // main window
    string WidgetTitle, string WidgetSub,   // widget and tray
    Color Main, Color Tint, Color Border,   // main window banner
    Color Dot)                              // widget dot
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static StateView For(KillSwitchStatus s, bool busy, bool allowLan = true)
    {
        if (busy)
            return new(VisualState.Busy, "Working...", "Applying firewall rules.",
                "Working...", "Applying firewall rules",
                C(0x005FB8), C(0xEFF4F9), C(0xD3E1F0), C(0x3B82F6));

        if (s.On && s.Uncovered.Count > 0)
            return new(VisualState.Attention, "New adapter not covered",
                $"\"{string.Join("\", \"", s.Uncovered)}\" appeared after the rules were applied and can bypass the VPN.",
                s.VpnUp ? "Protected" : "Blocking traffic", "New adapter not covered",
                C(0x9D5D00), C(0xFFF4CE), C(0xF2D9A6), C(0xF0A020));

        return s.State switch
        {
            ProtectionState.Protected => new(VisualState.Protected, "Protected",
                "Apps can reach the internet only through the VPN tunnel. If the VPN drops, traffic stops until it reconnects.",
                "Protected", "Kill switch ON - VPN connected",
                C(0x0F7B0F), C(0xDFF6DD), C(0xE5E5E5), C(0x2ECC71)),
            ProtectionState.Blocking => new(VisualState.Blocking, "Internet blocked - VPN is down",
                "Nothing leaves this PC until the VPN reconnects." + (allowLan ? " Local network is still reachable." : ""),
                "Blocking traffic", "Kill switch ON - VPN is down",
                C(0x9D5D00), C(0xFFF4CE), C(0xF2D9A6), C(0xF0A020)),
            _ => new(VisualState.Off, "Not protected",
                "Kill switch is off. If the VPN drops, apps fall back to your normal connection.",
                "Kill switch OFF", "Traffic is not restricted",
                C(0xC42B1C), C(0xFDE7E9), C(0xF1C4C7), C(0x8A8F99)),
        };
    }
}

/// <summary>A stroked icon drawn from a 24x24 path, scaled to IconSize.</summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(VectorIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(VectorIcon),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    protected override void OnRender(DrawingContext dc)
    {
        if (Data == null) return;
        var pen = new Pen(Stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new ScaleTransform(IconSize / 24, IconSize / 24));
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}

/// <summary>
/// The app icon, drawn at run time: a tunnel arch on a coloured tile.
/// Protected = traffic dot inside the tunnel, Blocking = bar across it, Off = empty grey arch.
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<(VisualState, int), Drawing.Icon> Cache = new();

    private static Drawing.Color TileColor(VisualState v) => v switch
    {
        VisualState.Protected => Drawing.Color.FromArgb(0x0F, 0x7B, 0x0F),
        VisualState.Blocking or VisualState.Attention => Drawing.Color.FromArgb(0xD9, 0x8A, 0x00),
        VisualState.Busy => Drawing.Color.FromArgb(0x00, 0x5F, 0xB8),
        _ => Drawing.Color.FromArgb(0x8A, 0x8F, 0x99),
    };

    public static Drawing.Icon Get(VisualState state, int size = 32)
    {
        if (!Cache.TryGetValue((state, size), out var icon))
        {
            using var bmp = Draw(state, size);
            Cache[(state, size)] = icon = Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        return icon;
    }

    public static ImageSource WindowIcon =>
        Imaging.CreateBitmapSourceFromHIcon(Get(VisualState.Protected, 48).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    private static Drawing.Bitmap Draw(VisualState state, int size)
    {
        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(Drawing.Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);   // everything below is on a 32x32 grid

        using (var tile = RoundedRect(1, 1, 30, 30, 7.5f))
        using (var fill = new Drawing.SolidBrush(TileColor(state)))
            g.FillPath(fill, tile);

        using var pen = new Drawing.Pen(Drawing.Color.White, 3.6f)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round,
        };
        using (var arch = new Drawing2D.GraphicsPath())
        {
            arch.AddLine(8.5f, 25f, 8.5f, 15.5f);
            arch.AddArc(8.5f, 8f, 15f, 15f, 180, 180);
            arch.AddLine(23.5f, 15.5f, 23.5f, 25f);
            g.DrawPath(pen, arch);
        }

        if (state == VisualState.Protected)
            g.FillEllipse(Drawing.Brushes.White, 13.2f, 17.6f, 5.6f, 5.6f);
        else if (state is VisualState.Blocking or VisualState.Attention)
            g.DrawLine(pen, 13f, 20.5f, 19f, 20.5f);
        return bmp;
    }

    private static Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new Drawing2D.GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Writes a multi-size .ico (used once to produce app.ico for the exe).</summary>
    public static void ExportIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = sizes.Select(s =>
        {
            using var bmp = Draw(VisualState.Protected, s);
            using var ms = new MemoryStream();
            bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }
}
