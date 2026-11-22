#!/bin/bash
#
# Internal worker of the Crayon installer package. postinstall supplies the installing
# user's verified HOME before any Yak or publisher code is started.
if [ "$EUID" -eq 0 ]; then
  echo "Open the Crayon installer in Finder from the Mac account that uses Rhino." >&2
  exit 1
fi
umask 077

# Filled in when the download is built (mac/scripts/Build.ps1).
VERSION="@VERSION@"
PACKAGE_FILE="@PACKAGE_FILE@"
PACKAGE_SHA256="@PACKAGE_SHA256@"
PLUGIN_SHA256="@PLUGIN_SHA256@"

PACKAGE_NAME="crayon-rhino"
PLUGIN_FILE="RhinoSketchingTool.rhp"
RHINO_MINIMUM="8.17" # before that, Rhino for Mac does not draw Crayon's toolbar over the viewport
MCP_PACKAGE="Rhino-MCP-Platform"
MCP_MINIMUM="0.4.0"
PACKAGE_SERVER="https://yak.rhino3d.com"
CLAUDE_INSTALLER="https://claude.ai/install.sh"
CODEX_INSTALLER="https://chatgpt.com/codex/install.sh"

HERE="$(cd "$(dirname "$0")" 2>/dev/null && pwd)"
RHINO=""
RHINO_VERSION=""
CAPABILITY_NOTE=""
WORK=""
LOCK=""
TRANSACTION=""
INSTALL_TARGET=""
INSTALL_STATE=preparing
LOG=/dev/null

# ------------------------------------------------------------------ saying

say()  { printf '%s\n' "$*"; printf '%s\n' "$*" >> "$LOG"; }
note() { printf '%s\n' "$*" >> "$LOG"; }               # for the log only
step() { say ""; say "$*"; }
show_message() {
  # Installer's failure page shows no script details. Display the recovery instructions
  # in the signed-in user's GUI when Installer.app is open. The same text goes in its log.
  pgrep -x -U "$EUID" Installer >/dev/null 2>&1 || return 0
  /usr/bin/osascript - "$1" "$2" <<'APPLESCRIPT' >/dev/null 2>&1 || true
on run args
    display alert (item 1 of args) message (item 2 of args) buttons {"OK"} default button "OK" giving up after 90
end run
APPLESCRIPT
}

cleanup() {
  if [ -n "$TRANSACTION" ] && [ "$INSTALL_STATE" != complete ] &&
      { [ "$INSTALL_STATE" = installing ] || [ -d "$TRANSACTION/previous" ]; }; then
    # Only the version being replaced and Yak's active-version pointer are ours.
    # Other version folders and all user data stay in place.
    if [ -e "$INSTALL_TARGET" ]; then
      mv "$INSTALL_TARGET" "$TRANSACTION/incomplete" || return 1
    fi
    if [ -d "$TRANSACTION/previous" ]; then
      mv "$TRANSACTION/previous" "$INSTALL_TARGET" || return 1
    fi
    if [ -f "$TRANSACTION/manifest.txt" ]; then
      mv -f "$TRANSACTION/manifest.txt" "$(dirname "$INSTALL_TARGET")/manifest.txt" || return 1
    else
      rm -f "$(dirname "$INSTALL_TARGET")/manifest.txt"
    fi
  fi
  if [ -n "$TRANSACTION" ]; then rm -rf "$TRANSACTION"; fi
  if [ -n "$LOCK" ]; then rm -f "$LOCK"; fi
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then rm -rf "$WORK"; fi
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
  say ""
  say "Setup stopped: $*"
  if [ "$LOG" != /dev/null ]; then say "Details are in $LOG"; fi
  show_message "Crayon installation stopped" "$*"
  exit 1
}

