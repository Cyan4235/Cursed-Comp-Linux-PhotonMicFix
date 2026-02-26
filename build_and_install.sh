#!/bin/bash
echo "=== PhotonMicFix Build & Install ==="
cd "$(dirname "$0")"

dotnet build -c Release
if [ $? -ne 0 ]; then
    echo "BUILD FAILED"
    exit 1
fi

DEST="$HOME/.local/share/Steam/steamapps/common/Cursed Companions/BepInEx/plugins/PhotonMicFix.dll"
cp -f bin/Release/net6.0/PhotonMicFix.dll "$DEST"
echo "Installed to: $DEST"
strings "$DEST" | grep "v[0-9]\." | head -1
echo "=== Done ==="
