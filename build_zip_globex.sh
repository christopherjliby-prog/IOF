#!/usr/bin/env bash
# Build IOF_GlobexTrap and package as a Quantower-ready zip.
# Usage: ./build_zip_globex.sh
# Output: IOF_GlobexTrap_FULL_INDICATOR_<date>.zip

set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="IOF_GlobexTrap_FULL_INDICATOR_${DATE}.zip"
SUBFOLDER="IOF_GlobexTrap"
BUILD_DIR="GlobexTrap/bin/Release"

echo "Building..."
dotnet build GlobexTrap/GlobexTrap.csproj -c Release -v quiet

echo "Packaging $OUT_ZIP..."
rm -f "$OUT_ZIP"

zip -j "$OUT_ZIP" "$BUILD_DIR/IOF_GlobexTrap.dll"
zip "$OUT_ZIP" GlobexTrap/IOF_GlobexTrap.cs

python3 - <<'PYEOF'
import zipfile, os, datetime

orig = "IOF_GlobexTrap_FULL_INDICATOR_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "IOF_GlobexTrap/"

with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        # Strip any leading path (e.g. GlobexTrap/) so all entries are flat under the subfolder
        name = os.path.basename(item.filename)
        data = zin.read(item.filename)
        new_item = zipfile.ZipInfo(sub + name)
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)

os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
