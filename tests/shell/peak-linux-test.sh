#!/usr/bin/env bash
#
# Tests for scripts/peak-linux.sh.
#
# The suite is self contained: it builds a fake Steam installation in a
# temporary directory and runs the script against it. No game and no network
# are needed.
#
# Usage: tests/shell/peak-linux-test.sh
#
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd -P)"
PEAK_LINUX="$REPO_ROOT/scripts/peak-linux.sh"

PASS=0
FAIL=0
CURRENT=""

SANDBOX=""

cleanup() {
    if [ -n "$SANDBOX" ] && [ -d "$SANDBOX" ]; then
        chmod -R u+rwX "$SANDBOX" 2>/dev/null || true
        rm -rf "$SANDBOX"
    fi
}
trap cleanup EXIT

start_case() {
    CURRENT="$1"
    SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/peakapi-test-XXXXXX")"
    printf '\n--- %s\n' "$CURRENT"
}

pass() {
    PASS=$((PASS + 1))
    printf '  ok   %s\n' "$1"
}

fail() {
    FAIL=$((FAIL + 1))
    printf '  FAIL %s\n' "$1"
    if [ -n "${2:-}" ]; then
        printf '       %s\n' "$2"
    fi
}

assert_ok() {
    local desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        pass "$desc"
    else
        fail "$desc" "command failed: $*"
    fi
}

assert_fails() {
    local desc="$1"
    shift
    if "$@" >/dev/null 2>&1; then
        fail "$desc" "command unexpectedly succeeded: $*"
    else
        pass "$desc"
    fi
}

assert_contains() {
    local desc="$1" haystack="$2" needle="$3"
    case "$haystack" in
        *"$needle"*) pass "$desc" ;;
        *) fail "$desc" "expected to find '$needle' in: $haystack" ;;
    esac
}

assert_not_contains() {
    local desc="$1" haystack="$2" needle="$3"
    case "$haystack" in
        *"$needle"*) fail "$desc" "did not expect '$needle' in: $haystack" ;;
        *) pass "$desc" ;;
    esac
}

assert_equal() {
    local desc="$1" expected="$2" actual="$3"
    if [ "$expected" = "$actual" ]; then
        pass "$desc"
    else
        fail "$desc" "expected '$expected', got '$actual'"
    fi
}

assert_file_exists() {
    local desc="$1" path="$2"
    if [ -e "$path" ]; then
        pass "$desc"
    else
        fail "$desc" "missing: $path"
    fi
}

assert_file_absent() {
    local desc="$1" path="$2"
    if [ ! -e "$path" ]; then
        pass "$desc"
    else
        fail "$desc" "should not exist: $path"
    fi
}

# --------------------------------------------------------------------------
# Fake Steam installations
# --------------------------------------------------------------------------

# A minimal 64 bit PE, so the architecture check passes.
make_fake_pe() {
    local path="$1"
    mkdir -p "$(dirname "$path")"
    # e_lfanew (at 0x3c) points at 0x40; the PE signature is 4 bytes, so the
    # COFF machine field lives at 0x44.
    printf '\x00%.0s' {1..60} >"$path"
    printf '\x40\x00\x00\x00' >>"$path"
    printf '\x00\x00\x00\x00' >>"$path"
    printf '\x64\x86' >>"$path"
    printf '\x00%.0s' {1..200} >>"$path"
}

make_steam_root() {
    local root="$1"
    mkdir -p "$root/steamapps/common"
    printf '"libraryfolders"\n{\n}\n' >"$root/steamapps/libraryfolders.vdf"
}

make_peak_install() {
    local lib="$1" name="${2:-PEAK}"
    local dir="$lib/steamapps/common/$name"
    mkdir -p "$dir/PEAK_Data/Plugins/x86_64" "$dir/BepInEx/core" "$dir/BepInEx/plugins" "$dir/BepInEx/config"
    make_fake_pe "$dir/PEAK.exe"
    printf '2.6.b' >"$dir/version.txt"
    printf 'fake eos' >"$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"
}

