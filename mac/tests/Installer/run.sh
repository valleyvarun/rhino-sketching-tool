#!/bin/bash
#
# Run the package scripts with a fixture Rhino, Yak and CLI publishers.
#   bash mac/tests/Installer/run.sh <folder containing extracted postinstall, engine.sh and Yak>
# Run as an ordinary account, as in Apple's currentUserHome install domain.

STAGED="${1:-}"
if [ -z "$STAGED" ] || [ ! -f "$STAGED/engine.sh" ] || [ ! -f "$STAGED/postinstall" ]; then
  echo "Give the folder that holds the built \"engine.sh\" (see mac/scripts/Build.ps1)." >&2
  exit 2
fi
STAGED="$(cd "$STAGED" && pwd)"
INSTALLER="$STAGED/engine.sh"
PACKAGE="$(ls "$STAGED"/*.yak 2>/dev/null | head -n 1)"
VERSION="$(sed -n 's/^VERSION="\(.*\)"$/\1/p' "$INSTALLER" | head -n 1)"
if [ -z "$PACKAGE" ] || [ -z "$VERSION" ]; then echo "The staged folder has no package, or the installer no version." >&2; exit 2; fi

if [ "$EUID" = 0 ]; then
  bash "$INSTALLER" 2>&1 | grep -q "Open the Crayon installer in Finder" || exit 1
  echo "Root execution correctly refused. Run the fixture as an ordinary user for the installation checks." >&2
  exit 2
fi

ROOT="$(mktemp -d)" || exit 1
trap 'rm -rf "$ROOT"' EXIT
passed=0
failed=0
world=0

