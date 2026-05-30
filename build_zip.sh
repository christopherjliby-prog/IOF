#!/usr/bin/env bash
# Build TradePhantoms_IOF_v2 and package as a Quantower-ready zip.
# Usage: ./build_zip.sh
# Output: TradePhantoms_IOF_v2_FULL_INDICATOR_<date>.zip

set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="TradePhantoms_IOF_v2_FULL_INDICATOR_${DATE}.zip"
SUBFOLDER="TradePhantoms_IOF_v2"
BUILD_DIR="bin/Release"

echo "Building..."
dotnet build TradePhantoms_IOF_v2.csproj -c Release -v quiet

echo "Packaging $OUT_ZIP..."
rm -f "$OUT_ZIP"

# Source files that go in the zip (same set as Brandon's delivery)
SOURCE_FILES=(
    AlertsHelper.cs
    ControlPointMarkerRenderer.cs
    DashboardRenderer.cs
    EntryAndTPHelpers.cs
    IOFZoneRegistry.cs
    MultiTimeframeZones.cs
    StatsStripRenderer.cs
    TradeIntentChannel.cs
    TradeLifecycle.cs
    TradePhantoms_IOF_v2.cs
    TrailStrategies.cs
    TrendStateMachine.cs
)

# Add DLL
zip -j "$OUT_ZIP" "$BUILD_DIR/TradePhantoms_IOF_v2.dll"

# Add source files under the subfolder path
for f in "${SOURCE_FILES[@]}"; do
    zip "$OUT_ZIP" "$f"
done

# Rename entries inside zip to live under TradePhantoms_IOF_v2/ subfolder
python3 - <<'PYEOF'
import zipfile, os
import datetime

orig = "TradePhantoms_IOF_v2_FULL_INDICATOR_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "TradePhantoms_IOF_v2/"

with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        new_item = zipfile.ZipInfo(sub + item.filename)
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)

os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
