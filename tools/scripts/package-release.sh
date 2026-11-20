#!/usr/bin/env bash
# Packages rasamicd + the native audio pipeline into dist/rasamic-<ver>-<rid>.zip
# Usage: tools/scripts/package-release.sh <rid> <native-lib-path> [version]
#   e.g. tools/scripts/package-release.sh win-x64 native/rasamic-native/build-win-x64/rasamic_native.dll
set -euo pipefail
cd "$(dirname "$0")/../.."

rid="${1:?usage: package-release.sh <rid> <native-lib> [version]}"
native="${2:?path to rasamic_native.dll / librasamic_native.so / librasamic_native.dylib}"
version="${3:-0.1.0}"
[ -f "$native" ] || { echo "native lib not found: $native" >&2; exit 1; }

name="rasamic-$version-$rid"
out="dist/$name"
rm -rf "$out"
mkdir -p dist

dotnet publish src/RasaMic.Server.Cli -c Release -r "$rid" --self-contained true -o "$out"
cp "$native" "$out/"
cp LICENSE THIRD_PARTY_NOTICES.md README.md "$out/"

if command -v zip >/dev/null 2>&1; then
    (cd dist && zip -qr "$name.zip" "$name")
else
    powershell.exe -NoProfile -Command "Compress-Archive -Path 'dist/$name' -DestinationPath 'dist/$name.zip' -Force"
fi
echo "PACKAGED dist/$name.zip"