# --------------------------------------------------------------------------
# Tests
# --------------------------------------------------------------------------

test_detects_a_native_steam_root() {
    start_case "detects a native Steam root"
    local root="$SANDBOX/Steam"
    make_steam_root "$root"
    make_peak_install "$root"

    local out
    out="$("$PEAK_LINUX" detect --steam-root "$root" 2>&1)"
    assert_contains "the game directory is reported" "$out" "$root/steamapps/common/PEAK"
    assert_contains "the PE architecture is verified" "$out" "64 bit PE"
}

test_detects_a_flatpak_steam_root() {
    start_case "detects a Flatpak Steam root"
    local root="$SANDBOX/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    make_steam_root "$root"
    make_peak_install "$root"

    local out
    out="$(HOME="$SANDBOX" "$PEAK_LINUX" detect 2>&1)"
    assert_contains "the Flatpak game directory is found" "$out" "$root/steamapps/common/PEAK"
}

test_detects_an_extra_library_folder() {
    start_case "detects an extra library folder"
    local root="$SANDBOX/Steam"
    local extra="$SANDBOX/Games Library With Spaces"
    make_steam_root "$root"
    make_peak_install "$extra"

    cat >"$root/steamapps/libraryfolders.vdf" <<EOF
"libraryfolders"
{
	"0"
	{
		"path"		"$root"
	}
	"1"
	{
		"path"		"$extra"
	}
}
EOF

    local out
    out="$("$PEAK_LINUX" detect --steam-root "$root" 2>&1)"
    assert_contains "the extra library is used" "$out" "$extra/steamapps/common/PEAK"
}

test_detects_the_new_libraryfolders_layout() {
    start_case "detects the new libraryfolders.vdf layout"
    local root="$SANDBOX/Steam"
    local extra="$SANDBOX/extra"
    make_steam_root "$root"
    make_peak_install "$extra"

    cat >"$root/steamapps/libraryfolders.vdf" <<EOF
"libraryfolders"
{
	"0"
	{
		"path"		"$root"
		"label"		""
		"contentid"		"1"
		"apps"
		{
			"3527290"		"1"
		}
	}
	"1"
	{
		"path"		"$extra"
		"label"		"extra"
		"contentid"		"2"
		"apps"
		{
			"3527290"		"1"
		}
	}
}
EOF

    local out
    out="$("$PEAK_LINUX" detect --steam-root "$root" 2>&1)"
    assert_contains "the extra library is used" "$out" "$extra/steamapps/common/PEAK"
}

test_follows_symlinked_steam_roots() {
    start_case "follows a symlinked Steam root"
    local real="$SANDBOX/real steam"
    local link="$SANDBOX/link"
    make_steam_root "$real"
    make_peak_install "$real"
    ln -s "$real" "$link"

    local out
    out="$("$PEAK_LINUX" detect --steam-root "$link" 2>&1)"
    assert_contains "the real path is used" "$out" "$real/steamapps/common/PEAK"
}

test_rejects_a_missing_game_directory() {
    start_case "rejects a missing game directory"
    assert_fails "the command fails" "$PEAK_LINUX" detect --game-dir "$SANDBOX/nope"
}

test_rejects_a_32_bit_game() {
    start_case "rejects a 32 bit game"
    local dir="$SANDBOX/game"
    mkdir -p "$dir"
    printf '\x00%.0s' {1..60} >"$dir/PEAK.exe"
    printf '\x40\x00\x00\x00' >>"$dir/PEAK.exe"
    printf '\x00\x00\x00\x00' >>"$dir/PEAK.exe"
    printf '\x4c\x01' >>"$dir/PEAK.exe"

    local out
    out="$("$PEAK_LINUX" detect --game-dir "$dir" 2>&1)"
    assert_contains "the error names the architecture" "$out" "32 bit PE"
}

