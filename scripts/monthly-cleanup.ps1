<#
  monthly-cleanup.ps1 — clears folders that only hold temporary, re-creatable data.
  Meant to run once a month from Task Scheduler (see install-monthly-cleanup.ps1).

  What it clears:
    - %TEMP%                          items not touched for 7+ days (anything newer may be in use)
    - Recycle Bin                     items deleted more than 30 days ago
    - %LOCALAPPDATA%\CrashDumps       crash dumps older than 7 days
    - Spotify streaming cache         only while Spotify is closed (downloaded songs live elsewhere)
    - Brave / Chrome / Edge disk caches   only while that browser is closed (no history, logins or cookies)

  What it deliberately leaves alone: GPU shader caches (deleting them causes stutter in games),
  hiberfil.sys / pagefile.sys, Downloads, and anything outside the user profile.

  Writes a log next to itself (cleanup.log, last ~500 lines kept).
  -WhatIf lists what would be removed without deleting anything.
#>
param([switch]$WhatIf)

$ErrorActionPreference = 'SilentlyContinue'
$log   = Join-Path $PSScriptRoot 'cleanup.log'
$lines = [System.Collections.Generic.List[string]]::new()
function Say($msg) { $lines.Add(('{0:yyyy-MM-dd HH:mm}  {1}' -f (Get-Date), $msg)) }

function Clear-Items {
    param([string]$Label, [string]$Path, [int]$OlderThanDays = 0)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $cut = (Get-Date).AddDays(-$OlderThanDays)
    $freed = 0L; $skipped = 0
    # files first, then folders that ended up empty
    Get-ChildItem -LiteralPath $Path -Recurse -File -Force |
        Where-Object { $_.LastWriteTime -lt $cut } |
        ForEach-Object {
            $len = $_.Length
            if ($WhatIf) { $freed += $len; return }
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $freed += $len } catch { $skipped++ }
        }
    if (-not $WhatIf) {
        Get-ChildItem -LiteralPath $Path -Recurse -Directory -Force |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1) } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    Say ('{0,-22} {1,8:N1} MB freed{2}' -f $Label, ($freed / 1MB), $(if ($skipped) { ", $skipped in use (skipped)" } else { '' }))
    $script:total += $freed
}

$total = 0L
$before = (Get-PSDrive C).Free
Say ('--- start{0}' -f $(if ($WhatIf) { ' (WhatIf: nothing deleted)' } else { '' }))

# 1. User temp folder
Clear-Items -Label 'Temp' -Path $env:TEMP -OlderThanDays 7

# 2. Crash dumps
Clear-Items -Label 'CrashDumps' -Path "$env:LOCALAPPDATA\CrashDumps" -OlderThanDays 7

# 3. Spotify streaming cache (only when Spotify is not running)
if (Get-Process Spotify) { Say 'Spotify cache          skipped (Spotify running)' }
else { Clear-Items -Label 'Spotify cache' -Path "$env:LOCALAPPDATA\Spotify\Data" }

# 4. Browser disk caches (only when the browser is not running)
$browsers = @(
    @{ Name = 'Brave';  Proc = 'brave';  Root = "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\User Data" },
    @{ Name = 'Chrome'; Proc = 'chrome'; Root = "$env:LOCALAPPDATA\Google\Chrome\User Data" },
    @{ Name = 'Edge';   Proc = 'msedge'; Root = "$env:LOCALAPPDATA\Microsoft\Edge\User Data" }
)
foreach ($b in $browsers) {
    if (-not (Test-Path -LiteralPath $b.Root)) { continue }
    if (Get-Process $b.Proc) { Say ('{0,-22} skipped ({1} running)' -f "$($b.Name) cache", $b.Name); continue }
    Get-ChildItem -LiteralPath $b.Root -Directory -Force |
        Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' } |
        ForEach-Object {
            foreach ($sub in 'Cache', 'Code Cache', 'GPUCache') {
                Clear-Items -Label ("{0} {1}" -f $b.Name, $sub) -Path (Join-Path $_.FullName $sub)
            }
        }
}

# 5. Recycle Bin: items deleted more than 30 days ago
try {
    $shell = New-Object -ComObject Shell.Application
    $bin = $shell.NameSpace(10)
    $cut = (Get-Date).AddDays(-30); $freed = 0L; $n = 0
    foreach ($item in @($bin.Items())) {
        $deleted = $item.ExtendedProperty('System.Recycle.DateDeleted')
        if ($deleted -and ([datetime]$deleted) -lt $cut) {
            $size = [long]$item.ExtendedProperty('System.Size')
            if (-not $WhatIf) { Remove-Item -LiteralPath $item.Path -Recurse -Force }
            $freed += $size; $n++
        }
    }
    Say ('{0,-22} {1,8:N1} MB freed ({2} items older than 30 days)' -f 'Recycle Bin', ($freed / 1MB), $n)
    $total += $freed
} catch { Say "Recycle Bin            failed: $($_.Exception.Message)" }

$after = (Get-PSDrive C).Free
Say ('--- done: {0:N2} GB counted, C: free space {1:N1} GB -> {2:N1} GB' -f ($total / 1GB), ($before / 1GB), ($after / 1GB))

$old = if (Test-Path $log) { Get-Content $log -Tail 500 } else { @() }
Set-Content -Path $log -Value (@($old) + $lines) -Encoding UTF8
$lines
