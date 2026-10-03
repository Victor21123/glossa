<#
.SYNOPSIS
  Builds Glossa (Release) into D:\Programs\Glossa, the copy started from the desktop shortcut.
.DESCRIPTION
  Builds into a staging folder first, so a failed build leaves the installed copy alone. Then closes the running
  Glossa (it listens for Global\Glossa.Exit), mirrors the build into the target, makes the desktop shortcut if it
  is missing, points autostart at the new exe if autostart is on, and starts Glossa in the tray again if it was
  running. User data stays in D:\GlossaData.
#>
param(
    [string]$Target = 'D:\Programs\Glossa',
    # Start Glossa in the tray even if it was not running.
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $root 'src\Glossa.App\bin\publish'
$exe = Join-Path $Target 'Glossa.exe'

# /MIR deletes whatever the build does not have: never point it at a folder that is not ours.
if ((Test-Path $Target) -and (Get-ChildItem $Target -Force | Select-Object -First 1) -and -not (Test-Path $exe)) {
    throw "$Target is not empty and has no Glossa.exe; not touching it."
}

# The companions' art: packed and encrypted from the author's catalog into the secrets folder (outside the
# repository, see Directory.Build.props); the build puts companions.pack beside the exe. No secrets - no companions.
$secrets = if ($env:GLOSSA_SECRETS) { $env:GLOSSA_SECRETS } else { Join-Path (Split-Path $root) 'GlossaSecrets' }
if (Test-Path (Join-Path $secrets 'keys.json')) {
    Push-Location $root
    try { dotnet run --project (Join-Path $root 'src\Glossa.Cli') -c Release -v quiet -- companions pack }
    finally { Pop-Location }
    if ($LASTEXITCODE) { throw "companions pack failed ($LASTEXITCODE)" }
} else {
    Write-Host 'No secrets folder: this build has no companions.'
}
Write-Host 'Building...'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish (Join-Path $root 'src\Glossa.App\Glossa.App.csproj') -c Release -o $stage --nologo -v quiet
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

$running = @(Get-Process Glossa -ErrorAction SilentlyContinue)
if ($running) {
    Write-Host 'Closing the running Glossa...'
    $signal = $null
    try { $signal = [Threading.EventWaitHandle]::OpenExisting('Global\Glossa.Exit') } catch { }
    if ($signal) { [void]$signal.Set(); $signal.Dispose() }
    # Builds before 2026-09-29 do not listen; llama-server dies with Glossa either way (job object).
    $wait = if ($signal) { 15000 } else { 0 }
    foreach ($p in $running) {
        if (-not $p.WaitForExit($wait)) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
    }
}

New-Item -ItemType Directory -Force $Target | Out-Null
# A portable copy keeps its data in GlossaData beside the exe: /MIR must not wipe it.
robocopy $stage $Target /MIR /XD (Join-Path $Target 'GlossaData') /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0

$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Glossa.lnk'
if (-not (Test-Path $lnk)) {
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Target
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Save()
    Write-Host "Shortcut: $lnk"
}

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty $run -Name Glossa -ErrorAction SilentlyContinue) {
    Set-ItemProperty $run -Name Glossa -Value "`"$exe`" --tray"
}

if ($running -or $Start) {
    Start-Process $exe -ArgumentList '--tray' -WorkingDirectory $Target
    Write-Host 'Glossa started in the tray.'
}
$mb = (Get-ChildItem $Target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ('Installed: {0} ({1:N0} MB)' -f $exe, $mb)
