#!/usr/bin/env bash
# Runs the native tests under Wine (needs wine64 + wine-mono): the redirect hook harness, then the ASI end to
# end in a fake game folder (SHVDN nightly API + a script built against the legacy API).
# Build first with: ./build.sh --test
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD="$ROOT/build"
WINE="${WINE:-$(command -v wine64 || echo /usr/lib/wine/wine64)}"
export WINEDEBUG="${WINEDEBUG:--all}"

GAME="$(mktemp -d)"
trap 'rm -rf "${GAME:?}"' EXIT
mkdir -p "$GAME/scripts"
cp "$BUILD/ShvdnLegacyCompat.asi" "$BUILD/tests/e2e_host.exe" "$BUILD/tests/BindingChecker.dll" "$GAME/"
cp "$BUILD/tests/LegacyScriptFixture.dll" "$GAME/scripts/"
cp "$ROOT/ref/nightly/ScriptHookVDotNet2.dll" "$ROOT/ref/nightly/ScriptHookVDotNet3.dll" "$GAME/"

failures=0
expect() { # name, expected binding failures, host args...
  local name="$1" want="$2"; shift 2
  rm -f "$GAME/binding-result.txt"
  (cd "$GAME" && "$WINE" e2e_host.exe "$@" >/dev/null 2>&1) || true
  local got
  got="$(tr -d '\r' < "$GAME/binding-result.txt" 2>/dev/null | sed -n 's/.*failures: \([0-9]*\)$/\1/p' || true)"
  if [[ "$got" == "$want" ]]; then echo "PASS $name (binding failures: $got)"; else echo "FAIL $name (expected $want, got '${got}')"; failures=$((failures + 1)); fi
  sleep 3 # let the background scan of the previous run finish writing
}

echo "== redirect hook harness"
"$WINE" "$BUILD/tests/redirect_harness.exe" 2>/dev/null || failures=$((failures + 1))

echo "== end to end"
expect "baseline without the ASI fails to bind" 14 --no-asi
expect "cold cache, SHVDN loads immediately" 0 --no-wait
expect "warm cache, SHVDN loads immediately" 0 --no-wait
rm -rf "${GAME:?}/ShvdnLegacyCompat"
expect "cold cache, normal start" 0
sed -i 's/^Mode=.*/Mode=Disk/' "$GAME/ShvdnLegacyCompat.ini"
expect "Mode=Disk" 0
sed -i 's/^Mode=.*/Mode=Restore/' "$GAME/ShvdnLegacyCompat.ini"
expect "Mode=Restore puts the original back" 14
cmp -s "$GAME/ScriptHookVDotNet3.dll" "$ROOT/ref/nightly/ScriptHookVDotNet3.dll" && echo "PASS original API bytes restored" || { echo "FAIL original API not restored"; failures=$((failures + 1)); }
sed -i 's/^Mode=.*/Mode=Redirect/' "$GAME/ShvdnLegacyCompat.ini"
expect "back to Mode=Redirect" 0

echo
if [[ $failures -eq 0 ]]; then echo "ALL WINE TESTS PASSED"; else echo "$failures WINE TEST(S) FAILED"; exit 1; fi
