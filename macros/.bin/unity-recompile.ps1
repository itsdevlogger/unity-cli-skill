param(
    [int]$TimeoutSeconds = 240,
    [switch]$Refresh
)

$ErrorActionPreference = 'Continue'
$signal = 'Library/unity-cli-compile.json'

function Test-Busy {
    $out = (unity command "console get" --level error --tail 1 2>&1 | Out-String)
    if ($out -match '"compiling":false') { return $false }
    return $true
}

function Read-Signal {
    param([string]$Path)

    $raw = Get-Content $Path -Raw -ErrorAction SilentlyContinue
    if (-not $raw) { return $null }
    if ($raw -notmatch '"ticks":(\d+)') { return $null }

    return [pscustomobject]@{
        Raw      = $raw.Trim()
        Ticks    = [long]$matches[1]
        Failed   = [bool]($raw -match '"failed":true')
        Reloaded = [bool]($raw -match '"stage":"reloaded"')
    }
}

if (-not (Test-Path 'ProjectSettings/ProjectVersion.txt')) {
    Write-Output '{"stage":"error","failed":true,"errors":0,"messages":["run this from a Unity project root"]}'
    exit 2
}

$before = 0
$previous = Read-Signal -Path $signal
if ($previous) { $before = $previous.Ticks }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

if ($Refresh) {
    unity command eval --code "UnityEditor.AssetDatabase.Refresh(); return 1.ToString();" *> $null
}

$triggered = $false
$upToDate = $false

while ((Get-Date) -lt $deadline) {
    $response = (unity command recompile 2>&1 | Out-String)

    if ($response -match 'up_to_date') { $upToDate = $true; break }
    if ($response -notmatch 'No Unity Editor instances') { $triggered = $true; break }

    Start-Sleep -Milliseconds 1000
}

if (-not $triggered -and -not $upToDate) {
    Write-Output "{""stage"":""error"",""failed"":true,""errors"":0,""messages"":[""no reachable Unity Editor within ${TimeoutSeconds}s""]}"
    exit 2
}

$lastTicks = -1
$stableSamples = 0

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500

    $current = Read-Signal -Path $signal

    if (-not $current -or $current.Ticks -le $before) {
        if ($upToDate) { break }
        $stableSamples = 0
        continue
    }

    if (-not ($current.Reloaded -or $current.Failed)) {
        $stableSamples = 0
        continue
    }

    if ($current.Ticks -eq $lastTicks) {
        $stableSamples++
    }
    else {
        $lastTicks = $current.Ticks
        $stableSamples = 0
    }

    if ($stableSamples -lt 4) { continue }
    if (Test-Busy) { $stableSamples = 0; continue }

    Write-Output $current.Raw
    if ($current.Failed) { exit 1 }
    exit 0
}

if ($upToDate) {
    Write-Output '{"stage":"up_to_date","failed":false,"errors":0,"messages":[]}'
    exit 0
}

Write-Output "{""stage"":""timeout"",""failed"":true,""errors"":0,""messages"":[""no compile signal within ${TimeoutSeconds}s""]}"
exit 3
