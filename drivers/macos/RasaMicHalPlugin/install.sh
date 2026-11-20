#!/bin/bash
# Build + install the Rasa Mic HAL plugin. One sudo prompt; coreaudiod is
# restarted by launchd automatically after killall.
set -e
cd "$(dirname "$0")"

cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build

echo "Installing RasaMicHalPlugin.driver to /Library/Audio/Plug-Ins/HAL (sudo)..."
sudo mkdir -p "/Library/Application Support/RasaMic"
sudo cp -R build/RasaMicHalPlugin.driver /Library/Audio/Plug-Ins/HAL/
sudo killall coreaudiod 2>/dev/null || true
sleep 3

echo "Installed. Check the device is listed:"
echo "  system_profiler SPAudioDataType | grep -B2 -A4 Rasa"
echo "Then start the feed:  rasamicd --hal"
echo "Uninstall: sudo rm -rf /Library/Audio/Plug-Ins/HAL/RasaMicHalPlugin.driver && sudo killall coreaudiod"