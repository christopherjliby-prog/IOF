#!/usr/bin/env bash
# Build IOF_TradeJournal and package as a Quantower-ready zip.
# Usage: ./build_zip_journal.sh
# Output: IOF_TradeJournal_WTF_<date>.zip
# Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF_TradeJournal\

set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="IOF_TradeJournal_WTF_${DATE}.zip"
SUBFOLDER="IOF_TradeJournal"
BUILD_DIR="IOF_TradeJournal/bin/Release"
SRC_DIR="IOF_TradeJournal"

echo "Building..."
dotnet build IOF_TradeJournal/IOF_TradeJournal.csproj -c Release -v quiet

echo "Packaging $OUT_ZIP..."
rm -f "$OUT_ZIP"

SOURCE_FILES=(
    EntryGrader.cs
    IOF_TradeJournal.cs
    JournalEntry.cs
    JournalWriter.cs
    SessionClassifier.cs
)

# Add DLL
zip -j "$OUT_ZIP" "$BUILD_DIR/IOF_TradeJournal.dll"

# Add source files
for f in "${SOURCE_FILES[@]}"; do
    zip "$OUT_ZIP" "$SRC_DIR/$f"
done

# Rename entries to live under IOF_TradeJournal/ subfolder
python3 - <<'PYEOF'
import zipfile, os, datetime

orig = "IOF_TradeJournal_WTF_" + datetime.date.today().strftime('%Y-%m-%d') + ".zip"
tmp  = orig + ".tmp"
sub  = "IOF_TradeJournal/"

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
