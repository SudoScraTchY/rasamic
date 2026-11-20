#!/usr/bin/env bash
# Velopack installer for the Windows GUI: Setup.exe + full/delta nupkgs + RELEASES.
# Usage: tools/scripts/package-velopack.sh <win-rid> <native-lib-path> [version]
# Requires the vpk CLI: dotnet tool install -g vpk
set -euo pipefail
cd "$(dirname "$0")/../.."

rid="${1:?usage: package-velopack.sh <win-rid> <native-lib> [version]}"
native="${2:?path to rasamic_native.dll}"
version="${3:-0.1.0}"
case "$rid" in win-*) ;; *) echo "this script targets Windows RIDs (win-x64 / win-arm64)" >&2; exit 1;; esac
[ -f "$native" ] || { echo "native lib not found: $native" >&2; exit 1; }
command -v vpk >/dev/null 2>&1 || { echo "vpk not found — run: dotnet tool install -g vpk" >&2; exit 1; }

out="dist/velopack-$rid"
rm -rf "$out"
mkdir -p dist

dotnet publish src/RasaMic.Server.App -c Release -r "$rid" --self-contained true -o "$out"
cp "$native" "$out/"
cp LICENSE THIRD_PARTY_NOTICES.md README.md "$out/"

vpk pack --packId RasaMic --packVersion "$version" --packDir "$out" \
    --mainExe RasaMic.Server.App.exe --packTitle "Rasa" --outputDir "dist/velopack-out-$rid"
echo "PACKAGED dist/velopack-out-$rid"