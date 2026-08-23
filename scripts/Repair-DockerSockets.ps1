<#
.SYNOPSIS
    Clears the stale Unix sockets that stop Docker Desktop from starting, then starts it.

.DESCRIPTION
    Shutting Windows down without quitting Docker Desktop leaves its AF_UNIX sockets
    behind as reparse points. On the next start Docker tries to remove each one before
    listening on it, the removal fails with "The file cannot be accessed by the system",
    and the whole service dies with "An unexpected error occurred".

    The sockets cannot be deleted — not by Remove-Item, not by `del`, not by
    `fsutil reparsepoint delete`. Renaming the directory that holds them does work, and
    Docker recreates it on the next start.

    Failures come one service at a time: clearing the Inference manager's socket only
    reveals the Secrets Engine's. Every known location is therefore cleared in one pass.

    IMPORTANT: never use "Reset to factory defaults" in Docker Desktop's error dialog to
    work around this. That deletes every volume, including worklane_pgdata, which holds
    the entire Worklane database.

.PARAMETER SkipStart
    Clear the sockets but leave Docker Desktop stopped.

.PARAMETER TimeoutSeconds
    How long to wait for the daemon to answer before giving up. Default 300.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/Repair-DockerSockets.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipStart,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$socketDirs = @(
    (Join-Path $localAppData 'Docker\run')
    (Join-Path $localAppData 'docker-secrets-engine')
)
$dockerExe = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'

function Test-DockerDaemon {
    try {
        & docker ps 2>&1 | Out-Null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

if (Test-DockerDaemon) {
    Write-Host 'Docker is already running; nothing to repair.' -ForegroundColor Green
    exit 0
}

Write-Host 'Stopping Docker Desktop...'
Get-Process -Name 'Docker Desktop', 'com.docker.backend', 'docker-ai' -ErrorAction SilentlyContinue |
    Stop-Process -Force -Confirm:$false
Start-Sleep -Seconds 5

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($dir in $socketDirs) {
    if (-not (Test-Path -LiteralPath $dir)) {
        Write-Host "  absent : $dir"
        continue
    }

    # Only move a directory that actually holds a socket, so a healthy install is left
    # alone and the parent does not fill up with empty castoffs.
    #
    # A live socket looks identical to a stale one — both are reparse points — but this
    # code is only reached after the daemon failed to answer and Docker was stopped, so
    # anything still sitting here is by definition left over.
    $stale = Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -match 'ReparsePoint' }

    if (-not $stale) {
        Write-Host "  clean  : $dir"
        continue
    }

    $newName = '{0}.stale-{1}' -f (Split-Path $dir -Leaf), $stamp
    try {
        Rename-Item -LiteralPath $dir -NewName $newName -ErrorAction Stop
        Write-Host ("  cleared: {0} ({1} socket(s) -> {2})" -f $dir, $stale.Count, $newName) -ForegroundColor Yellow
    } catch {
        Write-Warning ("  FAILED : {0} — {1}" -f $dir, $_.Exception.Message)
        Write-Warning '  A reboot clears these reliably if the rename cannot be done.'
    }
}

if ($SkipStart) {
    Write-Host 'Sockets cleared. Docker Desktop left stopped (-SkipStart).' -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $dockerExe)) {
    Write-Warning "Docker Desktop not found at $dockerExe — start it yourself."
    exit 1
}

Write-Host 'Starting Docker Desktop...'
Start-Process $dockerExe

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    if (Test-DockerDaemon) {
        Write-Host 'Docker is ready.' -ForegroundColor Green
        exit 0
    }
    Start-Sleep -Seconds 5
}

Write-Warning "Docker did not answer within $TimeoutSeconds seconds."
Write-Warning "Check $localAppData\Docker\backend.error.json for the service that failed."
Write-Warning 'Do NOT use "Reset to factory defaults" — it destroys the worklane_pgdata volume.'
exit 1
