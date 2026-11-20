#!/bin/sh
# Build librasamic_native.dylib for iOS (device arm64). Run on macOS with Xcode.
# Simulator: CMAKE_OSX_SYSROOT=iphonesimulator and arch arm64/x86_64 as needed.
set -e
cd "$(dirname "$0")/../../native/rasamic-native"
cmake -S . -B build-ios-arm64 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0
cmake --build build-ios-arm64
echo "Built: $(pwd)/build-ios-arm64/librasamic_native.dylib"