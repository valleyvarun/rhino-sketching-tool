#!/bin/bash
# Run the extracted postinstall script with directory-service, console and sudo fixtures.
# The real env -i, shell, tar and filesystem operations run as this ordinary user.
set -eu
source_script="$1"
fixture="$2/account"
tools="$fixture/system"
archive="$fixture/package"
home="$fixture/Rhino User"
other="$fixture/Other User"
mkdir -p "$tools" "$archive" "$home" "$other"
printf '%s\n' "$home" > "$tools/home"
echo 501 > "$tools/uid"
echo 501 > "$tools/console"
echo 501 > "$tools/owner"
echo 644 > "$tools/mode"

sed -e "s@/usr/bin/id@\"$tools/id\"@g" \
    -e "s@/usr/bin/dscacheutil@\"$tools/dscacheutil\"@g" \
    -e "s@/usr/bin/stat@\"$tools/stat\"@g" \
    -e "s@/bin/launchctl@\"$tools/launchctl\"@g" \
    -e "s@/usr/bin/sudo@\"$tools/sudo\"@g" \
    "$source_script" > "$archive/postinstall"
cat > "$tools/id" <<'SH'
#!/bin/bash
if [ "$1" = -nu ] && [ "$2" = 501 ]; then echo sketcher
elif [ "$1" = -u ]; then cat "$(dirname "$0")/uid"
else exit 1; fi
SH
cat > "$tools/dscacheutil" <<'SH'
#!/bin/bash
[ "$*" = "-q user -a uid 501" ] || exit 1
printf 'name: sketcher\nuid: 501\ndir: %s\n' "$(cat "$(dirname "$0")/home")"
SH
cat > "$tools/stat" <<'SH'
#!/bin/bash
here="$(dirname "$0")"
if [ "$3" = /dev/console ]; then cat "$here/console"
elif [ "$1" = -Lf ]; then cat "$here/owner"
elif [ "$2" = '%Lp' ]; then cat "$here/mode"
else echo 0; fi
SH
cat > "$tools/launchctl" <<'SH'
#!/bin/bash
[ "$1" = "asuser" ] && [ "$2" = 501 ] || exit 70
shift 2
exec "$@"
SH
cat > "$tools/sudo" <<'SH'
#!/bin/bash
[ "$1" = -nH ] && [ "$2" = -u ] && [ "$3" = '#501' ] && [ "$4" = -- ] || exit 71
echo 501 >> "$(dirname "$0")/delegated"
shift 4
exec "$@"
SH
chmod +x "$tools/id" "$tools/stat" "$tools/dscacheutil" "$tools/sudo" "$tools/launchctl"
cat > "$archive/engine.sh" <<'SH'
#!/bin/bash
[ "$EUID" -ne 0 ] && [ -z "$CONTAMINATED" ] || exit 72
[ -r "$(dirname "$0")/crayon.yak" ] || exit 73
printf '%s\n%s\n%s\n' "$USER" "$HOME" "$PWD" > "$HOME/ran"
SH
echo fixture > "$archive/crayon.yak"
run() { HOME="$other" CONTAMINATED=1 bash "$archive/postinstall" "$archive/outer.pkg" "$1" "$2"; }
matches() {
  local recorded="$home/ran"
  [ "$(sed -n '1p' "$recorded")" = sketcher ] &&
    [ "$(sed -n '2p' "$recorded")" = "$home" ] &&
    [ "$(sed -n '3p' "$recorded")" -ef "$home" ] &&
    [ ! -e "$other/ran" ]
}
refused() {
  rm -f "$home/ran"
  if run "$1" "$2"; then echo "Incorrectly accepted $1 $2" >&2; exit 1; fi
  [ ! -e "$home/ran" ] && [ ! -e "$other/ran" ]
}
run / "$home"
matches
[ ! -e "$tools/delegated" ]
echo "  ok    currentUserHome uses the installing user's registered home and a clean environment"
refused / "$other"
echo "  ok    rejects a different user's target"
refused "$other" /
echo "  ok    rejects another user's installation location"
echo 502 > "$tools/owner"
refused / /
echo "  ok    checks ownership of the home returned by directory services"
echo 501 > "$tools/owner"
echo 0 > "$tools/uid"
run / "$home"
matches
[ "$(cat "$tools/delegated")" = 501 ]
[ -z "$(ls -A "$home/Library/Caches/Crayon")" ]
echo "  ok    elevated Installer delegates before extracting and running the worker"
refused / "$other"
echo "  ok    elevated Installer rejects a target different from the console user"
echo 666 > "$tools/mode"
refused / /
echo "  ok    elevated Installer refuses mutable package resources"
echo 644 > "$tools/mode"
echo 0 > "$tools/console"
refused / /
echo "  ok    root without a signed-in user launches no installer"
