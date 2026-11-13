using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VpnKillSwitch;

public enum ProtectionState { Off, Protected, Blocking }

public sealed record AdapterInfo(string Name, string Description, bool Up);

public sealed class KillSwitchStatus
{
    public bool On { get; init; }
    public string? EnabledAt { get; init; }
    public IReadOnlyList<string> Servers { get; init; } = [];
    public IReadOnlyList<string> BlockedAdapters { get; init; } = [];
    /// <summary>Destinations reachable without the VPN.</summary>
    public IReadOnlyList<string> Exceptions { get; init; } = [];
    /// <summary>Connected non-VPN adapters that the current rules do not cover.</summary>
    public IReadOnlyList<string> Uncovered { get; init; } = [];
    public IReadOnlyList<AdapterInfo> VpnAdapters { get; init; } = [];
    public string? ReadError { get; init; }

    public bool VpnUp => VpnAdapters.Any(a => a.Up);
    public ProtectionState State => !On ? ProtectionState.Off : VpnUp ? ProtectionState.Protected : ProtectionState.Blocking;
}

/// <summary>Reads the kill-switch state without administrator rights and without starting PowerShell.</summary>
internal static class StatusReader
{
    private const string RuleGroup = "VPN Kill Switch";
    private static readonly Regex FilterLayer = new(@"-\d{4}$", RegexOptions.Compiled);

    public static KillSwitchStatus Read(string vpnPattern)
    {
        string? error = null;
        bool on = false;
        try { on = FirewallRulesPresent(); }
        catch (Exception ex) { error = "Could not read Windows Firewall rules: " + ex.Message; }

        string? enabledAt = null;
        var servers = new List<string>();
        var blocked = new List<string>();
        var exceptions = new List<string>();
        if (on) ReadState(ref enabledAt, servers, blocked, exceptions);

        Regex re;
        try { re = new Regex(vpnPattern, RegexOptions.IgnoreCase); }
        catch { re = new Regex(EngineConfig.DefaultPattern, RegexOptions.IgnoreCase); }

        var vpn = new List<AdapterInfo>();
        var uncovered = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Skip filter-driver layers such as "Wi-Fi-QoS Packet Scheduler-0000": not real adapters.
                if (FilterLayer.IsMatch(ni.Name)) continue;
                bool up = ni.OperationalStatus == OperationalStatus.Up;
                if (re.IsMatch(ni.Name) || re.IsMatch(ni.Description))
                {
                    vpn.Add(new AdapterInfo(ni.Name, ni.Description, up));
                    continue;
                }
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (on && up && blocked.Count > 0 && !blocked.Contains(ni.Name, StringComparer.OrdinalIgnoreCase)
                    && HasRoutableAddress(ni))
                    uncovered.Add(ni.Name);
            }
        }
        catch (Exception ex) { error ??= "Could not list network adapters: " + ex.Message; }

        return new KillSwitchStatus
        {
            On = on,
            EnabledAt = enabledAt,
            Servers = servers,
            BlockedAdapters = blocked,
            Exceptions = exceptions,
            Uncovered = uncovered,
            VpnAdapters = vpn,
            ReadError = error,
        };
    }

    // Windows also lists filter-driver layers and idle virtual adapters as interfaces. Only an
    // adapter that actually holds a non-link-local address can carry traffic past the rules.
    private static bool HasRoutableAddress(NetworkInterface ni)
    {
        try
        {
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.IsIPv6LinkLocal) continue;
                var b = ip.GetAddressBytes();
                if (b.Length == 4 && b[0] == 169 && b[1] == 254) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool FirewallRulesPresent()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                   ?? throw new InvalidOperationException("Windows Firewall API is not available.");
        dynamic policy = Activator.CreateInstance(type)!;
        foreach (dynamic rule in policy.Rules)
        {
            string? grouping = rule.Grouping;
            if (grouping == RuleGroup) return true;
        }
        return false;
    }

    private static void ReadState(ref string? enabledAt, List<string> servers, List<string> blocked, List<string> exceptions)
    {
        try
        {
            if (!File.Exists(Paths.StateFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.StateFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("EnabledAt", out var e) && e.ValueKind == JsonValueKind.String) enabledAt = e.GetString();
            ReadStrings(root, "Servers", servers);
            ReadStrings(root, "BlockedAdapters", blocked);
            ReadStrings(root, "Exceptions", exceptions);
        }
        catch { /* state file is informational only */ }
    }

    // PowerShell writes a one-element array as a bare string, so accept both.
    private static void ReadStrings(JsonElement root, string name, List<string> target)
    {
        if (!root.TryGetProperty(name, out var el)) return;
        if (el.ValueKind == JsonValueKind.String) target.Add(el.GetString()!);
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) target.Add(item.GetString()!);
    }
}