# version_at_least 0.4.1 0.4.0: true when the first is the same or newer.
version_at_least() {
  local i x y
  local -a have want
  IFS=. read -r -a have <<< "$1"
  IFS=. read -r -a want <<< "$2"
  for i in 0 1 2 3; do
    x="${have[$i]:-0}"; x="${x%%[!0-9]*}"; x="${x:-0}"
    y="${want[$i]:-0}"; y="${y%%[!0-9]*}"; y="${y:-0}"
    if [ "$x" -gt "$y" ]; then return 0; fi
    if [ "$x" -lt "$y" ]; then return 1; fi
  done
  return 0
}

# ---------------------------------------------------------- Rhino and Yak

# Yak is Rhino's own package tool, inside the Rhino application.
has_yak() { [ -n "$1" ] && [ -x "$1/Contents/Resources/bin/yak" ]; }

find_rhino() {
  local candidate
  for candidate in "${CRAYON_SETUP_TEST_ROOT:-}/Applications/Rhino 8.app" "$HOME/Applications/Rhino 8.app"; do
    if has_yak "$candidate"; then RHINO="$candidate"; return 0; fi
  done
  # Anywhere else, if Spotlight knows of it.
  if command -v mdfind >/dev/null 2>&1; then
    while IFS= read -r candidate; do
      if has_yak "$candidate"; then RHINO="$candidate"; return 0; fi
    done < <(mdfind "kMDItemCFBundleIdentifier == 'com.mcneel.rhinoceros.8'" 2>/dev/null; mdfind -name "Rhino 8.app" 2>/dev/null)
  fi
  return 1
}

# Which Rhino 8 this is, such as 8.19.25132.13002 or 8.19: as the application says of itself,
# or else as its Yak does. Nothing if neither will say.
rhino_version() {
  local found=""
  if [ -f "$RHINO/Contents/Info.plist" ]; then
    found="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$RHINO/Contents/Info.plist" 2>/dev/null)"
    case "$found" in
      [0-9]*.[0-9]*) ;;
      *) found="$(defaults read "$RHINO/Contents/Info" CFBundleShortVersionString 2>/dev/null)" ;;
    esac
  fi
  case "$found" in
    [0-9]*.[0-9]*) ;;
    *) found="$("$YAK" version 2>/dev/null | sed -n 's/.*Rhino \([0-9][0-9.]*\).*/\1/p' | head -n 1)" ;;
  esac
  case "$found" in
    [0-9]*.[0-9]*) printf '%s' "$found" | tr -d '\r' ;;
  esac
}

check_rhino_version() {
  local version
  version="$(rhino_version)"
  RHINO_VERSION="$version"
  note "Rhino's version: ${version:-not known}"
  case "$version" in
    8.*) ;;
    '') fail "Setup could not identify this Rhino version. Update Rhino 8 and open the Crayon installer again." ;;
    *) fail "This is Rhino $version. Choose the Rhino 8 application." ;;
  esac
  if [ -n "$version" ] && ! version_at_least "$version" "$RHINO_MINIMUM"; then
    fail "This is Rhino $version. Update to Rhino $RHINO_MINIMUM or later so the drawing toolbar can appear, then reopen the Crayon installer."
  fi
}

# Match the executable's path literally. App paths can contain brackets or parentheses.
rhino_running() { ps -axww -o comm= | awk -v prefix="$RHINO/Contents/MacOS/" 'index($0, prefix) == 1 { found=1 } END { exit !found }'; }

require_rhino_closed() {
  if rhino_running; then
    fail "Rhino is open. Save your work, choose Quit Rhino, and open the Crayon installer again."
  fi
}

yak_list() { "$YAK" list 2>> "$LOG"; }

# installed_version NAME: the version Yak has of a package, or nothing.
installed_version() {
  yak_list | awk -v name="$1" '
    BEGIN { wanted = tolower(name) }
    tolower($1) == wanted && match($0, /\([^)]*\)/) { print substr($0, RSTART + 1, RLENGTH - 2); exit }'
}

