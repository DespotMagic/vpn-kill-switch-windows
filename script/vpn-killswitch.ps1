<#
.SYNOPSIS
    VPN kill switch for Windows (built for Pritunl Client; works with any OpenVPN / WireGuard VPN).

.DESCRIPTION
    on      Blocks ALL traffic on every network adapter that is not the VPN adapter,
            except traffic to your VPN servers (so the VPN can still connect) and,
            optionally, your local network. Traffic inside the VPN tunnel is untouched.
            If the VPN drops, nothing can reach the internet until it reconnects.
            The rules survive reboots until you turn the kill switch off.

    off     Removes all kill-switch rules and restores normal networking.

    status  Shows whether the kill switch is on.

    register    Optional. Copies this script to "Program Files\VpnKillSwitch" (writable by
                administrators only) and creates two scheduled tasks, \VpnKillSwitch\On and
                \VpnKillSwitch\Off, that run it with administrator rights. The tray app uses
                them to switch without a UAC prompt each time.

    unregister  Removes those scheduled tasks and the installed copy.

    It uses Windows Firewall BLOCK rules, which win over every other allow rule on the
    system, so no other program can slip traffic past it.

.EXAMPLE
    .\vpn-killswitch.ps1 on
    .\vpn-killswitch.ps1 off
    .\vpn-killswitch.ps1 status
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('on', 'off', 'status', 'register', 'unregister')]
    [string]$Action = 'status',

    [switch]$Pause   # wait for Enter before closing (used when the script re-launches itself as admin)
)

# ============================== SETTINGS ==============================
# These are the defaults. If %ProgramData%\VpnKillSwitch\config.json exists (the tray app
# writes it), the values in that file win.

# Extra VPN servers (hostnames or IP addresses). Servers in your Pritunl profiles are
# found automatically; add anything here that is missing.
#   Example: $ExtraVpnServers = @('vpn.mycompany.com', '203.0.113.10')
$ExtraVpnServers = @()

# Destinations that stay reachable WITHOUT the VPN: host names, IP addresses or CIDR ranges.
# Host names are resolved when the kill switch is turned on and pinned in the hosts file.
# Wildcards (*.example.com) are not possible: Windows Firewall filters by IP address only.
#   Example: $AllowedDestinations = @('intranet.mycompany.com', '198.51.100.0/24')
$AllowedDestinations = @()

# Keep access to your local network (router page, printer, NAS) while the kill switch is on.
$AllowLan = $true

# Block DNS queries to your router / local DNS while the kill switch is on, so name lookups
# can't leak outside the VPN. VPN server names are pinned in the hosts file instead.
$BlockLanDns = $true

# How to recognise VPN adapters (regex, matched against adapter name and description).
$VpnAdapterPattern = 'TAP-Windows|TAP-Pritunl|Wintun|WireGuard|OpenVPN|Pritunl'

# ======================================================================

$ErrorActionPreference = 'Stop'
$RuleGroup   = 'VPN Kill Switch'
$StateDir    = Join-Path $env:ProgramData 'VpnKillSwitch'
$StateFile   = Join-Path $StateDir 'state.json'
$ConfigFile  = Join-Path $StateDir 'config.json'
$LastRunFile = Join-Path $StateDir 'last-run.json'
$InstallDir  = Join-Path $env:ProgramFiles 'VpnKillSwitch'
$TaskPath    = '\VpnKillSwitch\'
$HostsFile   = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$HostsBegin  = '# >>> vpn-killswitch (managed automatically, removed when the kill switch is off)'
$HostsEnd    = '# <<< vpn-killswitch'
# Latin-1 maps every byte to one character and back, so the rest of the hosts file
# (comments in any language, any encoding) is written back byte for byte.
$HostsEncoding = [Text.Encoding]::GetEncoding(28591)
# Full path, so an elevated run can never pick up another powershell.exe from PATH.
$PowerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# ---------- Re-launch as administrator if needed ----------
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", $Action, '-Pause')
    Start-Process -FilePath $PowerShellExe -Verb RunAs -ArgumentList $argList
    exit
}

Add-Type -AssemblyName System.Numerics
$BI = [System.Numerics.BigInteger]

function Write-Info([string]$msg) { Write-Host $msg -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host $msg -ForegroundColor Green }
function Write-Warn2([string]$msg){ Write-Host $msg -ForegroundColor Yellow }

