using System.Text.Json;
using System.Text.Json.Serialization;

namespace VpnKillSwitch;

internal static class Paths
{
    public static string ProgramDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnKillSwitch");
    public static string StateFile => Path.Combine(ProgramDataDir, "state.json");
    public static string ConfigFile => Path.Combine(ProgramDataDir, "config.json");
    public static string LastRunFile => Path.Combine(ProgramDataDir, "last-run.json");

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpnKillSwitch");
    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    public static string BundledScript => Path.Combine(AppContext.BaseDirectory, "vpn-killswitch.ps1");
    public static string InstalledScript =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VpnKillSwitch", "vpn-killswitch.ps1");
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum WidgetCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Per-user preferences of the tray app (%AppData%\VpnKillSwitch\settings.json).</summary>
public sealed class AppSettings
{
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool EnableOnStart { get; set; }
    public bool AutoCoverNewAdapters { get; set; } = true;
    public bool ShowWidget { get; set; } = true;
    public bool WidgetAlwaysOnTop { get; set; } = true;
    public bool WidgetInTaskbar { get; set; }
    public WidgetCorner WidgetCorner { get; set; } = WidgetCorner.BottomRight;
    public string? WidgetScreen { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json.Options) ?? new();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.AppDataDir);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json.Options));
        }
        catch { /* preferences are not critical */ }
    }
}

/// <summary>Options read by the script (%ProgramData%\VpnKillSwitch\config.json).</summary>
public sealed class EngineConfig
{
    public const string DefaultPattern = "TAP-Windows|TAP-Pritunl|Wintun|WireGuard|OpenVPN|Pritunl";

    public string[] ExtraVpnServers { get; set; } = [];
    /// <summary>Host names, IP addresses or CIDR ranges reachable without the VPN.</summary>
    public string[] AllowedDestinations { get; set; } = [];
    public bool AllowLan { get; set; } = true;
    public bool BlockLanDns { get; set; } = true;
    public string VpnAdapterPattern { get; set; } = DefaultPattern;

    public static EngineConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var c = JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(Paths.ConfigFile), Json.Options) ?? new();
                if (string.IsNullOrWhiteSpace(c.VpnAdapterPattern)) c.VpnAdapterPattern = DefaultPattern;
                c.ExtraVpnServers ??= [];
                c.AllowedDestinations ??= [];
                return c;
            }
        }
        catch { /* fall back to defaults */ }
        return new();
    }

    /// <summary>Throws if the file cannot be written.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Paths.ProgramDataDir);
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Json.Options));
    }
}