# Where Rhino keeps this user's packages, as Yak itself says.
package_folder() {
  local folder
  folder="$(yak_list | sed -n 's/^Package directory: //p' | head -n 1 | tr -d '\r')"
  if [ -z "$folder" ]; then folder="$HOME/Library/Application Support/McNeel/Rhinoceros/packages/8.0"; fi
  printf '%s' "$folder"
}

# ---------------------------------------------------------------- RhinoMCP

ensure_mcp() {
  local have
  if [ "$(uname -m)" = x86_64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null)" != 1 ]; then
    CAPABILITY_NOTE="Drawing, comments and chat are available on this Intel Mac. The RhinoMCP model connection requires Apple silicon."
    say "$CAPABILITY_NOTE"
    return 0
  fi
  if [ -n "$RHINO_VERSION" ] && ! version_at_least "$RHINO_VERSION" 8.20; then
    CAPABILITY_NOTE="Drawing, comments and chat are available. For model-connected chat, update Rhino $RHINO_VERSION to 8.20 or later and reopen the installer."
    say "$CAPABILITY_NOTE"
    return 0
  fi
  have="$(installed_version "$MCP_PACKAGE")"
  if [ -n "$have" ] && version_at_least "$have" "$MCP_MINIMUM"; then
    say "   RhinoMCP $have is already installed."
    return 0
  fi
  if [ -n "$have" ]; then say "   RhinoMCP $have is installed. Updating it."; else say "   Installing RhinoMCP (McNeel's $MCP_PACKAGE package)."; fi
  "$YAK" install "--source=$PACKAGE_SERVER" "$MCP_PACKAGE" >> "$LOG" 2>&1
  have="$(installed_version "$MCP_PACKAGE")"
  if [ -n "$have" ] && version_at_least "$have" "$MCP_MINIMUM"; then
    say "   RhinoMCP $have is installed."
    return 0
  fi
  fail "RhinoMCP could not be installed. Check your internet connection and reopen the Crayon installer. You can also install $MCP_PACKAGE from Rhino's PackageManager, quit Rhino, and retry."
}

# ------------------------------------------------------ Claude Code, Codex

# find_program NAME [MORE PLACES...]: where a command line program is, or nothing.
find_program() {
  local name="$1" found candidate
  shift
  found="$(command -v "$name" 2>/dev/null)"
  if [ -n "$found" ] && [ -x "$found" ] && [ ! -d "$found" ]; then printf '%s' "$found"; return 0; fi
  for candidate in "$HOME/.local/bin/$name" "${CRAYON_SETUP_TEST_ROOT:-}/opt/homebrew/bin/$name" "${CRAYON_SETUP_TEST_ROOT:-}/usr/local/bin/$name" \
                   "$HOME/.npm-global/bin/$name" "$HOME/.volta/bin/$name" "$HOME/.bun/bin/$name" "$@"; do
    if [ -x "$candidate" ] && [ ! -d "$candidate" ]; then printf '%s' "$candidate"; return 0; fi
  done
  return 1
}

find_claude() { find_program claude "$HOME/.claude/local/claude"; }
find_codex()  { find_program codex "$HOME/.codex/bin/codex"; }

# run_installer LABEL URL INTERPRETER: download a publisher's installer, then run it.
run_installer() {
  local label="$1" url="$2" interpreter="$3" script status
  script="$WORK/install-$$-$RANDOM.sh"
  say "   Installing $label with its own installer: $url"
  if ! curl --proto '=https' --tlsv1.2 --connect-timeout 20 --max-time 180 --retry 2 -fsSL "$url" -o "$script" 2>> "$LOG"; then
    fail "$label's installer could not be downloaded. Check your internet connection and reopen the Crayon installer."
  fi
  "$interpreter" "$script" </dev/null 2>&1 | tee -a "$LOG" | sed 's/^/     /'
  status="${PIPESTATUS[0]}"
  note "$label installer exit code: $status"
  [ "$status" = 0 ]
}

