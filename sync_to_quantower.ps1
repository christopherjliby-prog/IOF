# =============================================================================
# sync_to_quantower.ps1 — IOF Indicator Auto-Sync
# =============================================================================
# Pulls the latest indicator files from the IOF GitHub repo and copies them
# to your Quantower scripts folder.
#
# USAGE (manual):
#   Right-click → "Run with PowerShell"
#   OR from PowerShell terminal: .\sync_to_quantower.ps1
#
# USAGE (silent — called by Task Scheduler on logon):
#   powershell.exe -WindowStyle Hidden -File sync_to_quantower.ps1 -Silent
#
# QUANTOWER PATH: C:\Quantower\Settings\Scripts\Indicators\IOF
#
# WHAT IT SYNCS:
#   - TradePhantoms_IOF_v2.cs         (main indicator)
#   - IOFZoneRegistry.cs              (shared zone registry)
#   - VolumeSpike_IOF.cs              (volume spike confluence)
#   - VIX_PositionGuard.cs            (volatility safety HUD)
#   - AlertsHelper.cs
#   - ControlPointMarkerRenderer.cs
#   - DashboardRenderer.cs
#   - EntryAndTPHelpers.cs
#   - MultiTimeframeZones.cs
#
# -----------------------------------------------------------------------------
# PATCH NOTES
# -----------------------------------------------------------------------------
# 2026-05-17: Added -Silent flag for Task Scheduler / headless use.
#   - Logs results to C:\Quantower\Settings\Scripts\Indicators\IOF\sync_log.txt
#     instead of console when running silently
#
# 2026-05-17: Initial build.
#   - Downloads .cs files directly from GitHub raw content (no git required)
#   - Creates Quantower folder if it doesn't exist
#   - Backs up existing files to IOF\_backup_YYYYMMDD_HHMMSS\ before overwriting
#   - Shows a simple pass/fail summary at the end
# =============================================================================

param(
    [switch]$Silent   # Pass -Silent to suppress all UI (used by Task Scheduler)
)

$ErrorActionPreference = "Stop"

# ── Config ────────────────────────────────────────────────────────────────────

$QuantowerPath = "C:\Quantower\Settings\Scripts\Indicators\IOF"
$RepoOwner     = "christopherjliby-prog"
$RepoName      = "IOF"
$Branch        = "claude/google-drive-messaging-NacV0"

# Files to sync — add new .cs files here as they're added to the repo
$FilesToSync = @(
    "TradePhantoms_IOF_v2.cs",
    "IOFZoneRegistry.cs",
    "VolumeSpike_IOF.cs",
    "VIX_PositionGuard.cs",
    "AlertsHelper.cs",
    "ControlPointMarkerRenderer.cs",
    "DashboardRenderer.cs",
    "EntryAndTPHelpers.cs",
    "MultiTimeframeZones.cs"
)

$RawBase = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/$Branch"

# ── Logging helper ────────────────────────────────────────────────────────────

$LogFile = Join-Path $QuantowerPath "sync_log.txt"

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Message"
    if (-not $Silent) {
        Write-Host $Message -ForegroundColor $Color
    }
    # Always append to log file (create folder first if needed)
    if (-not (Test-Path $QuantowerPath)) {
        New-Item -ItemType Directory -Path $QuantowerPath -Force | Out-Null
    }
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
}

# ── Pre-flight ────────────────────────────────────────────────────────────────

Write-Log ""
Write-Log "IOF Quantower Sync" "Cyan"
Write-Log "─────────────────────────────────────────" "DarkGray"
Write-Log "Target: $QuantowerPath"
Write-Log "Source: github.com/$RepoOwner/$RepoName @ $Branch"
Write-Log ""

# Create folder if it doesn't exist
if (-not (Test-Path $QuantowerPath)) {
    Write-Log "Creating folder: $QuantowerPath" "Yellow"
    New-Item -ItemType Directory -Path $QuantowerPath -Force | Out-Null
}

# Backup existing .cs files
$existing = Get-ChildItem -Path $QuantowerPath -Filter "*.cs" -ErrorAction SilentlyContinue
if ($existing.Count -gt 0) {
    $stamp     = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = Join-Path $QuantowerPath "_backup_$stamp"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $existing | Copy-Item -Destination $backupDir
    Write-Log "Backed up $($existing.Count) existing file(s) → _backup_$stamp" "DarkGray"
    Write-Log ""
}

# ── Download ──────────────────────────────────────────────────────────────────

$passed = 0
$failed = 0
$results = @()

foreach ($file in $FilesToSync) {
    $url      = "$RawBase/$file"
    $destPath = Join-Path $QuantowerPath $file

    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -ErrorAction Stop
        [System.IO.File]::WriteAllBytes($destPath, $response.Content)
        Write-Log "  OK  $file" "Green"
        $passed++
        $results += [PSCustomObject]@{ File = $file; Status = "OK" }
    }
    catch {
        $code = $_.Exception.Response.StatusCode.Value__
        if ($code -eq 404) {
            Write-Log " SKIP $file (not in repo yet)" "DarkGray"
            $results += [PSCustomObject]@{ File = $file; Status = "SKIP (404)" }
        } else {
            Write-Log " FAIL $file — $_" "Red"
            $failed++
            $results += [PSCustomObject]@{ File = $file; Status = "FAIL: $_" }
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Log ""
Write-Log "─────────────────────────────────────────" "DarkGray"
if ($failed -eq 0) {
    Write-Log "Sync complete — $passed file(s) updated." "Green"
    if (-not $Silent) {
        Write-Host ""
        Write-Host "Reload the indicator in Quantower to apply changes:" -ForegroundColor Yellow
        Write-Host "  Settings → Scripts → right-click indicator → Reload" -ForegroundColor White
    }
} else {
    Write-Log "Sync finished with $failed failure(s). Check sync_log.txt for details." "Red"
}
Write-Log ""

# Keep window open if double-clicked (interactive mode only)
if (-not $Silent -and $Host.Name -eq "ConsoleHost") {
    Write-Host "Press any key to close..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
