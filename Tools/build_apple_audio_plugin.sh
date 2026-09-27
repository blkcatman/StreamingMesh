#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
source_file="$repository_root/Assets/Plugins/iOS/StreamingMeshAppleAudio.mm"
output_file="$repository_root/Assets/Plugins/macOS/StreamingMeshAppleAudio.dylib"
architecture=${STREAMINGMESH_MAC_ARCH:-$(uname -m)}
case "$architecture" in arm64|x86_64) ;; *) echo "Unsupported architecture: $architecture" >&2; exit 1 ;; esac

mkdir -p "$(dirname -- "$output_file")"
xcrun clang++ \
  -std=c++17 \
  -fobjc-arc \
  -fblocks \
  -dynamiclib \
  -arch "$architecture" \
  -mmacosx-version-min=12.0 \
  -framework Foundation \
  -framework AVFoundation \
  -framework CoreMedia \
  -install_name @rpath/StreamingMeshAppleAudio.dylib \
  "$source_file" \
  -o "$output_file"

file "$output_file"