ensure_agents() {
  local claude codex missing=""
  claude="$(find_claude)"
  codex="$(find_codex)"
  if [ -n "$claude" ]; then say "   Claude Code is already installed: $claude"; else missing="Claude Code"; fi
  if [ -n "$codex" ]; then say "   Codex is already installed: $codex"; else missing="${missing:+$missing and }Codex"; fi
  if [ -z "$missing" ]; then return 0; fi

  say "   Installing $missing for this Mac account."

  if [ -z "$claude" ]; then
    run_installer "Claude Code" "$CLAUDE_INSTALLER" bash || fail "Claude Code setup failed. Check your internet connection and reopen the Crayon installer."
    claude="$(find_claude)"
    if [ -n "$claude" ]; then say "   Claude Code is installed: $claude"
    else fail "Claude Code was not found after its setup finished. Install Claude Code from claude.com/claude-code and reopen the Crayon installer."; fi
  fi
  if [ -z "$codex" ]; then
    run_installer "Codex" "$CODEX_INSTALLER" sh || fail "Codex setup failed. Check your internet connection and reopen the Crayon installer."
    codex="$(find_codex)"
    if [ -n "$codex" ]; then say "   Codex is installed: $codex"
    else fail "Codex was not found after its setup finished. Install Codex CLI from developers.openai.com/codex/cli and reopen the Crayon installer."; fi
  fi
  return 0
}

# ------------------------------------------------------------------ Crayon

# SHA-256 of a file in capitals.
sha256() {
  if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$@"
  elif command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"
  elif command -v openssl >/dev/null 2>&1; then openssl dgst -sha256 -r "$@"
  fi 2>/dev/null | awk '{ print toupper($1); exit }'
}

check_download() {
  if [ ! -f "$PACKAGE" ]; then
    fail "The Crayon package is missing from this installer. Download it again and reopen it."
  fi
  if [ "$(sha256 "$PACKAGE")" != "$PACKAGE_SHA256" ]; then
    fail "This copy of Crayon is damaged (the download is incomplete, or was changed). Download it again."
  fi
}

install_crayon() {
  local folder installed
  folder="$packages/$PACKAGE_NAME"
  installed="$folder/$VERSION"
  note "Rhino's package folder: $packages"

  mkdir -p "$folder" || fail "Cannot write to Rhino's package folder: $packages"
  # shlock is supplied by macOS and automatically reclaims a dead process's lock.
  shlock -p "$$" -f "$folder/.crayon-install.lock" || fail "Another Crayon setup is using $folder. Close it and retry."
  LOCK="$folder/.crayon-install.lock"
  TRANSACTION="$(mktemp -d "$folder/.setup.XXXXXX")" || fail "Cannot stage Crayon in $folder."
  INSTALL_TARGET="$installed"
  if [ -e "$folder/manifest.txt" ]; then
    [ -f "$folder/manifest.txt" ] && cp "$folder/manifest.txt" "$TRANSACTION/" || fail "Cannot save the active Crayon version."
  fi
  if [ -e "$installed" ] && [ ! -d "$installed" ]; then fail "Expected a package directory at $installed."; fi
  if [ -d "$installed" ]; then
    note "Replacing the contents of installed version $VERSION"
    mv "$installed" "$TRANSACTION/previous" || fail "The installed Crayon is in use. Close Rhino and retry."
  fi
  INSTALL_STATE=installing

  if "$YAK" install "$PACKAGE" >> "$LOG" 2>&1 && [ -f "$installed/$PLUGIN_FILE" ]; then
    note "Installed with Yak."
  else
    # Yak would not: put the package where Yak would have, the way Yak does.
    note "Yak did not install the package. Unpacking it into Rhino's package folder instead."
    if [ -e "$installed" ]; then
      mv "$installed" "$TRANSACTION/partial-yak" || fail "Cannot replace the incomplete Yak installation."
    fi
    mkdir -p "$installed" >> "$LOG" 2>&1 &&
      unzip -o -q "$PACKAGE" -d "$installed" >> "$LOG" 2>&1 ||
      fail "Crayon could not be put in Rhino's package folder: $packages"
  fi

  # Rhino's next start has to load exactly the plug-in this setup carries.
  if [ ! -f "$installed/$PLUGIN_FILE" ]; then fail "Crayon's files did not arrive in $installed."; fi
  if [ "$(sha256 "$installed/$PLUGIN_FILE")" != "$PLUGIN_SHA256" ]; then
    fail "Crayon's installed files did not pass verification. Run setup again."
  fi
  # Yak can leave a higher-numbered preview active when installing a release.
  # Select the verified payload, with the previous manifest available for rollback.
  printf '%s\n' "$VERSION" > "$TRANSACTION/active" &&
    mv -f "$TRANSACTION/active" "$folder/manifest.txt" ||
    fail "Could not activate Crayon $VERSION in Rhino's package folder."
  INSTALL_STATE=complete
  INSTALLED="$installed"
}