# A fresh pretend Mac.
new_world() {
  world=$((world + 1))
  W="$ROOT/mac$world"
  RHINO="$W/Applications/Rhino 8.app"
  PACKAGES="$W/home/Library/Application Support/McNeel/Rhinoceros/packages/8.0"
  mkdir -p "$W/home" "$W/bin" "$W/state" "$W/tmp" "$RHINO/Contents/Resources/bin" "$RHINO/Contents/MacOS" "$PACKAGES"

  # Rhino's Yak: lists what is in the package folder, installs a .yak file by unpacking it
  # to <name>/<version> with a manifest.txt naming the version, and "downloads" by name.
  cat > "$RHINO/Contents/Resources/bin/yak" <<'EOF'
#!/bin/bash
case "$1" in
  version) echo "Yak 0.15.2 (0.15.1.1), Rhino $(cat "$FAKE_STATE/rhino-version" 2>/dev/null || echo 8.99) (stand-in)" ;;
  list)
    echo "Package directory: $(cat "$FAKE_STATE/foreign-root" 2>/dev/null || echo "$FAKE_PACKAGES")"
    echo
    for folder in "$FAKE_PACKAGES"/*/; do
      [ -f "$folder/manifest.txt" ] && echo "$(basename "$folder") ($(tr -d '\r\n' < "$folder/manifest.txt"))"
    done ;;
  install)
    shift
    what=""
    for argument in "$@"; do case "$argument" in --*) ;; *) what="$argument" ;; esac; done
    echo "yak install $what" >> "$FAKE_STATE/yak-installs"
    if [ -f "$what" ]; then
      [ -f "$FAKE_STATE/yak-refuses-files" ] && { echo "Yak could not install the package." >&2; exit 1; }
      base="$(basename "$what" .yak)"; name="${base%%-[0-9]*}"; rest="${base#"$name"-}"; version="${rest%-rh*}"
      mkdir -p "$FAKE_PACKAGES/$name/$version" && unzip -o -q "$what" -d "$FAKE_PACKAGES/$name/$version" || exit 1
      if [ ! -f "$FAKE_STATE/yak-keeps-active-version" ]; then
        printf '%s\n' "$version" > "$FAKE_PACKAGES/$name/manifest.txt" || exit 1
      fi
      if [ -f "$FAKE_STATE/corrupt-install" ]; then
        echo damaged > "$FAKE_PACKAGES/$name/$version/RhinoSketchingTool.rhp"
      fi
      echo "Successfully installed $name ($version)"
    else
      [ -f "$FAKE_STATE/no-mcp-for-this-mac" ] && { echo "No compatible distribution found." >&2; exit 1; }
      mkdir -p "$FAKE_PACKAGES/$what/0.4.0" && printf '0.4.0\n' > "$FAKE_PACKAGES/$what/manifest.txt" && echo "Successfully installed $what (0.4.0)"
    fi ;;
esac
EOF
  # Is Rhino running? Only if the test says so: for good ("rhino-is-open"), or for the next
  # few times it is asked ("rhino-open-checks" holds how many), as when somebody then quits it.
  cat > "$W/bin/ps" <<'EOF'
#!/bin/bash
rhino_open() { echo "$(cat "$FAKE_STATE/rhino-path")/Contents/MacOS/Rhinoceros"; }
[ -f "$FAKE_STATE/rhino-is-open" ] && { rhino_open; exit 0; }
if [ -f "$FAKE_STATE/rhino-open-checks" ]; then
  left="$(cat "$FAKE_STATE/rhino-open-checks")"
  if [ "$left" -gt 0 ]; then echo $((left - 1)) > "$FAKE_STATE/rhino-open-checks"; rhino_open; fi
fi
EOF
  (cd -P "$RHINO" && pwd) > "$W/state/rhino-path"
  printf '#!/bin/bash\necho "open $*" >> "$FAKE_STATE/opened"\n' > "$W/bin/open"
  printf '#!/bin/bash\necho unexpected >> "$FAKE_STATE/xattr"\nexit 1\n' > "$W/bin/xattr"
  printf '#!/bin/bash\n[ -f "$FAKE_STATE/rhino-path" ] && cat "$FAKE_STATE/rhino-path"\n' > "$W/bin/mdfind"
  printf '#!/bin/bash\nexit 1\n' > "$W/bin/pgrep"
  printf '#!/bin/bash\necho 14.5\n' > "$W/bin/sw_vers"
  printf '#!/bin/bash\n[ "$2" = sysctl.proc_translated ] && cat "$FAKE_STATE/translated" 2>/dev/null\n' > "$W/bin/sysctl"
  cat > "$W/bin/uname" <<'EOF'
#!/bin/bash
case "$1" in
  -s) echo Darwin ;;
  -m) cat "$FAKE_STATE/arch" 2>/dev/null || echo arm64 ;;
  *) /usr/bin/uname "$@" ;;
esac
EOF
  cat > "$W/bin/shlock" <<'EOF'
#!/bin/bash
[ "$1" = -p ] && [ "$3" = -f ] && (set -C; echo "$2" > "$4")
EOF
  # "defaults read <file without .plist> <key>": the string on the line after the key's.
  cat > "$W/bin/defaults" <<'EOF'
#!/bin/bash
[ "$1" = read ] || exit 1
sed -n "/<key>$3<\/key>/{n;s/.*<string>\(.*\)<\/string>.*/\1/p;}" "$2.plist" 2>/dev/null | head -n 1 | grep .
EOF
  # The publishers' installers: each puts its program in ~/.local/bin, unless the test is "offline".
  cat > "$W/bin/curl" <<'EOF'
#!/bin/bash
url=""; out=""
while [ $# -gt 0 ]; do
  case "$1" in -o) shift; out="$1" ;; http*) url="$1" ;; esac
  shift
done
echo "curl $url" >> "$FAKE_STATE/downloads"
[ -f "$FAKE_STATE/offline" ] && exit 6
case "$url" in
  *claude*) name=claude ;;
  *codex*) name=codex ;;
  *) exit 22 ;;
esac
printf '#!/bin/sh\nmkdir -p "$HOME/.local/bin"\nprintf "#!/bin/sh\\necho %s 1.0\\n" > "$HOME/.local/bin/%s"\nchmod +x "$HOME/.local/bin/%s"\necho "installed %s"\n' "$name" "$name" "$name" "$name" > "$out"
EOF
  chmod +x "$RHINO/Contents/Resources/bin/yak" "$W/bin/"*
}

