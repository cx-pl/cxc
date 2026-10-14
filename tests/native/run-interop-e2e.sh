#!/bin/sh
# Generate with run-interop-e2e.ps1 -GenerateOnly, then run this in Linux.
set -eu
root=${1:?Pass the generated fixture root}
core=${2:?Pass the cxcore source directory}
compiler=${3:-gcc}
cmake=${CMAKE:-cmake}
static=${CX_STATIC_LINK:-ON}
for fixture in native-abi native-text native-scalar; do
  source="$root/$fixture"
  if [ "$fixture" = native-scalar ]; then source="$source/app"; fi
  build="$root/$fixture/build-$compiler-$static"
  "$cmake" -S "$source/.obj" -B "$build" -DCXCORE_SOURCE_DIR="$core" -DCMAKE_C_COMPILER="$compiler" -DCX_STATIC_LINK="$static"
  "$cmake" --build "$build" -j4
  case "$fixture" in
    native-abi) executable=native_abi;;
    native-text) executable=native_text;;
    native-scalar) executable=native_scalar_app;;
  esac
  timeout 90 "$source/.bin/$executable"
done
for negative in signature layout field; do
  source="$root/bad-$negative"
  build="$source/build-$compiler-$static"
  checks="$source/sdk-checks-linux"
  mkdir -p "$checks"
  cat >"$checks/CMakeLists.txt" <<EOF
cmake_minimum_required(VERSION 3.31)
project(sdk_check C)
add_library(bad_native OBJECT "$source/.obj/bad_native.c")
target_include_directories(bad_native PRIVATE "$source" "$core/src" "$core/inc" "$core/platform")
target_compile_features(bad_native PRIVATE c_std_11)
target_compile_definitions(bad_native PRIVATE CX_STATIC_LINK CX_BAD_NATIVE_BUILD)
EOF
  "$cmake" -S "$checks" -B "$source/check-$compiler-$static" -DCMAKE_C_COMPILER="$compiler"
  if "$cmake" --build "$source/check-$compiler-$static" -j4 >"$source/build.log" 2>&1; then
    echo "Bad SDK $negative unexpectedly passed" >&2; exit 1
  fi
  grep -E 'SDK .*mismatch|SDK signature/ABI mismatch' "$source/build.log" >/dev/null
  echo "Rejected incompatible SDK $negative"
done
