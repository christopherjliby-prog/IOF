#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="FiveMinMoneyMaker_${DATE}.zip"

echo "Building..."
dotnet build FiveMinMoneyMaker/FiveMinMoneyMaker.csproj -c Release -v quiet

echo "Packaging $OUT_ZIP..."
rm -f "$OUT_ZIP"
zip -j "$OUT_ZIP" "FiveMinMoneyMaker/bin/Release/FiveMinMoneyMaker.dll"
zip "$OUT_ZIP" "FiveMinMoneyMaker/FiveMinMoneyMaker.cs"

python3 - <<'PYEOF'
import zipfile, os, datetime
orig = "FiveMinMoneyMaker_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "FiveMinMoneyMaker/"
with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        new_item = zipfile.ZipInfo(sub + os.path.basename(item.filename))
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)
os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