# Runs the installer in the pretend Mac, with nobody at the keyboard. Sets OUTPUT and STATUS.
setup() {
  OUTPUT="$(cd "$W" && env -i HOME="$W/home" PATH="$W/bin:/usr/bin:/bin:/usr/bin/core_perl" TMPDIR="$W/tmp" \
    CRAYON_SETUP_TEST_ROOT="$W/system" FAKE_PACKAGES="$PACKAGES" FAKE_STATE="$W/state" \
    bash "${1:-$INSTALLER}" < /dev/null 2>&1)"
  STATUS=$?
}

check() {
  local what="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    passed=$((passed + 1)); echo "  ok    $what"
  else
    failed=$((failed + 1)); echo "  FAIL  $what"
    echo "$OUTPUT" | sed 's/^/        | /'
  fi
}
says() { printf '%s\n' "$OUTPUT" | grep -q -F -- "$1"; }
silent_on() { ! says "$1"; }
status_is() { [ "$STATUS" = "$1" ]; }
installed() { [ -f "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp" ] && [ "$(tr -d '\r\n' < "$PACKAGES/crayon-rhino/manifest.txt")" = "$VERSION" ]; }
not_installed() { [ ! -e "$PACKAGES/crayon-rhino" ]; }
same_as_package() { [ "$(unzip -p "$PACKAGE" RhinoSketchingTool.rhp | shasum -a 256 | cut -d' ' -f1)" = "$(shasum -a 256 "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp" | cut -d' ' -f1)" ]; }
ran() { grep -q -F -- "$1" "$W/state/$2" 2>/dev/null; }
never_ran() { ! ran "$1" "$2"; }
untouched() { [ ! -f "$W/state/yak-installs" ] && [ ! -f "$W/state/downloads" ]; }

echo "Installer: $INSTALLER"
echo "Package:   $(basename "$PACKAGE") (version $VERSION)"

echo
echo "A Mac with Rhino 8 and nothing else"
new_world
setup
check "finishes without error" status_is 0
check "Crayon is in Rhino's package folder, as the version Rhino loads" installed
check "the installed plug-in is the one in the package" same_as_package
check "RhinoMCP was installed through Yak" [ -f "$PACKAGES/Rhino-MCP-Platform/manifest.txt" ]
check "Claude Code was installed with its installer" [ -x "$W/home/.local/bin/claude" ]
check "Codex was installed with its installer" [ -x "$W/home/.local/bin/codex" ]
check "says it is installed" says "Crayon $VERSION is installed."
check "with nobody at the keyboard, Rhino is not opened" [ ! -f "$W/state/opened" ]
check "a log was kept" [ -n "$(ls "$W/home/Library/Logs/Crayon/" 2>/dev/null)" ]
check "the user cache staging folder was cleared" [ -z "$(ls "$W/home/Library/Caches/Crayon" 2>/dev/null)" ]
check "preserves macOS quarantine metadata" [ ! -f "$W/state/xattr" ]

echo
echo "Setup run again on the same Mac"
rm -f "$W/state/downloads" "$W/state/yak-installs"
echo "left over from before" > "$PACKAGES/crayon-rhino/$VERSION/stale-file.txt"
setup
check "finishes without error" status_is 0
check "reuses RhinoMCP" says "RhinoMCP 0.4.0 is already installed."
check "reuses Claude Code and Codex" says "Codex is already installed"
check "downloads nothing" [ ! -f "$W/state/downloads" ]
check "does not ask Yak for RhinoMCP again" never_ran "Rhino-MCP-Platform" yak-installs
check "replaces the installed version's files (nothing stale left)" [ ! -e "$PACKAGES/crayon-rhino/$VERSION/stale-file.txt" ]
check "Crayon is still the version Rhino loads" installed

echo
echo "An older Crayon and an older RhinoMCP are installed"
new_world
mkdir -p "$PACKAGES/crayon-rhino/0.1.0" "$PACKAGES/Rhino-MCP-Platform/0.3.0"
echo old > "$PACKAGES/crayon-rhino/0.1.0/RhinoSketchingTool.rhp"; echo 0.1.0 > "$PACKAGES/crayon-rhino/manifest.txt"
echo 0.3.0 > "$PACKAGES/Rhino-MCP-Platform/manifest.txt"
setup
check "finishes without error" status_is 0
check "the new Crayon is the one Rhino loads" installed
check "RhinoMCP is updated" says "Updating it."
check "RhinoMCP is now 0.4.0" [ "$(tr -d '\r\n' < "$PACKAGES/Rhino-MCP-Platform/manifest.txt")" = 0.4.0 ]

