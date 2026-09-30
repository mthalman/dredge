#!/usr/bin/env bash
# Statically checks a Native AOT publish directory without running the executable.
# Usage: check-native-aot-output.sh <publish-directory> <runtime-identifier>
set -euo pipefail

publish_dir="$1"
rid="$2"

fail() {
  echo "::error::$*" >&2
  exit 1
}

hex_bytes() {
  od -An -tx1 -v -j "$2" -N "$3" "$1" | tr -d ' \n'
}

# Reads a little-endian field and returns it as big-endian hex.
le_hex() {
  local bytes reversed=""
  bytes="$(hex_bytes "$1" "$2" "$3")"
  for ((i = ${#bytes} - 2; i >= 0; i -= 2)); do
    reversed+="${bytes:i:2}"
  done
  echo "$reversed"
}

case "$rid" in
  win-*) exe="$publish_dir/dredge.exe" ;;
  *) exe="$publish_dir/dredge" ;;
esac

[ -f "$exe" ] || fail "Expected executable '$exe' was not produced."

managed_files="$(find "$publish_dir" \( -name '*.dll' -o -name '*.runtimeconfig.json' -o -name '*.deps.json' \) -print)"
[ -z "$managed_files" ] || fail "Native AOT output for $rid contains managed runtime files: $managed_files"

case "$rid" in
  *-x64) arch=x64 ;;
  *-arm64) arch=arm64 ;;
  *) fail "Unsupported runtime identifier '$rid'." ;;
esac

case "$rid" in
  win-*)
    [ "$(hex_bytes "$exe" 0 2)" = "4d5a" ] || fail "$exe is not a PE executable."
    pe_offset=$((16#$(le_hex "$exe" 60 4)))
    [ "$(hex_bytes "$exe" "$pe_offset" 4)" = "50450000" ] || fail "$exe has an invalid PE header."
    machine="$(le_hex "$exe" $((pe_offset + 4)) 2)"
    expected_machine=$([ "$arch" = x64 ] && echo 8664 || echo aa64)
    ;;
  osx-*)
    [ "$(le_hex "$exe" 0 4)" = "feedfacf" ] || fail "$exe is not a 64-bit Mach-O executable."
    machine="$(le_hex "$exe" 4 4)"
    expected_machine=$([ "$arch" = x64 ] && echo 01000007 || echo 0100000c)
    ;;
  linux-*)
    [ "$(hex_bytes "$exe" 0 5)" = "7f454c4602" ] || fail "$exe is not a 64-bit ELF executable."
    machine="$(le_hex "$exe" 18 2)"
    expected_machine=$([ "$arch" = x64 ] && echo 003e || echo 00b7)

    if [[ "$rid" == linux-musl-* ]]; then
      expected_loader="ld-musl-"
      unexpected_loader="ld-linux-"
    else
      expected_loader="ld-linux-"
      unexpected_loader="ld-musl-"
    fi

    LC_ALL=C grep -aqF "$expected_loader" "$exe" || fail "$exe does not reference the $expected_loader dynamic loader expected for $rid."
    if LC_ALL=C grep -aqF "$unexpected_loader" "$exe"; then
      fail "$exe references the $unexpected_loader dynamic loader, which does not match $rid."
    fi
    ;;
  *)
    fail "Unsupported runtime identifier '$rid'."
    ;;
esac

[ "$machine" = "$expected_machine" ] || fail "$exe targets machine type 0x$machine, expected 0x$expected_machine for $rid."

echo "Verified Native AOT executable for $rid: $exe"