# ---------- Optional config file (overrides the SETTINGS block above) ----------
if (Test-Path $ConfigFile) {
    try {
        $cfg = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $cfg.ExtraVpnServers) { $ExtraVpnServers = @($cfg.ExtraVpnServers) }
        if ($null -ne $cfg.AllowedDestinations) { $AllowedDestinations = @($cfg.AllowedDestinations) }
        if ($null -ne $cfg.AllowLan)        { $AllowLan = [bool]$cfg.AllowLan }
        if ($null -ne $cfg.BlockLanDns)     { $BlockLanDns = [bool]$cfg.BlockLanDns }
        if ($cfg.VpnAdapterPattern)         { $VpnAdapterPattern = [string]$cfg.VpnAdapterPattern }
    } catch {
        Write-Warn2 "Could not read $ConfigFile - using the defaults in the script."
    }
}
try { [void][regex]::new($VpnAdapterPattern) }
catch {
    Write-Warn2 "The VPN adapter pattern '$VpnAdapterPattern' is not a valid regular expression - using the default."
    $VpnAdapterPattern = 'TAP-Windows|TAP-Pritunl|Wintun|WireGuard|OpenVPN|Pritunl'
}

# ---------- IP range arithmetic (IPv4 + IPv6) ----------
function ConvertTo-BigInt([byte[]]$bytes) {
    # big-endian bytes -> unsigned BigInteger
    $le = New-Object byte[] ($bytes.Length + 1)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $le[$i] = $bytes[$bytes.Length - 1 - $i] }
    return New-Object System.Numerics.BigInteger (, $le)
}

function ConvertFrom-BigInt($n, [int]$len) {
    $le = $n.ToByteArray()
    $be = New-Object byte[] $len
    for ($i = 0; $i -lt $len; $i++) {
        if ($i -lt $le.Length) { $be[$len - 1 - $i] = $le[$i] }
    }
    return (New-Object System.Net.IPAddress (, $be)).ToString()
}

function ConvertTo-IpRange([string]$cidr) {
    $parts  = $cidr.Split('/')
    $ip     = [System.Net.IPAddress]::Parse($parts[0])
    $bytes  = $ip.GetAddressBytes()
    $bits   = $bytes.Length * 8
    $prefix = if ($parts.Count -gt 1) { [int]$parts[1] } else { $bits }
    $size   = $BI::Pow([System.Numerics.BigInteger]2, $bits - $prefix)
    $n      = ConvertTo-BigInt $bytes
    $start  = $BI::Multiply($BI::Divide($n, $size), $size)
    $end    = $BI::Subtract($BI::Add($start, $size), $BI::One)
    return [pscustomobject]@{ V6 = ($bytes.Length -eq 16); Start = $start; End = $end }
}

function Test-Cidr([string]$cidr) {
    $p = $cidr.Split('/')
    $ip = $null
    if ($p.Count -ne 2 -or -not [System.Net.IPAddress]::TryParse($p[0], [ref]$ip)) { return $false }
    $n = 0
    if (-not [int]::TryParse($p[1], [ref]$n)) { return $false }
    # /0 would allow the whole internet and switch the protection off in all but name.
    return ($n -ge 1 -and $n -le $ip.GetAddressBytes().Length * 8)
}

# Everything EXCEPT the given ranges, as firewall address ranges ("a-b").
function Get-Complement($ranges, [bool]$v6) {
    $len = if ($v6) { 16 } else { 4 }
    $max = $BI::Subtract($BI::Pow([System.Numerics.BigInteger]2, $len * 8), $BI::One)
    $cur = $BI::Zero
    $out = New-Object System.Collections.Generic.List[string]
    $sorted = @($ranges | Where-Object { $_.V6 -eq $v6 } | Sort-Object -Property Start)
    foreach ($r in $sorted) {
        if ($BI::Compare($r.Start, $cur) -gt 0) {
            $a = ConvertFrom-BigInt $cur $len
            $b = ConvertFrom-BigInt ($BI::Subtract($r.Start, $BI::One)) $len
            $out.Add($(if ($a -eq $b) { $a } else { "$a-$b" }))
        }
        $next = $BI::Add($r.End, $BI::One)
        if ($BI::Compare($next, $cur) -gt 0) { $cur = $next }
    }
    if ($BI::Compare($cur, $max) -le 0) {
        $a = ConvertFrom-BigInt $cur $len
        $b = ConvertFrom-BigInt $max $len
        $out.Add($(if ($a -eq $b) { $a } else { "$a-$b" }))
    }
    return $out.ToArray()
}