echo
echo "The previous Mac beta has a higher version number than the release"
new_world
prior="1.0.0-beta.2"
mkdir -p "$PACKAGES/crayon-rhino/$prior"
echo beta > "$PACKAGES/crayon-rhino/$prior/RhinoSketchingTool.rhp"
echo "$prior" > "$PACKAGES/crayon-rhino/manifest.txt"
touch "$W/state/yak-keeps-active-version"
setup
check "finishes despite Yak retaining the preview as active" status_is 0
check "Rhino's manifest selects the bundled release" installed
check "the active plug-in matches the bundled release" same_as_package
check "previous files are preserved" grep -qx beta "$PACKAGES/crayon-rhino/$prior/RhinoSketchingTool.rhp"

echo
echo "Rhino is open"
new_world
touch "$W/state/rhino-is-open"
setup
check "stops" status_is 1
check "asks for Rhino to be closed" says "Rhino is open."
check "installs nothing" not_installed
check "does not touch RhinoMCP or the assistants either" untouched

echo
echo "RhinoMCP fails on a supported Mac"
new_world
touch "$W/state/no-mcp-for-this-mac"
setup
check "reports an installation failure to Installer" status_is 1
check "shows the dependency and recovery instructions" says "RhinoMCP could not be installed"
check "leaves the current Crayon intact" not_installed
rm -f "$W/state/no-mcp-for-this-mac"
setup
check "reuses completed work on retry" installed

echo
echo "An Intel Mac can sketch even when Yak advertises an incompatible RhinoMCP build"
new_world
echo x86_64 > "$W/state/arch"
echo 0 > "$W/state/translated"
setup
check "recognizes the hardware" says "on this Intel Mac"
check "skips incompatible RhinoMCP" never_ran "Rhino-MCP-Platform" yak-installs
check "installs Crayon" installed
echo 1 > "$W/state/translated"
setup
check "a translated terminal on Apple silicon can install RhinoMCP" ran "Rhino-MCP-Platform" yak-installs

echo
echo "A Rhino 8 from before 8.17"
new_world
echo 8.12 > "$W/state/rhino-version"
setup
check "stops before installing" status_is 1
check "explains which Rhino update to get" says "Update to Rhino 8.17 or later"
check "leaves the Mac alone" untouched

echo
echo "An up-to-date Rhino 8"
new_world
echo 8.20 > "$W/state/rhino-version"
setup
check "finishes without error" status_is 0
check "says nothing about Rhino's version" silent_on "only show in"
check "nothing is left to look at" silent_on "still need a look"

echo
echo "Rhino 8.19 has the toolbar but ships with .NET 7"
new_world
echo 8.19 > "$W/state/rhino-version"
setup
check "explains the MCP runtime requirement" says "For model-connected chat"
check "identifies the update needed for model-connected chat" says "update Rhino 8.19 to 8.20"
check "skips the incompatible model connector" never_ran "Rhino-MCP-Platform" yak-installs
check "installs Crayon for sketching" installed

echo
echo "Rhino says which version it is itself (its Info.plist), and Yak something else"
new_world
plist() {
  printf '<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n<plist version="1.0"><dict>\n<key>CFBundleShortVersionString</key>\n<string>%s</string>\n</dict></plist>\n' "$1" > "$RHINO/Contents/Info.plist"
}
plist 8.9.24194.18122
setup
check "an old Rhino is known by what it says of itself" says "This is Rhino 8.9.24194.18122."
check "stops before installing" status_is 1
plist 8.21.25188.17002
echo 8.3 > "$W/state/rhino-version"
setup
check "a new one likewise, whatever Yak says" silent_on "only show in"
plist 8
setup
check "with no full version there, Yak's word is taken" says "This is Rhino 8.3."

echo
echo "Selecting an application for a different major version"
new_world
plist 9.0
setup
check "rejects Rhino 9" status_is 1
check "identifies the selected version" says "This is Rhino 9.0."
check "leaves it untouched" untouched

