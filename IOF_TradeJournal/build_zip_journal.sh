#!/usr/bin/env bash
# Build IOF_TradeJournal ("WTF Are You Doing?!") and package as a Quantower-ready zip.
# Usage: ./build_zip_journal.sh
# Output: IOF_TradeJournal_FULL_INDICATOR_<date>.zip

set -e
cd "$(dirname "$0")"

DATE=$(date +%Y-%m-%d)
OUT_ZIP="IOF_TradeJournal_FULL_INDICATOR_${DATE}.zip"

echo "Building IOF Trade Journal..."
dotnet build IOF_TradeJournal.csproj -c Release -v quiet

echo "Packaging ${OUT_ZIP}..."
rm -f "$OUT_ZIP"

SOURCE_FILES=(
    IOF_TradeJournal.cs
    JournalEntry.cs
    JournalWriter.cs
    EntryGrader.cs
    SessionClassifier.cs
)

# DLL
zip -j "$OUT_ZIP" "bin/Release/IOF_TradeJournal.dll"

# Source files
for f in "${SOURCE_FILES[@]}"; do
    zip "$OUT_ZIP" "$f"
done

# Rename entries inside zip to live under IOF_TradeJournal/ subfolder
python3 - <<PYEOF
import zipfile, os, datetime

orig = "IOF_TradeJournal_FULL_INDICATOR_${DATE}.zip"
tmp  = orig + ".tmp"
sub  = "IOF_TradeJournal/"

with zipfile.ZipFile(orig, 'r') as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        new_item = zipfile.ZipInfo(sub + item.filename)
        new_item.compress_type = zipfile.ZIP_DEFLATED
        zout.writestr(new_item, data)

os.replace(tmp, orig)
print(f"Done: {orig}")
PYEOF