# ---------- Find VPN servers ----------
function Get-VpnServerNames {
    $names = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($s in $ExtraVpnServers) { if ($s) { [void]$names.Add($s.Trim()) } }

    $dirs = @()
    $dirs += @(Resolve-Path (Join-Path $env:SystemDrive 'Users\*\AppData\Roaming\pritunl') -ErrorAction SilentlyContinue | ForEach-Object { $_.Path })
    $dirs += @((Join-Path $env:ProgramData 'Pritunl'), (Join-Path $env:APPDATA 'pritunl')) | Where-Object { Test-Path $_ }

    $files = @($dirs | Select-Object -Unique | ForEach-Object {
        Get-ChildItem -Path $_ -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.ovpn', '.conf' }
    })

    foreach ($f in $files) {
        try {
            $text = [IO.File]::ReadAllText($f.FullName)
        } catch { continue }

        # OpenVPN "remote <host> [port] [proto]" lines (also inside JSON strings)
        foreach ($m in [regex]::Matches($text, '(?m)(?:^|\\n)\s*remote\s+([^\s\\"]+)')) {
            [void]$names.Add($m.Groups[1].Value)
        }
        # Pritunl profile .conf (JSON): sync_hosts / server hosts used to update the profile
        if ($f.Extension -eq '.conf') {
            try {
                $j = $text | ConvertFrom-Json
                foreach ($u in @($j.sync_hosts)) {
                    if ($u) { try { [void]$names.Add(([uri]$u).Host) } catch {} }
                }
            } catch {}
        }
    }
    return @($names)
}

function Resolve-Servers([string[]]$names) {
    $result = @()
    foreach ($n in $names) {
        $ip = $null
        if ([System.Net.IPAddress]::TryParse($n, [ref]$ip)) {
            $result += [pscustomobject]@{ Name = $n; Ip = $ip.ToString(); IsHostname = $false }
            continue
        }
        # Names come from profile and config files and end up in the hosts file: accept plain host names only.
        if ($n -notmatch '^[A-Za-z0-9]([A-Za-z0-9._-]{0,251}[A-Za-z0-9])?$') {
            Write-Warn2 "  '$n' is not a valid host name (skipped)."
            continue
        }
        try {
            foreach ($a in [System.Net.Dns]::GetHostAddresses($n)) {
                if ($a.AddressFamily -in 'InterNetwork', 'InterNetworkV6') {
                    $result += [pscustomobject]@{ Name = $n; Ip = $a.ToString(); IsHostname = $true }
                }
            }
        } catch {
            Write-Warn2 "  Could not resolve '$n' (skipped)."
        }
    }
    return $result
}

# ---------- hosts file pinning ----------
# Replaces the managed block in the hosts file with pins for the given servers
# ($null or an empty list just removes the block). Everything else is left untouched.
function Set-HostsPins($servers) {
    $old = @()
    if (Test-Path $HostsFile) { $old = @([IO.File]::ReadAllLines($HostsFile, $HostsEncoding)) }

    $new = New-Object System.Collections.Generic.List[string]
    $inside = $false
    foreach ($l in $old) {
        if ($l -eq $HostsBegin) { $inside = $true; continue }
        if ($l -eq $HostsEnd)   { $inside = $false; continue }
        if (-not $inside) { $new.Add($l) }
    }

    $pins = @($servers | Where-Object { $_ -and $_.IsHostname } |
        ForEach-Object { "$($_.Ip) $($_.Name)" } | Select-Object -Unique)
    if ($pins.Count -gt 0) {
        $new.Add($HostsBegin)
        foreach ($p in $pins) { $new.Add($p) }
        $new.Add($HostsEnd)
    }

    if (($new -join "`n") -ne ($old -join "`n")) {
        [IO.File]::WriteAllLines($HostsFile, $new.ToArray(), $HostsEncoding)
    }
}

# ---------- State ----------
function Save-State($obj) {
    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir | Out-Null }
    $obj | ConvertTo-Json -Depth 5 | Set-Content -Path $StateFile -Encoding UTF8
}
function Read-State {
    if (Test-Path $StateFile) { try { return Get-Content $StateFile -Raw | ConvertFrom-Json } catch {} }
    return $null
}

function Get-Adapters {
    $all = @(Get-NetAdapter -ErrorAction SilentlyContinue)
    $vpn = @($all | Where-Object { $_.Name -match $VpnAdapterPattern -or $_.InterfaceDescription -match $VpnAdapterPattern })
    $other = @($all | Where-Object { $vpn.Name -notcontains $_.Name })
    return [pscustomobject]@{ Vpn = $vpn; Other = $other }
}

