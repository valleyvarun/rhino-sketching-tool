#!/bin/bash
# Exercise the scripts from the actual website download in the installer's test fixture.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
pkg="${1:-$here/../dist/CrayonSetup.pkg}"
expected_version="${2:-$(tr -d '\r\n' < "$here/../../release.version.txt")}"
pkg="$(cd "$(dirname "$pkg")" && pwd)/$(basename "$pkg")"
test -f "$pkg" || { echo "Missing installer: $pkg" >&2; exit 1; }
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
product="$scratch/product"

case "$(uname -s)" in
  Darwin)
    reader=/usr/bin/tar
    /usr/sbin/pkgutil --expand-full "$pkg" "$product"
    # Ask Apple's Installer to read the product. This information request runs no scripts.
    /usr/sbin/installer -pkginfo -pkg "$pkg"
    ;;
  *)
    case "$(uname -s)" in
      MINGW*|MSYS*|CYGWIN*) reader="$(cygpath -u "$WINDIR")/System32/tar.exe" ;;
      *) reader=bsdtar ;;
    esac
    mkdir "$product"
    "$reader" -xf "$pkg" -C "$product"
    ;;
esac

test -s "$product/Distribution"
test -s "$product/Crayon.pkg/PackageInfo"
test -d "$product/Resources"
scripts="$product/Crayon.pkg/Scripts"
if [ -f "$scripts" ]; then
  mkdir "$scratch/scripts"
  "$reader" -xf "$scripts" -C "$scratch/scripts"
  scripts="$scratch/scripts"
fi
test -x "$scripts/postinstall"
test -s "$scripts/engine.sh"
set -- "$scripts"/*.yak
test "$#" = 1 && test -s "$1" || { echo "Expected a single bundled Rhino package." >&2; exit 1; }
test "$(basename "$1")" = "crayon-rhino-$expected_version-rh8_0-mac.yak"
grep -Fx "VERSION=\"$expected_version\"" "$scripts/engine.sh" >/dev/null
unzip -p "$1" manifest.yml | tr -d '\r' | grep -Fx "version: $expected_version" >/dev/null
echo "Verified the installer archive and bundled Rhino package $expected_version."
bash "$here/Installer/run.sh" "$scripts"