echo
echo "No internet for the assistants' installers"
new_world
touch "$W/state/offline"
setup
check "a supported assistant failure reaches Installer" status_is 1
check "gives the user a retry action" says "Claude Code's installer could not be downloaded"
check "preserves completed dependency installs" [ -f "$PACKAGES/Rhino-MCP-Platform/manifest.txt" ]
rm -f "$W/state/offline"
setup
check "retry finishes after a connectivity failure" installed

echo
echo "Yak will not install a package file"
new_world
touch "$W/state/yak-refuses-files"
setup
check "finishes without error" status_is 0
check "Crayon is put in the package folder all the same, as the version Rhino loads" installed
check "the installed plug-in is the one in the package" same_as_package

echo
echo "Yak writes a damaged replacement"
new_world
mkdir -p "$PACKAGES/crayon-rhino/$VERSION"
echo original > "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp"
echo "$VERSION" > "$PACKAGES/crayon-rhino/manifest.txt"
touch "$W/state/corrupt-install"
setup
check "reports failure" status_is 1
check "restores the original files" grep -qx original "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp"
check "restores the original active version" installed
rm -f "$W/state/corrupt-install"
setup
check "a retry succeeds" same_as_package

echo
echo "Two copies of setup target the same installation"
new_world
mkdir -p "$PACKAGES/crayon-rhino/$VERSION"
echo original > "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp"
echo "$VERSION" > "$PACKAGES/crayon-rhino/manifest.txt"
echo other > "$PACKAGES/crayon-rhino/.crayon-install.lock"
setup
check "the second setup stops" status_is 1
check "identifies the other setup" says "Another Crayon setup"
check "leaves its active files alone" grep -qx original "$PACKAGES/crayon-rhino/$VERSION/RhinoSketchingTool.rhp"

echo
echo "A graphical install has no interactive questions"
new_world
setup
check "installs Claude without text input" [ -x "$W/home/.local/bin/claude" ]
check "Codex is installed" [ -x "$W/home/.local/bin/codex" ]
check "does not request terminal input" silent_on "[Y/n]"
check "keeps Rhino closed" [ ! -f "$W/state/opened" ]

echo
echo "No Rhino 8"
new_world
rm -f "$W/state/rhino-path"
setup
check "stops" status_is 1
check "gives a Finder-friendly recovery" says "Rhino 8 for Mac was not found"
check "installs nothing" untouched

echo
echo "Yak points to another account"
new_world
echo "$ROOT/someone-else" > "$W/state/foreign-root"
setup
check "refuses the foreign package destination" status_is 1
check "explains how to recover" says "per-user package location"
check "no files go into the other home" [ ! -e "$ROOT/someone-else" ]
check "the installers were never invoked" untouched

echo
echo "The download is damaged"
new_world
BROKEN="$ROOT/broken"
mkdir -p "$BROKEN"
cp "$PACKAGE" "$BROKEN/"
cp "$INSTALLER" "$BROKEN/"
printf 'damaged' >> "$BROKEN/$(basename "$PACKAGE")"
setup "$BROKEN/engine.sh"
check "stops" status_is 1
check "says to download it again" says "Download it again."
check "installs nothing" not_installed
rm -f "$BROKEN"/*.yak
setup "$BROKEN/engine.sh"
check "a package gone missing from the folder stops it too" status_is 1
check "gives the recovery action" says "Download it again"

echo
account_results="$(bash "$(dirname "$0")/account.sh" "$STAGED/postinstall" "$ROOT" 2>&1)"
account_status=$?
printf '%s\n' "$account_results"
[ "$account_status" -eq 0 ] || exit 1
account_checks="$(printf '%s\n' "$account_results" | grep -c '^  ok    ')"
passed=$((passed + account_checks))

echo
echo "Not a Mac"
if [ "$(uname -s)" = Darwin ]; then
  echo "  (skipped: this is a Mac)"
else
  new_world
  OUTPUT="$(cd "$W" && env -i HOME="$W/home" PATH="/usr/bin:/bin" bash "$INSTALLER" < /dev/null 2>&1)"; STATUS=$?
  check "refuses to run" status_is 1
  check "and points at the Windows setup" says "CrayonSetup.exe"
fi

echo
echo "$passed passed, $failed failed"
[ "$failed" = 0 ]
