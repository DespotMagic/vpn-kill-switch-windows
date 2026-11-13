<#
.SYNOPSIS
    Tests the pure logic of vpn-killswitch.ps1 (address arithmetic, input validation, hosts-file editing).

.DESCRIPTION
    Safe to run: it does not need administrator rights and does not touch Windows Firewall,
    the real hosts file or scheduled tasks. The functions are loaded from the script's syntax
    tree, so the script itself is never executed.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tests\Test-ScriptLogic.ps1
#>
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '..\script\vpn-killswitch.ps1'

# ---- load the functions without running the script ----
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Host $_ -ForegroundColor Red }; exit 1 }
$ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
    ForEach-Object { Invoke-Expression $_.Extent.Text }

Add-Type -AssemblyName System.Numerics
$BI = [System.Numerics.BigInteger]
$HostsEncoding = [Text.Encoding]::GetEncoding(28591)
$HostsBegin = '# >>> vpn-killswitch (managed automatically, removed when the kill switch is off)'
$HostsEnd   = '# <<< vpn-killswitch'
$HostsFile  = Join-Path ([IO.Path]::GetTempPath()) ("ks-hosts-test-{0}.txt" -f [guid]::NewGuid().ToString('N'))

$script:failed = 0
$script:passed = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "  ok    $name" -ForegroundColor Green }
    else     { $script:failed++; Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}
function Same($a, $b) { return (@($a) -join '|') -eq (@($b) -join '|') }

# ---- address ranges ----
Write-Host 'Address ranges'
$r = ConvertTo-IpRange '10.1.2.3/8'
Check 'CIDR is aligned to its network' ((ConvertFrom-BigInt $r.Start 4) -eq '10.0.0.0' -and (ConvertFrom-BigInt $r.End 4) -eq '10.255.255.255')
$r = ConvertTo-IpRange '8.8.8.8'
Check 'single address is a one-address range' ((ConvertFrom-BigInt $r.Start 4) -eq '8.8.8.8' -and (ConvertFrom-BigInt $r.End 4) -eq '8.8.8.8')
$r = ConvertTo-IpRange 'fe80::/10'
Check 'IPv6 CIDR' ((ConvertFrom-BigInt $r.Start 16) -eq 'fe80::' -and (ConvertFrom-BigInt $r.End 16) -eq 'febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff')

$allowed = @('10.0.0.0/8', '192.168.0.0/16', '8.8.8.8') | ForEach-Object { ConvertTo-IpRange $_ }
$c = Get-Complement $allowed $false
Check 'IPv4 complement' (Same $c @('0.0.0.0-8.8.8.7', '8.8.8.9-9.255.255.255', '11.0.0.0-192.167.255.255', '192.169.0.0-255.255.255.255')) ($c -join ' ')

$c = Get-Complement (@('0.0.0.0/8', '255.255.255.255', '10.0.0.0/8', '10.5.0.0/16', '11.0.0.0/8') | ForEach-Object { ConvertTo-IpRange $_ }) $false
Check 'edges, overlap and adjacency' (Same $c @('1.0.0.0-9.255.255.255', '12.0.0.0-255.255.255.254')) ($c -join ' ')

$c = Get-Complement (@('fe80::/10', 'fc00::/7', 'ff00::/8') | ForEach-Object { ConvertTo-IpRange $_ }) $true
Check 'IPv6 complement (local ranges allowed)' (Same $c @('::-fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff', 'fe00::-fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff', 'fec0::-feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')) ($c -join ' ')

$c = Get-Complement $allowed $true
Check 'IPv6 fully blocked when nothing IPv6 is allowed' (Same $c @('::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')) ($c -join ' ')

$c = Get-Complement (@('0.0.0.0/1', '128.0.0.0/1') | ForEach-Object { ConvertTo-IpRange $_ }) $false
Check 'nothing left to block when everything is allowed' (@($c).Count -eq 0)

