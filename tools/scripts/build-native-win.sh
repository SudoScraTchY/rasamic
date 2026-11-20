#!/usr/bin/env bash
# Build rasamic_native for win-x64 using MSVC (VS 2026) + Ninja (Android SDK cmake).
set -euo pipefail
MSYS_NO_PATHCONV=1 cmd /c "M:\\Agentic\\Projects\\WoMic-Foss\\tools\\scripts\\build-native-win.bat"
echo "OK: native/rasamic-native/build-win-x64/rasamic_native.dll"