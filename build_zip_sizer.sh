#!/usr/bin/env bash
# Build IOF_PositionSizer and package as a Quantower-ready zip.
# Output: IOF_PositionSizer_<date>.zip
# Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF_PositionSizer\

set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="IOF_PositionSizer_${DATE}.zip"

echo "Building..."
dotnet build IOF_PositionSizer/IOF_PositionSizer.csproj -c Release -v quiet

echo "Packaging $OUT_ZIP..."
rm -f "$OUT_ZIP"

zip -j "$OUT_ZIP" "IOF_PositionSizer/bin/Release/IOF_PositionSizer.dll"
zip "$OUT_ZIP" "IOF_PositionSizer/IOF_PositionSizer.cs"

python3 - <<'PYEOF'
import zipfile, os, datetime

orig = "IOF_PositionSizer_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "IOF_PositionSizer/"

with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        basename = os.path.basename(item.filename)
        new_item = zipfile.ZipInfo(sub + basename)
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)

os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