# ---------- OFF ----------
function Disable-KillSwitch([switch]$Quiet) {
    Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    try { Set-HostsPins $null }
    catch {
        Write-Warn2 "Could not clean the hosts file ($($_.Exception.Message))."
        Write-Warn2 "Remove the 'vpn-killswitch' block from $HostsFile by hand, or the VPN may fail to connect after its server address changes."
    }
    $state = Read-State
    if ($state -and $state.ProfilesWeEnabled) {
        Set-NetFirewallProfile -Name @($state.ProfilesWeEnabled) -Enabled False
    }
    if (Test-Path $StateFile) { Remove-Item $StateFile -Force }
    Clear-DnsClientCache -ErrorAction SilentlyContinue
    if (-not $Quiet) { Write-Ok 'Kill switch is OFF. Normal networking restored.' }
}

# ---------- ON ----------
function Enable-KillSwitch {
    # Existing rules stay until the new ones are in place, so running "on" again
    # (to cover a new adapter, or after changing settings) never opens a gap.
    $script:EnableStarted = $true
    $prev = Read-State
    $oldRules = @(Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue)
    $script:OldRuleNames = @($oldRules | ForEach-Object { $_.Name })
    $profilesWeEnabled = @()
    if ($prev -and $prev.ProfilesWeEnabled) { $profilesWeEnabled = @($prev.ProfilesWeEnabled) }

    Write-Info 'Looking for VPN servers in Pritunl profiles...'
    $names = Get-VpnServerNames
    if ($names.Count -eq 0) {
        throw "No VPN servers found. Add them to `$ExtraVpnServers at the top of this script."
    }
    $servers = @(Resolve-Servers $names)
    if ($servers.Count -eq 0) {
        throw 'None of the VPN servers could be resolved. Check your internet connection and try again.'
    }
    $servers | Sort-Object Name, Ip -Unique | ForEach-Object { Write-Host "  $($_.Name) -> $($_.Ip)" }

    # Destinations the user chose to keep reachable without the VPN.
    $excHosts  = @()   # resolved host names / single addresses
    $excCidrs  = @()   # address ranges
    $excNames  = @()
    foreach ($d in $AllowedDestinations) {
        $d = "$d".Trim()
        if (-not $d) { continue }
        if ($d.Contains('/')) {
            if (Test-Cidr $d) { $excCidrs += $d } else { Write-Warn2 "  '$d' is not a valid address range (skipped)." }
        } else { $excNames += $d }
    }
    if ($excNames.Count -gt 0) { $excHosts = @(Resolve-Servers $excNames) }
    if ($excHosts.Count + $excCidrs.Count -gt 0) {
        Write-Info 'Allowed without VPN:'
        $excHosts | Sort-Object Name, Ip -Unique | ForEach-Object {
            if ($_.IsHostname) { Write-Host "  $($_.Name) -> $($_.Ip)" } else { Write-Host "  $($_.Ip)" }
        }
        $excCidrs | ForEach-Object { Write-Host "  $_" }
    }

    $ad = Get-Adapters
    if ($ad.Other.Count -eq 0) { throw 'No network adapters found to protect.' }
    Write-Info 'VPN adapters (traffic allowed):'
    if ($ad.Vpn.Count -eq 0) { Write-Host '  (none yet - will be allowed automatically when the VPN creates it)' }
    $ad.Vpn   | ForEach-Object { Write-Host "  $($_.Name)  [$($_.InterfaceDescription)]" }
    $lanNote = if ($AllowLan) { ' and local network' } else { '' }
    Write-Info "Other adapters (blocked except VPN servers$lanNote):"
    $ad.Other | ForEach-Object { Write-Host "  $($_.Name)  [$($_.InterfaceDescription)]" }

    # On PCs managed by an organisation, group policy can make Windows Firewall ignore
    # locally created rules. The kill switch would then look on while blocking nothing.
    $locked = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Where-Object { "$($_.AllowLocalFirewallRules)" -eq 'False' })
    if ($locked.Count -gt 0) {
        throw "Windows Firewall on this PC is managed by policy and ignores locally created rules (profiles: $($locked.Name -join ', ')). The kill switch cannot work here."
    }

    # Windows Firewall must be on for the rules to work.
    $off = @(Get-NetFirewallProfile | Where-Object { "$($_.Enabled)" -ne 'True' })
    if ($off.Count -gt 0) {
        Write-Warn2 "Windows Firewall was off for: $($off.Name -join ', '). Turning it on (restored when the kill switch is turned off)."
        Set-NetFirewallProfile -Name $off.Name -Enabled True
        $profilesWeEnabled += $off.Name
    }

    # Addresses that stay reachable outside the tunnel.
    $allowed = @()
    foreach ($s in $servers)  { $allowed += ConvertTo-IpRange $s.Ip }
    foreach ($s in $excHosts) { $allowed += ConvertTo-IpRange $s.Ip }
    foreach ($c in $excCidrs) { $allowed += ConvertTo-IpRange $c }
    $allowed += ConvertTo-IpRange '255.255.255.255'          # DHCP, so you can still join networks
    if ($AllowLan) {
        foreach ($c in '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16', '224.0.0.0/4',
                       'fe80::/10', 'fc00::/7', 'ff00::/8') {
            $allowed += ConvertTo-IpRange $c
        }
    }
    $blockRemote = @(Get-Complement $allowed $false) + @(Get-Complement $allowed $true)
    $ifaces = @($ad.Other | ForEach-Object { $_.Name })

    $common = @{
        Group         = $RuleGroup
        Action        = 'Block'
        Profile       = 'Any'
        # -InterfaceAlias takes wildcard patterns; escape names that contain * ? [ ]
        InterfaceAlias = @($ifaces | ForEach-Object { [System.Management.Automation.WildcardPattern]::Escape($_) })
        Enabled       = 'True'
    }
    $created = @()
    $created += New-NetFirewallRule @common -DisplayName 'VPN Kill Switch - block outbound outside VPN' -Direction Outbound -RemoteAddress $blockRemote
    $created += New-NetFirewallRule @common -DisplayName 'VPN Kill Switch - block inbound outside VPN'  -Direction Inbound  -RemoteAddress $blockRemote
    if ($AllowLan -and $BlockLanDns) {
        $created += New-NetFirewallRule @common -DisplayName 'VPN Kill Switch - block DNS outside VPN (UDP)' -Direction Outbound -Protocol UDP -RemotePort 53, 853
        $created += New-NetFirewallRule @common -DisplayName 'VPN Kill Switch - block DNS outside VPN (TCP)' -Direction Outbound -Protocol TCP -RemotePort 53, 853
    }

    # Make sure Windows Firewall is really enforcing the new rules, not just storing them.
    $queryErr = $null
    $active = @(Get-NetFirewallRule -PolicyStore ActiveStore -Group $RuleGroup -ErrorAction SilentlyContinue -ErrorVariable queryErr)
    $queryFailed = $queryErr -and ($queryErr[0].CategoryInfo.Category -ne 'ObjectNotFound')
    if (-not $queryFailed) {
        $missing = @($created | Where-Object { $active.Name -notcontains $_.Name })
        if ($missing.Count -gt 0) {
            throw 'Windows Firewall stored the rules but is not enforcing them (another firewall product or a policy is in control). The kill switch cannot work here.'
        }
    }

    # New rules are active: refresh the hosts pins, then drop the previous rules.
    Set-HostsPins (@($servers) + @($excHosts))
    if ($oldRules.Count -gt 0) { $oldRules | Remove-NetFirewallRule }
    $script:Committed = $true   # from here on the new rules are the only ones: never roll back

    Clear-DnsClientCache -ErrorAction SilentlyContinue

    Save-State ([pscustomobject]@{
        EnabledAt         = (Get-Date).ToString('s')
        Servers           = @($servers | ForEach-Object { "$($_.Name) -> $($_.Ip)" })
        Exceptions        = @(@($excHosts | ForEach-Object { if ($_.IsHostname) { "$($_.Name) -> $($_.Ip)" } else { $_.Ip } }) + @($excCidrs))
        BlockedAdapters   = $ifaces
        ProfilesWeEnabled = @($profilesWeEnabled | Select-Object -Unique)
    })

    Write-Ok ''
    Write-Ok 'Kill switch is ON. Internet works only through the VPN.'
    Write-Host 'If you plug in a NEW network adapter (USB Wi-Fi, phone tethering), run "on" again so it is covered.'
}