test_status_reports_the_installation() {
    start_case "status reports the installation"
    local dir="$SANDBOX/game"
    make_peak_install "$SANDBOX"
    dir="$SANDBOX/steamapps/common/PEAK"
    printf 'proxy' >"$dir/winhttp.dll"
    printf '[General]\nenabled = true\n' >"$dir/doorstop_config.ini"

    local out
    out="$("$PEAK_LINUX" status --game-dir "$dir" 2>&1)"
    assert_contains "the proxy is reported" "$out" "winhttp.dll (proxy)"
    assert_contains "the doorstop state is reported" "$out" "doorstop enabled"
    assert_contains "the EOS state is reported" "$out" "EOSSDK-Win64-Shipping.dll"
}

test_status_warns_that_a_disabled_proxy_is_not_vanilla() {
    start_case "status warns that a disabled proxy is not a vanilla baseline"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'proxy' >"$dir/winhttp.dll"
    printf '[General]\nenabled = false\n' >"$dir/doorstop_config.ini"

    local out
    out="$("$PEAK_LINUX" status --game-dir "$dir" 2>&1)"
    assert_contains "the warning is printed" "$out" "NOT a vanilla baseline"
}

test_backup_and_restore_round_trip() {
    start_case "backup and restore round trip"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'original proxy' >"$dir/winhttp.dll"
    printf 'original eos' >"$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"
    mkdir -p "$SANDBOX/plugins"
    printf 'plugin' >"$SANDBOX/plugins/com.github.nglmercer.PEAKapi.dll"
    printf 'core' >"$SANDBOX/plugins/PeakRestApi.Core.dll"
    printf 'new proxy' >"$SANDBOX/proxy.dll"

    assert_ok "the install succeeds" "$PEAK_LINUX" install --game-dir "$dir" \
        --plugin-dir "$SANDBOX/plugins" --proxy "$SANDBOX/proxy.dll" --adopt-proxy
    assert_equal "the proxy was installed" "new proxy" "$(cat "$dir/winhttp.dll")"

    assert_ok "the restore succeeds" "$PEAK_LINUX" restore --game-dir "$dir"
    assert_equal "the proxy is restored" "original proxy" "$(cat "$dir/winhttp.dll")"
    assert_file_exists "the EOS SDK is restored" "$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"
    assert_file_absent "the plugin is removed" "$dir/BepInEx/plugins/com.github.nglmercer.PEAKapi.dll"
}

test_restore_is_idempotent() {
    start_case "restore is idempotent"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'original proxy' >"$dir/winhttp.dll"
    mkdir -p "$SANDBOX/plugins"
    printf 'plugin' >"$SANDBOX/plugins/com.github.nglmercer.PEAKapi.dll"
    printf 'core' >"$SANDBOX/plugins/PeakRestApi.Core.dll"
    printf 'new proxy' >"$SANDBOX/proxy.dll"

    "$PEAK_LINUX" install --game-dir "$dir" --plugin-dir "$SANDBOX/plugins" \
        --proxy "$SANDBOX/proxy.dll" --adopt-proxy >/dev/null
    "$PEAK_LINUX" restore --game-dir "$dir" >/dev/null
    local out
    out="$("$PEAK_LINUX" restore --game-dir "$dir" 2>&1)"
    assert_contains "the second restore succeeds" "$out" "restore complete"
    assert_equal "the file is unchanged" "original proxy" "$(cat "$dir/winhttp.dll")"
}

test_restore_refuses_to_overwrite_a_changed_file() {
    start_case "restore refuses to overwrite a changed file"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'original proxy' >"$dir/winhttp.dll"
    mkdir -p "$SANDBOX/plugins"
    printf 'plugin' >"$SANDBOX/plugins/com.github.nglmercer.PEAKapi.dll"
    printf 'core' >"$SANDBOX/plugins/PeakRestApi.Core.dll"
    printf 'new proxy' >"$SANDBOX/proxy.dll"

    "$PEAK_LINUX" install --game-dir "$dir" --plugin-dir "$SANDBOX/plugins" \
        --proxy "$SANDBOX/proxy.dll" --adopt-proxy >/dev/null
    printf 'someone else changed this' >"$dir/winhttp.dll"

    local out
    out="$("$PEAK_LINUX" restore --game-dir "$dir" 2>&1)"
    assert_contains "the refusal is reported" "$out" "changed after the backup"
    assert_equal "the file is left alone" "someone else changed this" "$(cat "$dir/winhttp.dll")"

    out="$("$PEAK_LINUX" restore --game-dir "$dir" --force 2>&1)"
    assert_equal "--force overwrites the file" "original proxy" "$(cat "$dir/winhttp.dll")"
}

