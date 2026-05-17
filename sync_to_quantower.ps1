# =============================================================================
# sync_to_quantower.ps1 — IOF Indicator Auto-Sync
# =============================================================================
# Pulls the latest indicator files from the IOF GitHub repo and copies them
# to your Quantower scripts folder. Run this whenever you want to update.
#
# USAGE (run as normal user — no admin needed):
#   Right-click → "Run with PowerShell"
#   OR from PowerShell terminal: .\sync_to_quantower.ps1
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
# 2026-05-17: Initial build.
#   - Downloads .cs files directly from GitHub raw content (no git required)
#   - Creates Quantower folder if it doesn't exist
#   - Backs up existing files to IOF\_backup_YYYYMMDD_HHMMSS\ before overwriting
#   - Shows a simple pass/fail summary at the end
# =============================================================================

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

# ── Pre-flight ────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "IOF Quantower Sync" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host "Target: $QuantowerPath"
Write-Host "Source: github.com/$RepoOwner/$RepoName @ $Branch"
Write-Host ""

# Create folder if it doesn't exist
if (-not (Test-Path $QuantowerPath)) {
    Write-Host "Creating folder: $QuantowerPath" -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $QuantowerPath -Force | Out-Null
}

# Backup existing .cs files
$existing = Get-ChildItem -Path $QuantowerPath -Filter "*.cs" -ErrorAction SilentlyContinue
if ($existing.Count -gt 0) {
    $stamp     = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = Join-Path $QuantowerPath "_backup_$stamp"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $existing | Copy-Item -Destination $backupDir
    Write-Host "Backed up $($existing.Count) existing file(s) → _backup_$stamp" -ForegroundColor DarkGray
    Write-Host ""
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
        Write-Host "  OK  $file" -ForegroundColor Green
        $passed++
        $results += [PSCustomObject]@{ File = $file; Status = "OK" }
    }
    catch {
        $code = $_.Exception.Response.StatusCode.Value__
        if ($code -eq 404) {
            Write-Host " SKIP $file (not in repo yet)" -ForegroundColor DarkGray
            $results += [PSCustomObject]@{ File = $file; Status = "SKIP (404)" }
        } else {
            Write-Host " FAIL $file — $_" -ForegroundColor Red
            $failed++
            $results += [PSCustomObject]@{ File = $file; Status = "FAIL: $_" }
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray
if ($failed -eq 0) {
    Write-Host "Sync complete — $passed file(s) updated." -ForegroundColor Green
    Write-Host ""
    Write-Host "Reload the indicator in Quantower to apply changes:" -ForegroundColor Yellow
    Write-Host "  Settings → Scripts → right-click indicator → Reload" -ForegroundColor White
} else {
    Write-Host "Sync finished with $failed failure(s). Check output above." -ForegroundColor Red
}
Write-Host ""

# Keep window open if double-clicked
if ($Host.Name -eq "ConsoleHost") {
    Write-Host "Press any key to close..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
