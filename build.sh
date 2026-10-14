#!/usr/bin/env bash
# Builds ShvdnLegacyCompat.asi on Linux (or WSL / MSYS2) with the .NET SDK and MinGW-w64.
#   Requirements: dotnet SDK 8+, x86_64-w64-mingw32-g++ and x86_64-w64-mingw32-windres.
#   Usage: ./build.sh [--test]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/build"
MANAGED_OUT="$OUT/managed"
CXX="${CXX:-x86_64-w64-mingw32-g++}"
WINDRES="${WINDRES:-x86_64-w64-mingw32-windres}"

rm -rf "$OUT"
mkdir -p "$MANAGED_OUT"

echo "==> Building managed patcher (net48) and shims"
dotnet build "$ROOT/src/Managed/Patcher/ShvdnCompat.Patcher.csproj" -c Release -f net48 -nologo -v q
cp "$ROOT/src/Managed/Patcher/bin/Release/net48/ShvdnCompat.Patcher.exe" "$MANAGED_OUT/"
cp "$ROOT/src/Managed/Patcher/bin/Release/net48/Mono.Cecil.dll" "$MANAGED_OUT/"

if [[ "${1:-}" == "--test" ]]; then
  echo "==> Running tests"
  dotnet test "$ROOT/tests/ShvdnCompat.Tests/ShvdnCompat.Tests.csproj" -nologo -v q
fi

echo "==> Building ShvdnLegacyCompat.asi"
"$WINDRES" -I "$MANAGED_OUT" -I "$ROOT/src/Native" -O coff -o "$OUT/resource.o" "$ROOT/src/Native/resource.rc"
"$CXX" -std=c++17 -O2 -Wall -Wextra -shared -static -static-libgcc -static-libstdc++ \
  -DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN -DNOMINMAX \
  -o "$OUT/ShvdnLegacyCompat.asi" \
  "$ROOT"/src/Native/*.cpp "$OUT/resource.o" \
  -lbcrypt -Wl,--subsystem,windows -s

if [[ "${1:-}" == "--test" ]]; then
  echo "==> Building native test programs (run them on Windows or with wine64)"
  mkdir -p "$OUT/tests"
  NATIVE_FLAGS=(-std=c++17 -O2 -static -municode -DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN -I"$ROOT/src/Native" -I"$ROOT/tests/native")
  "$CXX" "${NATIVE_FLAGS[@]}" "$ROOT/tests/native/redirect_harness.cpp" "$ROOT/src/Native/redirect.cpp" \
    "$ROOT/src/Native/log.cpp" -o "$OUT/tests/redirect_harness.exe"
  "$CXX" "${NATIVE_FLAGS[@]}" "$ROOT/tests/native/e2e_host.cpp" "$ROOT/tests/native/clr_host.cpp" -o "$OUT/tests/e2e_host.exe"
  dotnet build "$ROOT/tests/native/BindingChecker/BindingChecker.csproj" -c Release -nologo -v q
  dotnet build "$ROOT/tests/LegacyScriptFixture/LegacyScriptFixture.csproj" -c Release -nologo -v q
  cp "$ROOT/tests/native/BindingChecker/bin/Release/net48/BindingChecker.dll" "$OUT/tests/"
  cp "$ROOT/tests/LegacyScriptFixture/bin/Release/net48/LegacyScriptFixture.dll" "$OUT/tests/"
fi

echo "==> Done: $OUT/ShvdnLegacyCompat.asi"