test_dry_run_changes_nothing() {
    start_case "dry run changes nothing"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'original proxy' >"$dir/winhttp.dll"

    local out
    out="$("$PEAK_LINUX" backup --game-dir "$dir" --dry-run 2>&1)"
    assert_contains "the dry run is announced" "$out" "dry-run"
    assert_file_absent "no manifest was written" "$dir/.peakapi-backup/manifest.tsv"
    assert_equal "the file is unchanged" "original proxy" "$(cat "$dir/winhttp.dll")"
}

test_eos_disable_and_restore() {
    start_case "the EOS fallback disables and restores"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    local eos="$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"
    local digest
    digest="$(sha256sum "$eos" | awk '{print $1}')"

    assert_ok "the disable succeeds" "$PEAK_LINUX" eos-disable --game-dir "$dir" --yes
    assert_file_absent "the EOS SDK is gone" "$eos"
    assert_file_exists "the disabled copy is there" "$eos.disabled"

    assert_ok "the restore succeeds" "$PEAK_LINUX" eos-restore --game-dir "$dir"
    assert_file_exists "the EOS SDK is back" "$eos"
    assert_equal "the content is the same" "$digest" "$(sha256sum "$eos" | awk '{print $1}')"
}

test_eos_disable_without_an_eos_sdk() {
    start_case "the EOS fallback reports a missing EOS SDK"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    rm -f "$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"

    local out
    out="$("$PEAK_LINUX" eos-disable --game-dir "$dir" --yes 2>&1)"
    assert_contains "the error is reported" "$out" "no EOS SDK"
}

test_eos_restore_detects_a_changed_file() {
    start_case "the EOS restore detects a changed file"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    local eos="$dir/PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"

    "$PEAK_LINUX" backup --game-dir "$dir" >/dev/null
    mv "$eos" "$eos.disabled"
    printf 'tampered' >"$eos.disabled"

    local out
    out="$("$PEAK_LINUX" eos-restore --game-dir "$dir" 2>&1)"
    assert_contains "the mismatch is reported" "$out" "does not match"
    assert_file_absent "the file is not moved back" "$eos"
}

test_install_refuses_to_overwrite_an_unknown_proxy() {
    start_case "install refuses to overwrite an unknown proxy"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'somebody elses proxy' >"$dir/winhttp.dll"

    local out
    out="$("$PEAK_LINUX" install --game-dir "$dir" --plugin-dir "$SANDBOX/plugins" 2>&1)"
    assert_contains "the refusal is reported" "$out" "unknown third party proxy"
    assert_equal "the proxy is untouched" "somebody elses proxy" "$(cat "$dir/winhttp.dll")"
}

test_install_writes_the_launch_option() {
    start_case "install writes the Steam launch option"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    mkdir -p "$SANDBOX/plugins"
    printf 'plugin' >"$SANDBOX/plugins/com.github.nglmercer.PEAKapi.dll"
    printf 'core' >"$SANDBOX/plugins/PeakRestApi.Core.dll"

    local out
    out="$("$PEAK_LINUX" install --game-dir "$dir" --plugin-dir "$SANDBOX/plugins" 2>&1)"
    assert_contains "the launch option is printed" "$out" 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
    assert_file_exists "the plugin was installed" "$dir/BepInEx/plugins/com.github.nglmercer.PEAKapi.dll"
    assert_file_exists "the doorstop config was written" "$dir/doorstop_config.ini"
}

