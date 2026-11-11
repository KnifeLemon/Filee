#!/bin/sh
set -eu

# Filee's macOS standalone installer. A normal install owns ~/Applications; --upgrade replaces only the Filee bundle
# the running app identified, so an app dragged to /Applications can update after the administrator approves macOS's prompt.

source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
default_install_dir="$HOME/Applications/Filee.app"
install_dir=$default_install_dir
upgrade=false

case "${1:-}" in
    '')
        ;;
    --upgrade)
        upgrade=true
        if [ "$#" -gt 2 ]; then
            printf '%s\n' 'Usage: install.sh [--upgrade [Filee.app path]]' >&2
            exit 1
        fi
        if [ "$#" -eq 2 ]; then
            install_dir=$2
        fi
        ;;
    --help|-h)
        printf '%s\n' 'Usage: install.sh [--upgrade [Filee.app path]]'
        exit 0
        ;;
    *)
        printf '%s\n' 'Usage: install.sh [--upgrade [Filee.app path]]' >&2
        exit 1
        ;;
esac

bundle_id() {
    /usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$1/Contents/Info.plist" 2>/dev/null || true
}

is_filee_bundle() {
    [ -d "$1" ] && [ ! -L "$1" ] && [ -x "$1/Contents/MacOS/Filee" ] && [ "$(bundle_id "$1")" = 'com.filee.app' ]
}

if ! is_filee_bundle "$source_dir/Filee.app"; then
    printf '%s\n' "Payload validation failed: $source_dir/Filee.app is not a Filee application bundle." >&2
    exit 1
fi

if [ "$upgrade" = true ]; then
    # Do not let a downloaded archive replace an arbitrary app: Filee only supports its two documented locations.
    if [ "$install_dir" != "$HOME/Applications/Filee.app" ] && [ "$install_dir" != '/Applications/Filee.app' ]; then
        printf '%s\n' "Cannot upgrade an app outside the supported Applications folders: $install_dir" >&2
        exit 1
    fi
    if ! is_filee_bundle "$install_dir"; then
        printf '%s\n' "No existing Filee application bundle was found at $install_dir." >&2
        exit 1
    fi

    parent_dir=$(dirname -- "$install_dir")
    # /Applications is normally root-owned. Run precisely this verified script and target through AppleScript arguments,
    # not a concatenated shell command, so paths cannot change what receives administrator rights.
    if [ ! -w "$parent_dir" ]; then
        exec /usr/bin/osascript - "$source_dir/install.sh" "$install_dir" <<'APPLESCRIPT'
on run argv
  do shell script (quoted form of item 1 of argv) & " --upgrade " & (quoted form of item 2 of argv) with administrator privileges
end run
APPLESCRIPT
    fi

    work_dir=$(mktemp -d "$parent_dir/.filee-update.XXXXXX")
    staged_app="$work_dir/Filee.app"
    previous_app="$work_dir/previous.app"
    cleanup() { rm -rf -- "$work_dir"; }
    trap cleanup EXIT HUP INT TERM

    /usr/bin/ditto "$source_dir/Filee.app" "$staged_app"
    if ! is_filee_bundle "$staged_app"; then
        printf '%s\n' 'Staged payload validation failed.' >&2
        exit 1
    fi
    if ! mv "$install_dir" "$previous_app"; then
        printf '%s\n' "Could not prepare $install_dir for the update." >&2
        exit 1
    fi
    if ! mv "$staged_app" "$install_dir"; then
        mv "$previous_app" "$install_dir" || true
        printf '%s\n' "Could not activate the update; the previous Filee app was restored." >&2
        exit 1
    fi
    /usr/bin/xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true
    trap - EXIT HUP INT TERM
    cleanup
    printf '%s\n' "Updated $install_dir"
    exit 0
fi

bin_dir="$HOME/.local/bin"
if [ -e "$install_dir" ] || [ -L "$install_dir" ]; then
    printf '%s\n' "An installation already exists at $install_dir. Run uninstall.sh first."
    exit 1
fi
# A filee link left by an earlier Filee whose app is gone (moved to the Trash instead of uninstall.sh) is replaced.
# Anything else at that path isn't ours and stays.
stale_link=false
if [ -L "$bin_dir/filee" ] && [ ! -e "$bin_dir/filee" ]; then
    case $(readlink "$bin_dir/filee") in
        */Filee.app/Contents/MacOS/filee-cli) stale_link=true ;;
    esac
fi
if [ "$stale_link" = false ] && { [ -e "$bin_dir/filee" ] || [ -L "$bin_dir/filee" ]; }; then
    printf '%s\n' "$bin_dir/filee already exists. Remove or rename it before installing Filee."
    exit 1
fi
mkdir -p "$HOME/Applications" "$bin_dir"
/usr/bin/ditto "$source_dir/Filee.app" "$install_dir"
# ditto keeps the download's quarantine flag: macOS would then start Filee from a translocated copy and could stop
# the filee command without a word (#316).
/usr/bin/xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true
if [ "$stale_link" = true ]; then
    rm -f -- "$bin_dir/filee"
    printf '%s\n' "Replaced the filee link left by an earlier installation."
fi
ln -s "$install_dir/Contents/MacOS/filee-cli" "$bin_dir/filee"
printf '%s\n' "Installed $install_dir" "CLI: $bin_dir/filee (add $bin_dir to PATH if needed)."
printf '%s\n' 'Open Filee from Applications. Grant Accessibility and Finder Automation access when requested.'
printf '%s\n' 'If Filee is already listed and switched on under Accessibility, remove it with the - button and add it again with +.'