# Property check: an address is blocked exactly when it is not allowed.
$real = @('203.0.113.7', '198.51.100.23', '255.255.255.255', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16', '224.0.0.0/4') |
    ForEach-Object { ConvertTo-IpRange $_ }
$blocked = Get-Complement $real $false | ForEach-Object {
    $p = $_.Split('-'); $a = ConvertTo-IpRange $p[0]; $b = ConvertTo-IpRange $p[$p.Count - 1]
    [pscustomobject]@{ Start = $a.Start; End = $b.End }
}
$rnd = New-Object System.Random 12345
$bad = 0
$samples = @('203.0.113.7', '203.0.113.6', '203.0.113.8', '9.255.255.255', '10.0.0.0', '11.0.0.0', '172.15.255.255', '172.32.0.0',
             '192.168.255.255', '192.169.0.0', '223.255.255.255', '224.0.0.0', '239.255.255.255', '240.0.0.0', '255.255.255.254', '255.255.255.255', '0.0.0.0')
1..3000 | ForEach-Object { $samples += '{0}.{1}.{2}.{3}' -f $rnd.Next(256), $rnd.Next(256), $rnd.Next(256), $rnd.Next(256) }
foreach ($ip in $samples) {
    $n = (ConvertTo-IpRange $ip).Start
    $isAllowed = @($real    | Where-Object { $BI::Compare($n, $_.Start) -ge 0 -and $BI::Compare($n, $_.End) -le 0 }).Count -gt 0
    $isBlocked = @($blocked | Where-Object { $BI::Compare($n, $_.Start) -ge 0 -and $BI::Compare($n, $_.End) -le 0 }).Count -gt 0
    if ($isAllowed -eq $isBlocked) { $bad++ }
}
Check "every address is either allowed or blocked, never both ($($samples.Count) samples)" ($bad -eq 0) "$bad mismatches"

# ---- input validation ----
Write-Host 'Input validation'
Check 'valid IPv4 range'  (Test-Cidr '198.51.100.0/24')
Check 'valid IPv6 range'  (Test-Cidr '2001:db8::/32')
Check '/0 is rejected'    (-not (Test-Cidr '0.0.0.0/0'))
Check 'prefix too long'   (-not (Test-Cidr '10.0.0.0/33'))
Check 'garbage rejected'  (-not (Test-Cidr 'example.com/24') -and -not (Test-Cidr '10.0.0.0/x') -and -not (Test-Cidr '10.0.0.0/8/8'))

function Write-Warn2([string]$msg) { }   # keep the test output quiet
$res = @(Resolve-Servers @('203.0.113.7', "bad name", "evil`n1.2.3.4 bank.example", '*.example.com', 'http://x', 'localhost'))
Check 'IP literal passes through' (@($res | Where-Object { $_.Ip -eq '203.0.113.7' -and -not $_.IsHostname }).Count -eq 1)
Check 'malformed host names are dropped' (@($res | Where-Object { $_.Name -match '\s|\*|/' }).Count -eq 0)
Check 'real host name resolves' (@($res | Where-Object { $_.Name -eq 'localhost' -and $_.IsHostname }).Count -ge 1)

$m = [regex]::Matches("client`nremote vpn.example.com 1194 udp`n  remote 203.0.113.9 443`n", '(?m)(?:^|\\n)\s*remote\s+([^\s\\"]+)') | ForEach-Object { $_.Groups[1].Value }
Check 'OpenVPN "remote" lines are found' (Same $m @('vpn.example.com', '203.0.113.9'))

# ---- hosts file ----
Write-Host 'Hosts file'
try {
    # CRLF file with a Windows-1251 comment and a UTF-8 comment: neither is valid ASCII.
    $bytes = [byte[]](0x23, 0x20, 0xCF, 0xF0, 0xE8, 0xEC, 0xE5, 0xF0, 0x0D, 0x0A) +
             [Text.Encoding]::UTF8.GetBytes("# caf$([char]0xE9) $([char]0x4F60)`r`n127.0.0.1 localhost`r`n10.0.0.5 nas.local`r`n")
    [IO.File]::WriteAllBytes($HostsFile, $bytes)

    Set-HostsPins $null
    Check 'no pins, no block: file is not rewritten' (Same ([IO.File]::ReadAllBytes($HostsFile)) $bytes)

    $servers = @(
        [pscustomobject]@{ Name = 'vpn.example.com'; Ip = '203.0.113.7'; IsHostname = $true },
        [pscustomobject]@{ Name = 'vpn.example.com'; Ip = '203.0.113.7'; IsHostname = $true },
        [pscustomobject]@{ Name = '203.0.113.9';     Ip = '203.0.113.9'; IsHostname = $false }
    )
    Set-HostsPins $servers
    $after = [IO.File]::ReadAllBytes($HostsFile)
    $text = $HostsEncoding.GetString($after)
    Check 'original bytes are kept in front of the block' (Same $after[0..($bytes.Length - 1)] $bytes)
    Check 'host name is pinned once, IP literal is not' (([regex]::Matches($text, '203\.0\.113\.7 vpn\.example\.com')).Count -eq 1 -and $text -notmatch '203\.0\.113\.9')

    Set-HostsPins @([pscustomobject]@{ Name = 'new.example.com'; Ip = '203.0.113.8'; IsHostname = $true })
    $text = $HostsEncoding.GetString([IO.File]::ReadAllBytes($HostsFile))
    Check 'pins are replaced, not appended' ($text -match 'new\.example\.com' -and $text -notmatch 'vpn\.example\.com' -and ([regex]::Matches($text, '>>> vpn-killswitch')).Count -eq 1)

    Set-HostsPins $null
    Check 'removing the pins restores the file byte for byte' (Same ([IO.File]::ReadAllBytes($HostsFile)) $bytes)
}
finally { Remove-Item $HostsFile -Force -ErrorAction SilentlyContinue }

Write-Host ''
if ($script:failed -eq 0) { Write-Host "All $($script:passed) checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$($script:failed) of $($script:passed + $script:failed) checks FAILED." -ForegroundColor Red
exit 1
