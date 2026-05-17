# =============================================================================
# install_auto_sync.ps1 — Register IOF Auto-Sync as a Windows Logon Task
# =============================================================================
# Run this ONCE. After that, every time you log into Windows the sync runs
# automatically in the background — by the time Quantower opens, the indicator
# files are already up to date.
#
# USAGE:
#   Right-click → "Run as Administrator"  (needed to register the task)
#
# WHAT IT DOES:
#   1. Copies sync_to_quantower.ps1 to a permanent location
#      (C:\Quantower\Settings\Scripts\Indicators\IOF\sync_to_quantower.ps1)
#   2. Registers a Task Scheduler task: "IOF_Quantower_Sync"
#      - Trigger: At logon (your user account only)
#      - Action:  Run sync_to_quantower.ps1 -Silent in the background
#      - No window, no interruption — just updates the files silently
#   3. Runs the sync immediately so you're current right now
#
# TO UNINSTALL:
#   Unregister-ScheduledTask -TaskName "IOF_Quantower_Sync" -Confirm:$false
#
# TO CHECK LAST SYNC:
#   Open: C:\Quantower\Settings\Scripts\Indicators\IOF\sync_log.txt
#
# -----------------------------------------------------------------------------
# PATCH NOTES
# -----------------------------------------------------------------------------
# 2026-05-17: Initial build.
# =============================================================================

$ErrorActionPreference = "Stop"

# ── Config ────────────────────────────────────────────────────────────────────

$TaskName       = "IOF_Quantower_Sync"
$QuantowerPath  = "C:\Quantower\Settings\Scripts\Indicators\IOF"
$ScriptDest     = Join-Path $QuantowerPath "sync_to_quantower.ps1"
$ScriptSource   = Join-Path $PSScriptRoot "sync_to_quantower.ps1"

# ── Pre-flight ────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "IOF Auto-Sync Installer" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Run this script as Administrator (right-click → Run as Administrator)" -ForegroundColor Red
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}

# Check sync script exists next to this installer
if (-not (Test-Path $ScriptSource)) {
    Write-Host "ERROR: sync_to_quantower.ps1 not found at:" -ForegroundColor Red
    Write-Host "  $ScriptSource" -ForegroundColor Red
    Write-Host "Make sure both scripts are in the same folder." -ForegroundColor Yellow
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}

# ── Step 1: Copy sync script to permanent location ────────────────────────────

Write-Host "Step 1: Installing sync script..." -ForegroundColor White

if (-not (Test-Path $QuantowerPath)) {
    New-Item -ItemType Directory -Path $QuantowerPath -Force | Out-Null
}

Copy-Item -Path $ScriptSource -Destination $ScriptDest -Force
Write-Host "  Installed to: $ScriptDest" -ForegroundColor Green

# ── Step 2: Register Scheduled Task ───────────────────────────────────────────

Write-Host ""
Write-Host "Step 2: Registering scheduled task..." -ForegroundColor White

# Remove existing task if present
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "  Removed existing task." -ForegroundColor DarkGray
}

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-WindowStyle Hidden -NonInteractive -ExecutionPolicy Bypass -File `"$ScriptDest`" -Silent"

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -DontStopOnIdleEnd

$principal = New-ScheduledTaskPrincipal `
    -UserId $env:USERNAME `
    -LogonType Interactive `
    -RunLevel Limited

Register-ScheduledTask `
    -TaskName  $TaskName `
    -Action    $action `
    -Trigger   $trigger `
    -Settings  $settings `
    -Principal $principal `
    -Description "Syncs IOF Quantower indicator files from GitHub on every logon." `
    | Out-Null

Write-Host "  Task registered: $TaskName" -ForegroundColor Green
Write-Host "  Trigger: At logon ($env:USERNAME)" -ForegroundColor Green
Write-Host "  Runs silently — no window" -ForegroundColor Green

# ── Step 3: Run sync right now ────────────────────────────────────────────────

Write-Host ""
Write-Host "Step 3: Running initial sync now..." -ForegroundColor White
Write-Host ""

& powershell.exe -WindowStyle Normal -ExecutionPolicy Bypass -File $ScriptDest

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host "All done. From now on:" -ForegroundColor Green
Write-Host "  - Log into Windows" -ForegroundColor White
Write-Host "  - Sync runs automatically in the background" -ForegroundColor White
Write-Host "  - Open Quantower — latest indicator files are already there" -ForegroundColor White
Write-Host ""
Write-Host "Sync log: $QuantowerPath\sync_log.txt" -ForegroundColor DarkGray
Write-Host ""

Read-Host "Press Enter to close"
