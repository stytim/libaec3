#!/bin/bash
# Builds the arm64 macOS library into install/macos-arm64 (lib/aec3.dylib, include/aec3_api.h).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$ROOT/build/macos-arm64"

cmake -S "$ROOT" -B "$BUILD_DIR" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -DCMAKE_INSTALL_PREFIX="$ROOT/install/macos-arm64"
cmake --build "$BUILD_DIR" --config Release -j "$(sysctl -n hw.ncpu)"
cmake --install "$BUILD_DIR" --config Release