test_verify_detects_a_stale_log() {
    start_case "verify detects a stale log"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'proxy' >"$dir/winhttp.dll"
    printf '[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n' >"$dir/doorstop_config.ini"
    printf 'BepInEx\nPEAKapi\n' >"$dir/BepInEx/LogOutput.log"
    touch -d '2 days ago' "$dir/BepInEx/LogOutput.log"

    local out
    out="$("$PEAK_LINUX" verify --game-dir "$dir" 2>&1)"
    assert_contains "the stale log is reported" "$out" "stale"
}

test_verify_accepts_a_fresh_log() {
    start_case "verify accepts a fresh log"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'proxy' >"$dir/winhttp.dll"
    printf '[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n' >"$dir/doorstop_config.ini"
    printf 'BepInEx\nPEAKapi\n' >"$dir/BepInEx/LogOutput.log"

    local out
    out="$("$PEAK_LINUX" verify --game-dir "$dir" 2>&1)"
    assert_not_contains "the log is not called stale" "$out" "stale"
}

test_verify_reports_a_missing_loader() {
    start_case "verify reports a missing loader"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"

    local out
    out="$("$PEAK_LINUX" verify --game-dir "$dir" 2>&1)"
    assert_contains "the missing proxy is reported" "$out" "winhttp.dll"
    assert_contains "the missing log is reported" "$out" "LogOutput.log"
}

test_diagnose_redacts_the_token_and_the_paths() {
    start_case "diagnose redacts the token and the paths"
    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf '[HTTP]\nAuthenticationToken = SUPERSECRETTOKENVALUE1234567890\n' >"$dir/BepInEx/config/org.peak.rest-api.cfg"
    local out="$SANDBOX/report.txt"

    assert_ok "the report is written" "$PEAK_LINUX" diagnose --game-dir "$dir" --out "$out"
    local body
    body="$(cat "$out")"
    assert_not_contains "the token is not in the report" "$body" "SUPERSECRETTOKENVALUE1234567890"
    assert_contains "the redaction marker is used" "$body" "<redacted>"
    assert_not_contains "the home directory is not in the report" "$body" "$SANDBOX"
}

test_permission_failure_is_reported() {
    start_case "a permission failure is reported"
    if [ "$(id -u)" -eq 0 ]; then
        printf '  skip (running as root, the permission check is meaningless)\n'
        return
    fi

    local dir="$SANDBOX/steamapps/common/PEAK"
    make_peak_install "$SANDBOX"
    printf 'proxy' >"$dir/winhttp.dll"
    chmod 555 "$dir"

    local out
    out="$("$PEAK_LINUX" backup --game-dir "$dir" 2>&1)"
    assert_contains "the failure is reported" "$out" "backup directory"

    chmod 755 "$dir"
}

# --------------------------------------------------------------------------
# Runner
# --------------------------------------------------------------------------

main() {
    printf 'peak-linux.sh test suite\n'

    test_detects_a_native_steam_root
    test_detects_a_flatpak_steam_root
    test_detects_an_extra_library_folder
    test_detects_the_new_libraryfolders_layout
    test_follows_symlinked_steam_roots
    test_rejects_a_missing_game_directory
    test_rejects_a_32_bit_game
    test_status_reports_the_installation
    test_status_warns_that_a_disabled_proxy_is_not_vanilla
    test_backup_and_restore_round_trip
    test_restore_is_idempotent
    test_restore_refuses_to_overwrite_a_changed_file
    test_dry_run_changes_nothing
    test_eos_disable_and_restore
    test_eos_disable_without_an_eos_sdk
    test_eos_restore_detects_a_changed_file
    test_install_refuses_to_overwrite_an_unknown_proxy
    test_install_writes_the_launch_option
    test_verify_detects_a_stale_log
    test_verify_accepts_a_fresh_log
    test_verify_reports_a_missing_loader
    test_diagnose_redacts_the_token_and_the_paths
    test_permission_failure_is_reported

    printf '\n%s passed, %s failed\n' "$PASS" "$FAIL"
    [ "$FAIL" -eq 0 ]
}

main "$@"