# ---------- STATUS ----------
function Show-Status {
    $rules = @(Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue)
    $state = Read-State
    if ($rules.Count -eq 0) {
        Write-Warn2 'Kill switch is OFF.'
    } else {
        Write-Ok "Kill switch is ON (since $($state.EnabledAt))."
        Write-Info 'Allowed VPN servers:'
        @($state.Servers) | ForEach-Object { Write-Host "  $_" }
        if (@($state.Exceptions).Count -gt 0 -and $state.Exceptions) {
            Write-Info 'Allowed without VPN:'
            @($state.Exceptions) | ForEach-Object { Write-Host "  $_" }
        }
        Write-Info 'Blocked adapters:'
        @($state.BlockedAdapters) | ForEach-Object { Write-Host "  $_" }
        $ad = Get-Adapters
        $uncovered = @($ad.Other | Where-Object { @($state.BlockedAdapters) -notcontains $_.Name })
        if ($uncovered.Count -gt 0) {
            Write-Warn2 "WARNING: new adapter(s) not covered: $($uncovered.Name -join ', '). Run 'on' again."
        }
    }
    $ad = Get-Adapters
    Write-Info 'VPN adapters right now:'
    if ($ad.Vpn.Count -eq 0) { Write-Host '  (none found)' }
    $ad.Vpn | ForEach-Object { Write-Host "  $($_.Name): $($_.Status)" }
}