# -------------------------------------------------------------------- main

if [ "$(uname -s)" != "Darwin" ]; then
  echo "This sets up Crayon in Rhino 8 for Mac. On Windows, use CrayonSetup.exe instead."
  exit 1
fi
case "$VERSION$PACKAGE_FILE$PACKAGE_SHA256$PLUGIN_SHA256" in
  *@*) echo "This is the template of the installer, not the installer. Build the download with mac/scripts/Build.ps1."; exit 1 ;;
esac
case "$VERSION" in
  ""|*/*|*..*|*[!0-9A-Za-z.+-]*) echo "Not a package version: $VERSION"; exit 1 ;;
esac

LOG_FOLDER="$HOME/Library/Logs/Crayon"
if mkdir -p "$LOG_FOLDER" 2>/dev/null; then
  LOG="$(mktemp "$LOG_FOLDER/setup-$(date +%Y%m%d-%H%M%S).XXXXXX")" || LOG=/dev/null
fi
CACHE="$HOME/Library/Caches/Crayon"
mkdir -p "$CACHE" || fail "Setup could not create its cache in your Library folder. Check your home folder permissions and retry."
WORK="$(mktemp -d "$CACHE/setup.XXXXXX" 2>/dev/null)" || WORK=""
if [ -z "$WORK" ]; then fail "No temporary folder could be made."; fi
PACKAGE="$HERE/$PACKAGE_FILE"

say "Crayon $VERSION for Rhino 8 (Mac)"
note "Started $(date) on macOS $(sw_vers -productVersion 2>/dev/null) $(uname -m)"
check_download

step "1. Finding Rhino 8"
if ! find_rhino; then
  fail "Rhino 8 for Mac was not found. Install Rhino 8 in Applications, open it once, quit it, and reopen the Crayon installer."
fi
RHINO="$(cd -P "$RHINO" && pwd)" || fail "Could not resolve the Rhino application path."
YAK="$RHINO/Contents/Resources/bin/yak"
say "   $RHINO"
note "$("$YAK" version 2>&1)"
check_rhino_version
require_rhino_closed
packages="$(package_folder)"
case "$packages/" in
  "$HOME"/*) ;;
  *) fail "Rhino's package location is outside this account's home folder. Select a per-user package location in Rhino and retry." ;;
esac

step "2. Connecting Rhino to your assistants (RhinoMCP)"
ensure_mcp

step "3. Assistants (Claude Code and Codex)"
ensure_agents

step "4. Installing Crayon in Rhino 8"
require_rhino_closed # it may have been opened in the meantime
install_crayon
say "   $INSTALLED"

say ""
say "Crayon $VERSION is installed."
say "Open Rhino 8 and type Crayon. The Agent tab asks once which assistant to use, and"
say "signing in to it happens there."
if [ "$LOG" != /dev/null ]; then say "A log of this setup is in $LOG"; fi

[ -z "$CAPABILITY_NOTE" ] || show_message "Crayon is installed" "$CAPABILITY_NOTE"
exit 0
