#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
source_file="$repository_root/Assets/Plugins/iOS/StreamingMeshAppleAudio.mm"
output_file="$repository_root/Assets/Plugins/macOS/StreamingMeshAppleAudio.dylib"

mkdir -p "$(dirname -- "$output_file")"
xcrun clang++ \
  -std=c++17 \
  -fobjc-arc \
  -fblocks \
  -dynamiclib \
  -arch arm64 \
  -arch x86_64 \
  -mmacosx-version-min=12.0 \
  -framework Foundation \
  -framework AVFoundation \
  -framework CoreMedia \
  -install_name @rpath/StreamingMeshAppleAudio.dylib \
  "$source_file" \
  -o "$output_file"

lipo -info "$output_file"