# ---------- Scheduled tasks for switching without a UAC prompt ----------
function Register-Tasks {
    if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
    $target = Join-Path $InstallDir 'vpn-killswitch.ps1'
    if ($PSCommandPath -ne $target) { Copy-Item -LiteralPath $PSCommandPath -Destination $target -Force }

    $user          = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $taskSettings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
    foreach ($a in 'On', 'Off') {
        $psArgs = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$target`" $($a.ToLower())"
        $taskAction = New-ScheduledTaskAction -Execute $PowerShellExe -Argument $psArgs
        Register-ScheduledTask -TaskPath $TaskPath -TaskName $a -Action $taskAction `
            -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null
    }
    Write-Ok "Scheduled tasks created. Script installed to $target"
}

function Unregister-Tasks {
    foreach ($a in 'On', 'Off') {
        Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $a -Confirm:$false -ErrorAction SilentlyContinue
    }
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $svc.GetFolder('\').DeleteFolder($TaskPath.Trim('\'), 0)
    } catch {}
    if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Write-Ok 'Scheduled tasks removed.'
}

# ---------- Main ----------
$exitCode = 0
$errorMessage = $null
$mutex = $null
$ownsMutex = $false
try {
    # One switch operation at a time (the tray app, a scheduled task and a console can overlap).
    if ($Action -in 'on', 'off') {
        $mutex = New-Object System.Threading.Mutex($false, 'Global\VpnKillSwitchScript')
        try { $ownsMutex = $mutex.WaitOne(90000) }
        catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw 'Another kill-switch operation is still running. Try again in a moment.' }
    }
    switch ($Action) {
        'on'         { Enable-KillSwitch }
        'off'        { Disable-KillSwitch }
        'status'     { Show-Status }
        'register'   { Register-Tasks }
        'unregister' { Unregister-Tasks }
    }
} catch {
    $errorMessage = $_.Exception.Message
    Write-Host "ERROR: $errorMessage" -ForegroundColor Red
    if ($Action -eq 'on' -and $script:Committed) {
        # The new rules are in place and the old ones are gone; only bookkeeping failed.
        Write-Warn2 'The kill switch IS on, but its state could not be saved. Run "on" again.'
    } elseif ($Action -eq 'on' -and $script:EnableStarted) {
        if ($script:OldRuleNames -and $script:OldRuleNames.Count -gt 0) {
            # The kill switch was already on: keep the previous rules, drop any half-made new ones.
            Write-Warn2 'Keeping the previous kill-switch rules in place.'
            try {
                Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue |
                    Where-Object { $script:OldRuleNames -notcontains $_.Name } | Remove-NetFirewallRule
            } catch {}
        } else {
            Write-Warn2 'Rolling back so you are not left without internet...'
            try { Disable-KillSwitch -Quiet } catch {}
        }
    }
    $exitCode = 1
} finally {
    if ($mutex) {
        if ($ownsMutex) { try { $mutex.ReleaseMutex() } catch {} }
        $mutex.Dispose()
    }
}

# Report the outcome for the tray app (it cannot read this window's output).
if ($Action -ne 'status') {
    try {
        if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir | Out-Null }
        [pscustomobject]@{
            Action     = $Action
            Success    = ($exitCode -eq 0)
            Error      = $errorMessage
            FinishedAt = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json | Set-Content -Path $LastRunFile -Encoding UTF8
    } catch {}
}

if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
if ($exitCode) { exit $exitCode }
