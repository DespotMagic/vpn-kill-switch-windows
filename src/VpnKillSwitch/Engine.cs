using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace VpnKillSwitch;

public sealed record EngineResult(bool Success, string? Error = null, bool Cancelled = false);

/// <summary>Runs the PowerShell script with administrator rights and waits for its result.</summary>
internal static class Engine
{
    private const string TaskOn = @"\VpnKillSwitch\On";
    private const string TaskOff = @"\VpnKillSwitch\Off";

    /// <summary>True when the scheduled tasks for prompt-free switching exist.</summary>
    public static bool TasksRegistered() =>
        File.Exists(Paths.InstalledScript)
        && RunHidden("schtasks.exe", $"/query /tn \"{TaskOn}\"") == 0
        && RunHidden("schtasks.exe", $"/query /tn \"{TaskOff}\"") == 0;

    /// <summary>True when the copy in Program Files differs from the script shipped with the app.</summary>
    public static bool InstalledScriptOutdated()
    {
        try
        {
            if (!File.Exists(Paths.InstalledScript) || !File.Exists(Paths.BundledScript)) return false;
            return !Hash(Paths.InstalledScript).SequenceEqual(Hash(Paths.BundledScript));
        }
        catch { return false; }
    }

    private static byte[] Hash(string path)
    {
        using var s = File.OpenRead(path);
        return SHA256.HashData(s);
    }

    /// <param name="action">on, off, register or unregister</param>
    /// <param name="useTasks">run on/off through the scheduled tasks (no UAC prompt)</param>
    public static async Task<EngineResult> RunAsync(string action, bool useTasks)
    {
        var started = DateTime.UtcNow.AddSeconds(-2);
        Process? proc = null;
        try
        {
            if (useTasks && action is "on" or "off")
            {
                var task = action == "on" ? TaskOn : TaskOff;
                int code = await Task.Run(() => RunHidden("schtasks.exe", $"/run /tn \"{task}\""));
                if (code != 0) return new(false, $"Could not start the scheduled task {task} (exit code {code}).");
            }
            else
            {
                if (!File.Exists(Paths.BundledScript))
                    return new(false, "vpn-killswitch.ps1 was not found next to the program.");
                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass " +
                                $"-File \"{Paths.BundledScript}\" {action}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, null, Cancelled: true);   // UAC prompt declined
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }

        // The script writes last-run.json when it finishes.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        DateTime? exitedAt = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            var result = TryReadLastRun(action, started);
            if (result != null) return result;
            if (proc is { HasExited: true })
            {
                exitedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - exitedAt > TimeSpan.FromSeconds(3))
                    return new(false, "The script finished without reporting a result.");
            }
        }
        return new(false, "Timed out waiting for the script to finish.");
    }

    private static EngineResult? TryReadLastRun(string action, DateTime notBeforeUtc)
    {
        try
        {
            var fi = new FileInfo(Paths.LastRunFile);
            if (!fi.Exists || fi.LastWriteTimeUtc < notBeforeUtc) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(fi.FullName));
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("Action").GetString(), action, StringComparison.OrdinalIgnoreCase)) return null;
            bool ok = root.GetProperty("Success").GetBoolean();
            string? err = root.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return new(ok, ok ? null : err ?? "The script reported an error.");
        }
        catch { return null; }   // file still being written
    }

    private static int RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return -1; }
    }
}

/// <summary>"Start with Windows" for the tray app (per-user Run key, no administrator rights needed).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VpnKillSwitch";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
