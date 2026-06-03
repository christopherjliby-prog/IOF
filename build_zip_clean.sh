#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
DATE=$(date +%Y-%m-%d)
OUT_ZIP="IOF_Clean_${DATE}.zip"
echo "Building..." && dotnet build IOF_Clean/IOF_Clean.csproj -c Release -v quiet
echo "Packaging $OUT_ZIP..." && rm -f "$OUT_ZIP"
zip -j "$OUT_ZIP" "IOF_Clean/bin/Release/IOF_Clean.dll"
zip "$OUT_ZIP" "IOF_Clean/IOF_Clean.cs"
python3 - <<'PYEOF'
import zipfile, os, datetime
orig = "IOF_Clean_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "IOF_Clean/"
with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        new_item = zipfile.ZipInfo(sub + os.path.basename(item.filename))
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)
os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
