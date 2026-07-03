#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
NATIVE_DIR="$ROOT_DIR/native"
UNITY_PLUGIN_DIR="$ROOT_DIR/unity/com.pi.unity-harness/Editor/Plugins/x86_64"

cd "$NATIVE_DIR"
cargo build --release

mkdir -p "$UNITY_PLUGIN_DIR"
cp -f "$NATIVE_DIR/target/release/pi_unity_harness_native.dll" \
  "$UNITY_PLUGIN_DIR/pi_unity_harness_native.dll"

echo "native dll copied to Unity package plugin directory"
