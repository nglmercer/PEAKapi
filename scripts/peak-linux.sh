#!/usr/bin/env bash
#
# peak-linux.sh - Linux/Proton loader and diagnostics helper for PEAKapi.
#
# The script never modifies a game file without a backup that can be rolled
# back, and it never guesses where PEAK is installed: the Steam library
# folders are read from libraryfolders.vdf and the game directory is verified
# before anything is touched.
#
# Every mutating command supports --dry-run, which prints what would happen
# and changes nothing.
#
# Usage: scripts/peak-linux.sh <command> [options]
#
#   detect      locate Steam, the PEAK install and the Proton prefix
#   status      show what is installed and injected right now
#   diagnose    write a redacted diagnostic report
#   verify      check that the injection works (files, log freshness, API)
#   backup      back up the files the tests change, with SHA-256
#   restore     restore the files from the backup manifest (idempotent)
#   install     install the BepInEx loader and the PEAKapi plugin
#   uninstall   remove PEAKapi and restore the original state
#   eos-disable temporarily disable the EOS SDK (opt-in, degraded mode)
#   eos-restore restore the EOS SDK
#
# Options:
#   --dry-run          do not change anything, only report
#   --game-dir DIR     use this game directory instead of the detected one
#   --steam-root DIR   use this Steam root instead of the detected one
#   --prefix DIR       use this Proton prefix instead of the detected one
#   --proxy FILE       the doorstop proxy (winhttp.dll) to install
#   --bepinex FILE     the BepInEx pack (zip) to install
#   --plugin-dir DIR   the directory with the built PEAKapi DLLs
#   --adopt-proxy      adopt an already installed proxy instead of refusing
#   --force            allow overwriting files that changed behind our back
#   --yes              do not ask for confirmation
#   --out FILE         the output file of the diagnose command
#
set -euo pipefail

APPID=3527290
GAME_DIR_NAME=PEAK
GAME_EXE=PEAK.exe
MANIFEST_VERSION=1
BACKUP_DIR_NAME=.peakapi-backup

DRY_RUN=0
GAME_DIR_OVERRIDE=""
STEAM_ROOT_OVERRIDE=""
PREFIX_OVERRIDE=""
PROXY_FILE=""
BEPINEX_FILE=""
PLUGIN_DIR=""
ADOPT_PROXY=0
FORCE=0
ASSUME_YES=0
OUT_FILE=""

# --------------------------------------------------------------------------
# Output helpers
# --------------------------------------------------------------------------

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RESET=$'\033[0m'
    C_INFO=$'\033[0;36m'
    C_WARN=$'\033[0;33m'
    C_ERR=$'\033[0;31m'
    C_OK=$'\033[0;32m'
else
    C_RESET=""
    C_INFO=""
    C_WARN=""
    C_ERR=""
    C_OK=""
fi

info() { printf '%s==>%s %s\n' "$C_INFO" "$C_RESET" "$*"; }
ok() { printf '%s  ok%s %s\n' "$C_OK" "$C_RESET" "$*"; }
warn() { printf '%swarn%s %s\n' "$C_WARN" "$C_RESET" "$*" >&2; }
err() { printf '%sfail%s %s\n' "$C_ERR" "$C_RESET" "$*" >&2; }
die() { err "$*"; exit 1; }

run() {
    if [ "$DRY_RUN" -eq 1 ]; then
        printf '  [dry-run] %s\n' "$*"
        return 0
    fi
    "$@"
}

die_on_error() {
    if [ "$DRY_RUN" -eq 1 ]; then
        printf '  [dry-run] %s\n' "$*"
        return 0
    fi
    "$@" || die "failed: $*"
}

confirm() {
    if [ "$ASSUME_YES" -eq 1 ]; then
        return 0
    fi
    if [ ! -t 0 ]; then
        die "$1 (refusing without --yes on a non interactive terminal)"
    fi
    printf '%s [y/N] ' "$1"
    read -r answer
    case "$answer" in
        [yY] | [yY][eE][sS]) return 0 ;;
        *) return 1 ;;
    esac
}

usage() {
    sed -n '3,40p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

# --------------------------------------------------------------------------
# Small utilities
# --------------------------------------------------------------------------

sha256_of() {
    if [ -f "$1" ]; then
        sha256sum -- "$1" | awk '{print $1}'
    else
        printf 'missing\n'
    fi
}

now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# Takes the first line of a value that was captured before. Piping into
# "head -1" would close the pipe early and kill the producer with SIGPIPE,
# which "set -o pipefail" would then turn into a failure of the whole script.
first_line() {
    printf '%s' "${1%%$'\n'*}"
}

file_mtime() {
    if [ -e "$1" ]; then
        stat -c %Y -- "$1" 2>/dev/null || stat -f %m -- "$1" 2>/dev/null || printf '0\n'
    else
        printf '0\n'
    fi
}

human_age() {
    local then="$1" now delta
    now="$(date +%s)"
    delta=$((now - then))
    if [ "$delta" -lt 0 ]; then
        printf 'in the future\n'
    elif [ "$delta" -lt 90 ]; then
        printf '%ss ago\n' "$delta"
    elif [ "$delta" -lt 5400 ]; then
        printf '%sm ago\n' "$((delta / 60))"
    elif [ "$delta" -lt 172800 ]; then
        printf '%sh ago\n' "$((delta / 3600))"
    else
        printf '%sd ago\n' "$((delta / 86400))"
    fi
}

# --------------------------------------------------------------------------
# Steam, library and prefix discovery
# --------------------------------------------------------------------------

# Prints every candidate Steam root, most specific first.
steam_root_candidates() {
    if [ -n "$STEAM_ROOT_OVERRIDE" ]; then
        printf '%s\n' "$STEAM_ROOT_OVERRIDE"
        return
    fi

    local candidates=()
    [ -n "${STEAM_DIR:-}" ] && candidates+=("$STEAM_DIR")
    [ -n "${XDG_DATA_HOME:-}" ] && candidates+=("$XDG_DATA_HOME/Steam")
    candidates+=("$HOME/.local/share/Steam")
    candidates+=("$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam")
    candidates+=("$HOME/.var/app/com.valvesoftware.Steam/data/Steam")
    candidates+=("$HOME/.steam/root")
    candidates+=("$HOME/.steam/steam")
    candidates+=("$HOME/.steam/debian-installation")

    local c
    for c in "${candidates[@]}"; do
        [ -n "$c" ] || continue
        if [ -d "$c/steamapps" ]; then
            printf '%s\n' "$c"
        fi
    done
}

# Prints every Steam library folder, reading libraryfolders.vdf.
#
# Both the old (indexed) and the new (with an "apps" sub table) layout are
# supported. Paths may contain spaces and may be symlinks.
library_roots() {
    local root
    while IFS= read -r root; do
        [ -n "$root" ] || continue
        printf '%s\n' "$root"

        local vdf="$root/steamapps/libraryfolders.vdf"
        [ -f "$vdf" ] || continue

        # The path values are quoted and may contain backslashes on some
        # installations, so both separators are accepted.
        sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf" \
            | sed 's/\\$//' \
            | while IFS= read -r lib; do
                [ -n "$lib" ] || continue
                [ -d "$lib" ] || continue
                printf '%s\n' "$lib"
            done
    done < <(steam_root_candidates) | awk '!seen[$0]++'
}

# Resolves the PEAK game directory. Sets GAME_DIR and GAME_LIB_ROOT.
detect_game_dir() {
    if [ -n "$GAME_DIR_OVERRIDE" ]; then
        [ -d "$GAME_DIR_OVERRIDE" ] || die "the game directory '$GAME_DIR_OVERRIDE' does not exist"
        GAME_DIR="$(cd "$GAME_DIR_OVERRIDE" && pwd -P)"
        GAME_LIB_ROOT="$(dirname "$(dirname "$GAME_DIR")")"
        return
    fi

    local lib candidate
    while IFS= read -r lib; do
        candidate="$lib/steamapps/common/$GAME_DIR_NAME"
        if [ -f "$candidate/$GAME_EXE" ]; then
            GAME_DIR="$(cd "$candidate" && pwd -P)"
            GAME_LIB_ROOT="$(cd "$lib" && pwd -P)"
            return
        fi
    done < <(library_roots)

    die "PEAK was not found in any Steam library. Use --game-dir to point at the folder that holds $GAME_EXE."
}

# Resolves the Proton prefix of the game. Sets PREFIX_DIR.
detect_prefix() {
    if [ -n "$PREFIX_OVERRIDE" ]; then
        [ -d "$PREFIX_OVERRIDE" ] || die "the prefix '$PREFIX_OVERRIDE' does not exist"
        PREFIX_DIR="$(cd "$PREFIX_OVERRIDE" && pwd -P)"
        return
    fi

    local lib candidate
    while IFS= read -r lib; do
        candidate="$lib/steamapps/compatdata/$APPID/pfx"
        if [ -d "$candidate" ]; then
            PREFIX_DIR="$(cd "$candidate" && pwd -P)"
            return
        fi
    done < <(library_roots)

    # The prefix is created on the first start of the game, so it may not
    # exist yet. Report the expected location instead of failing.
    PREFIX_DIR=""
}

# Reads a little endian unsigned integer of $2 bytes at offset $1 from $3 and
# prints it as a hex string. A PE header is always little endian, whatever the
# host is, so the bytes are reversed by hand.
read_le() {
    local offset="$1" size="$2" file="$3" hex i rev=""
    hex="$(od -An -tx1 -j "$offset" -N "$size" -- "$file" 2>/dev/null | tr -d ' \n')"
    if [ "${#hex}" -ne $((size * 2)) ]; then
        return 1
    fi

    for ((i = size - 1; i >= 0; i--)); do
        rev="$rev${hex:$((i * 2)):2}"
    done

    printf '%s' "$rev"
}

verify_game_binary() {
    local exe="$GAME_DIR/$GAME_EXE"
    [ -f "$exe" ] || die "'$GAME_EXE' is missing in '$GAME_DIR'. This is not a PEAK installation."

    local off_hex off machine
    off_hex="$(read_le 60 4 "$exe")" || die "'$GAME_EXE' could not be read"
    off=$((16#$off_hex))
    machine="$(read_le $((off + 4)) 2 "$exe")" || die "'$GAME_EXE' has no PE header"

    case "$machine" in
        8664) ok "$GAME_EXE is a 64 bit PE (machine 0x$machine)" ;;
        014c) die "$GAME_EXE is a 32 bit PE. PEAKapi needs the 64 bit game." ;;
        *) die "$GAME_EXE has an unexpected machine type 0x$machine" ;;
    esac
}

# --------------------------------------------------------------------------
# Backup manifest
# --------------------------------------------------------------------------

BACKUP_DIR=""
MANIFEST=""

backup_init() {
    BACKUP_DIR="$GAME_DIR/$BACKUP_DIR_NAME"
    MANIFEST="$BACKUP_DIR/manifest.tsv"
}

# manifest_lookup <relpath> -> prints "sha256<TAB>origin" or nothing.
manifest_lookup() {
    [ -f "$MANIFEST" ] || return 0
    awk -F'\t' -v p="$1" '$1 == "file" && $2 == p { print $3 "\t" $4; exit }' "$MANIFEST"
}

manifest_has() {
    [ -f "$MANIFEST" ] || return 1
    awk -F'\t' -v p="$1" '$1 == "file" && $2 == p { found = 1 } END { exit found ? 0 : 1 }' "$MANIFEST"
}

# The digest of the file as it is right after PEAKapi installed it.
manifest_installed() {
    [ -f "$MANIFEST" ] || return 0
    awk -F'\t' -v p="$1" '$1 == "installed" && $2 == p { print $3; exit }' "$MANIFEST"
}

# The file a PEAKapi operation moved away from its original place.
manifest_moved() {
    [ -f "$MANIFEST" ] || return 0
    awk -F'\t' -v p="$1" '$1 == "moved" && $2 == p { print $3 "\t" $4; exit }' "$MANIFEST"
}

# manifest_append <relpath> <sha256> <origin>
manifest_append() {
    [ "$DRY_RUN" -eq 1 ] && return 0
    mkdir -p "$BACKUP_DIR/files" 2>/dev/null || true
    printf 'file\t%s\t%s\t%s\n' "$1" "$2" "$3" >>"$MANIFEST"
}

# Records the digest of a file PEAKapi just wrote, so that the restore can
# tell "we changed this" from "somebody else changed this".
manifest_record_installed() {
    local rel="$1" digest="$2" tmp
    [ "$DRY_RUN" -eq 1 ] && return 0
    if [ -f "$MANIFEST" ]; then
        # Replace an earlier row for the same file.
        tmp="$MANIFEST.tmp"
        awk -F'\t' -v p="$rel" '!($1 == "installed" && $2 == p)' "$MANIFEST" >"$tmp"
        mv -f -- "$tmp" "$MANIFEST"
    fi
    printf 'installed\t%s\t%s\n' "$rel" "$digest" >>"$MANIFEST"
}

# Records that a file was moved to another place inside the game folder.
manifest_record_moved() {
    local rel="$1" digest="$2" new_rel="$3"
    [ "$DRY_RUN" -eq 1 ] && return 0
    printf 'moved\t%s\t%s\t%s\n' "$rel" "$digest" "$new_rel" >>"$MANIFEST"
}

# Stores a copy of <src> under the content addressed backup store.
backup_store() {
    local src="$1" digest="$2"
    [ "$DRY_RUN" -eq 1 ] && return 0
    mkdir -p "$BACKUP_DIR/files" 2>/dev/null \
        || die "could not create the backup directory '$BACKUP_DIR/files'"
    [ -f "$BACKUP_DIR/files/$digest" ] || cp -p -- "$src" "$BACKUP_DIR/files/$digest"
}

# backup_file <abs path> - backs up a file that lives inside the game dir.
backup_file() {
    local abs="$1" rel digest
    rel="${abs#"$GAME_DIR"/}"

    if manifest_has "$rel"; then
        return 0
    fi

    if [ ! -e "$abs" ]; then
        manifest_append "$rel" "absent" "did not exist"
        return 0
    fi

    digest="$(sha256_of "$abs")"
    backup_store "$abs" "$digest"
    manifest_append "$rel" "$digest" "$abs"
    info "backed up $rel (sha256 $digest)"
}

# --------------------------------------------------------------------------
# detect
# --------------------------------------------------------------------------

cmd_detect() {
    detect_game_dir
    detect_prefix

    info "game directory"
    printf '  %s\n' "$GAME_DIR"
    info "steam library"
    printf '  %s\n' "$GAME_LIB_ROOT"
    info "proton prefix"
    if [ -n "$PREFIX_DIR" ]; then
        printf '  %s\n' "$PREFIX_DIR"
    else
        printf '  %s (not created yet; it appears after the first start)\n' "${GAME_LIB_ROOT}/steamapps/compatdata/$APPID/pfx"
    fi

    verify_game_binary

    info "steam roots"
    steam_root_candidates | sed 's/^/  /'
    info "library folders"
    library_roots | sed 's/^/  /'
}

# --------------------------------------------------------------------------
# status
# --------------------------------------------------------------------------

EOS_DLL_REL="PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"

status_line() {
    local label="$1" path="$2" expect="${3:-}"
    if [ -e "$path" ]; then
        printf '  %-28s present   %s\n' "$label" "$expect"
    else
        printf '  %-28s MISSING   %s\n' "$label" "$expect"
    fi
}

cmd_status() {
    detect_game_dir
    detect_prefix

    info "PEAK"
    printf '  %s\n' "$GAME_DIR"
    [ -f "$GAME_DIR/version.txt" ] && printf '  version: %s\n' "$(tr -d '\r\n' <"$GAME_DIR/version.txt")"
    [ -f "$GAME_DIR/.doorstop_version" ] && printf '  doorstop: %s\n' "$(tr -d '\r\n' <"$GAME_DIR/.doorstop_version")"

    info "loader"
    status_line "winhttp.dll (proxy)" "$GAME_DIR/winhttp.dll" "$(sha256_of "$GAME_DIR/winhttp.dll")"
    status_line "doorstop_config.ini" "$GAME_DIR/doorstop_config.ini"
    status_line "BepInEx core" "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll"
    status_line "PEAKapi plugin" "$GAME_DIR/BepInEx/plugins/com.github.nglmercer.PEAKapi.dll"
    status_line "PEAKapi core" "$GAME_DIR/BepInEx/plugins/PeakRestApi.Core.dll"

    if [ -f "$GAME_DIR/doorstop_config.ini" ]; then
        local enabled
        enabled="$(first_line "$(sed -n 's/^[[:space:]]*enabled[[:space:]]*=[[:space:]]*\(true\|false\).*/\1/ip' "$GAME_DIR/doorstop_config.ini" || true)")"
        printf '  %-28s %s\n' "doorstop enabled" "${enabled:-unknown}"
        if [ "${enabled:-}" = "false" ]; then
            warn "doorstop is disabled, but the proxy DLL is still in the game folder."
            warn "Wine loads winhttp.dll from the game folder regardless, so the"
            warn "proxy is still in the process. This is NOT a vanilla baseline."
        fi
    fi

    info "EOS"
    if [ -e "$GAME_DIR/$EOS_DLL_REL" ]; then
        printf '  %-28s present   %s\n' "EOSSDK-Win64-Shipping.dll" "$(sha256_of "$GAME_DIR/$EOS_DLL_REL")"
    elif [ -e "$GAME_DIR/$EOS_DLL_REL.disabled" ]; then
        printf '  %-28s DISABLED  %s\n' "EOSSDK-Win64-Shipping.dll" "$(sha256_of "$GAME_DIR/$EOS_DLL_REL.disabled")"
    else
        printf '  %-28s MISSING\n' "EOSSDK-Win64-Shipping.dll"
    fi

    info "logs"
    local log
    for log in "$GAME_DIR/BepInEx/LogOutput.log" "$GAME_DIR/output_log.txt"; do
        if [ -f "$log" ]; then
            printf '  %-28s %s\n' "$(basename "$log")" "$(human_age "$(file_mtime "$log")")"
        else
            printf '  %-28s MISSING\n' "$(basename "$log")"
        fi
    done
}

# --------------------------------------------------------------------------
# verify
# --------------------------------------------------------------------------

# The failure modes are told apart, because they need different fixes.
VERIFY_RESULT=0

verify_fail() {
    err "$*"
    VERIFY_RESULT=1
}

cmd_verify() {
    detect_game_dir
    detect_prefix
    VERIFY_RESULT=0

    info "loader files"
    if [ ! -f "$GAME_DIR/winhttp.dll" ]; then
        verify_fail "no winhttp.dll in the game folder: the BepInEx loader is not installed"
    else
        ok "winhttp.dll present (sha256 $(sha256_of "$GAME_DIR/winhttp.dll"))"
    fi

    if [ ! -f "$GAME_DIR/doorstop_config.ini" ]; then
        verify_fail "no doorstop_config.ini in the game folder"
    else
        local target
        target="$(first_line "$(sed -n 's/^[[:space:]]*target_assembly[[:space:]]*=[[:space:]]*\(.*\)$/\1/ip' "$GAME_DIR/doorstop_config.ini" || true)" | tr -d '\r' | tr -d '"')"
        if [ -z "$target" ]; then
            verify_fail "doorstop_config.ini has no target_assembly"
        elif [ ! -f "$GAME_DIR/${target//\\//}" ]; then
            verify_fail "doorstop targets '$target', which does not exist in the game folder"
        else
            ok "doorstop targets $target"
        fi
    fi

    info "bepinex"
    if [ ! -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]; then
        verify_fail "the BepInEx core is missing"
    else
        ok "the BepInEx core is present"
    fi

    info "plugin"
    if [ ! -f "$GAME_DIR/BepInEx/plugins/com.github.nglmercer.PEAKapi.dll" ]; then
        verify_fail "the PEAKapi plugin is not installed"
    else
        ok "the PEAKapi plugin is installed"
    fi

    info "log freshness"
    local log="$GAME_DIR/BepInEx/LogOutput.log"
    if [ ! -f "$log" ]; then
        verify_fail "there is no BepInEx/LogOutput.log: the loader never ran"
    else
        local age now delta
        now="$(date +%s)"
        age="$(file_mtime "$log")"
        delta=$((now - age))
        if [ "$delta" -gt 600 ]; then
            verify_fail "BepInEx/LogOutput.log is $(human_age "$age") old: it is stale, not evidence of a current injection"
        else
            ok "BepInEx/LogOutput.log is $(human_age "$age") old"
        fi
        if ! grep -q "PEAKapi" "$log"; then
            verify_fail "the loader ran but the log does not mention PEAKapi"
        else
            ok "the log mentions PEAKapi"
        fi
        if grep -qE "could not bind|failed to start|Failed to load" "$log"; then
            verify_fail "the log reports an error; see the log for the details"
        fi
    fi

    info "api"
    local port token
    port="$(first_line "$(sed -n 's/^[[:space:]]*Port[[:space:]]*=[[:space:]]*\([0-9]*\).*/\1/p' "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" 2>/dev/null || true)")"
    port="${port:-17454}"
    if command -v curl >/dev/null 2>&1; then
        local code
        code="$(curl -s -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/health" || true)"
        case "$code" in
            401) ok "the API answers on port $port (401 without a token, as expected)" ;;
            200) ok "the API answers on port $port" ;;
            000) warn "nothing answers on port $port; is the game running?" ;;
            *) warn "the API on port $port answered $code" ;;
        esac
        if [ -f "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" ]; then
            token="$(first_line "$(sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" 2>/dev/null || true)" | tr -d '\r\n')"
            if [ -n "$token" ]; then
                code="$(curl -s -m 3 -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $token" "http://127.0.0.1:$port/health" || true)"
                case "$code" in
                    200) ok "the API accepts the token" ;;
                    401) verify_fail "the API rejects the token from the configuration" ;;
                    504) verify_fail "the API accepts the token but the main thread does not run the queue (COMMAND_TIMEOUT)" ;;
                    000) warn "nothing answers on port $port" ;;
                    *) warn "the API answered $code with a token" ;;
                esac
            fi
        fi
    else
        warn "curl is not installed, the API was not probed"
    fi

    if [ "$VERIFY_RESULT" -ne 0 ]; then
        err "verification failed"
        return 1
    fi
    ok "verification passed"
}

# --------------------------------------------------------------------------
# backup / restore
# --------------------------------------------------------------------------

cmd_backup() {
    detect_game_dir
    detect_prefix
    backup_init

    if [ "$DRY_RUN" -eq 1 ]; then
        info "would write the backup manifest to $MANIFEST"
    else
        mkdir -p "$BACKUP_DIR/files" || die "could not create the backup directory '$BACKUP_DIR'"
        if [ ! -f "$MANIFEST" ]; then
            printf '# peakapi backup manifest v%s\n# created %s\n' "$MANIFEST_VERSION" "$(now_iso)" >"$MANIFEST" \
                || die "could not write the backup manifest '$MANIFEST'"
        fi
        ok "backup manifest: $MANIFEST"
    fi

    local rel
    for rel in \
        "winhttp.dll" \
        "doorstop_config.ini" \
        ".doorstop_version" \
        "$EOS_DLL_REL" \
        "$EOS_DLL_REL.disabled" \
        "BepInEx/plugins/com.github.nglmercer.PEAKapi.dll" \
        "BepInEx/plugins/PeakRestApi.Core.dll" \
        "BepInEx/plugins/Newtonsoft.Json.dll" \
        "BepInEx/config/org.peak.rest-api.cfg"; do
        backup_file "$GAME_DIR/$rel"
    done

    if [ "$DRY_RUN" -eq 1 ]; then
        info "dry-run: nothing was written"
    else
        ok "backup complete"
    fi
}

cmd_restore() {
    detect_game_dir
    detect_prefix
    backup_init

    if [ ! -f "$MANIFEST" ]; then
        die "no backup manifest at '$MANIFEST'. Run 'backup' first."
    fi

    VERIFY_RESULT=0
    local rel recorded_digest current installed
    while IFS=$'\t' read -r kind rel recorded_digest _origin; do
        [ "$kind" = "file" ] || [ "$kind" = "moved" ] || continue
        [ -n "$rel" ] || continue

        local target="$GAME_DIR/$rel"
        installed="$(manifest_installed "$rel")"

        # A file that PEAKapi moved somewhere else is moved back first.
        local moved_info moved_digest moved_rel
        moved_info="$(manifest_moved "$rel")"
        if [ -n "$moved_info" ]; then
            moved_digest="${moved_info%%$'\t'*}"
            moved_rel="${moved_info##*$'\t'}"
            local moved_abs="$GAME_DIR/$moved_rel"
            if [ ! -e "$target" ] && [ -e "$moved_abs" ]; then
                local moved_now
                moved_now="$(sha256_of "$moved_abs")"
                if [ "$moved_now" = "$moved_digest" ]; then
                    run mv -- "$moved_abs" "$target"
                    ok "moved $moved_rel back to $rel"
                elif [ "$FORCE" -eq 1 ]; then
                    run mv -f -- "$moved_abs" "$target"
                    ok "moved $moved_rel back to $rel (overwritten with --force)"
                else
                    warn "$moved_rel was changed while it was moved and is left alone."
                    VERIFY_RESULT=1
                fi
                continue
            fi
        fi

        if [ "$recorded_digest" = "absent" ]; then
            # The file did not exist before PEAKapi was installed. Remove it
            # only when it is still the file PEAKapi put there.
            if [ ! -e "$target" ]; then
                continue
            fi

            current="$(sha256_of "$target")"
            if [ -n "$installed" ] && [ "$current" = "$installed" ]; then
                run rm -f -- "$target"
                ok "removed $rel (it did not exist before the install)"
            elif [ "$FORCE" -eq 1 ]; then
                run rm -f -- "$target"
                ok "removed $rel (overwritten with --force)"
            else
                warn "$rel was changed after the install and is left alone."
                warn "Use --force to remove it."
                VERIFY_RESULT=1
            fi
            continue
        fi

        local stored="$BACKUP_DIR/files/$recorded_digest"
        if [ ! -f "$stored" ]; then
            err "the backup copy of $rel is missing ($stored)"
            VERIFY_RESULT=1
            continue
        fi

        if [ ! -e "$target" ]; then
            run cp -p -- "$stored" "$target"
            ok "restored $rel"
            continue
        fi

        current="$(sha256_of "$target")"
        if [ "$current" = "$recorded_digest" ]; then
            # Already in the backed up state: this is what makes the restore
            # idempotent.
            continue
        fi

        if [ -n "$installed" ] && [ "$current" = "$installed" ]; then
            run cp -p -- "$stored" "$target"
            ok "restored $rel"
            continue
        fi

        if [ "$FORCE" -eq 1 ]; then
            run cp -p -- "$stored" "$target"
            ok "restored $rel (overwritten with --force)"
        else
            warn "$rel was changed after the backup (recorded $recorded_digest, found $current)."
            warn "It was left alone. Use --force to overwrite it."
            VERIFY_RESULT=1
        fi
    done <"$MANIFEST"

    if [ "$VERIFY_RESULT" -ne 0 ]; then
        err "the restore was not complete"
        return 1
    fi
    ok "restore complete"
}

# --------------------------------------------------------------------------
# install / uninstall
# --------------------------------------------------------------------------

# Refuses to touch a proxy that we did not put there.
check_proxy_is_ours() {
    local proxy="$GAME_DIR/winhttp.dll"
    [ -f "$proxy" ] || return 0

    # Only a proxy that PEAKapi installed itself may be replaced. A proxy
    # that was merely backed up is a third party one.
    if [ -n "$(manifest_installed "winhttp.dll")" ]; then
        return 0
    fi

    if [ "$ADOPT_PROXY" -eq 1 ]; then
        warn "adopting the existing winhttp.dll without knowing where it came from"
        warn "its sha256 is $(sha256_of "$proxy")"
        return 0
    fi

    die "a winhttp.dll is already installed in the game folder and PEAKapi did not put it there.
Refusing to overwrite an unknown third party proxy.
Re-run with --adopt-proxy to keep and record it, or remove it yourself first."
}

cmd_install() {
    detect_game_dir
    detect_prefix
    verify_game_binary
    backup_init

    info "backing up the current state"
    cmd_backup

    check_proxy_is_ours

    local plugin_src="${PLUGIN_DIR:-$GAME_DIR/../PEAKapi/src/PeakRestApi/bin/Release/netstandard2.1}"

    if [ ! -d "$plugin_src" ]; then
        die "the built plugin was not found in '$plugin_src'. Build it with 'dotnet build -c Release' or pass --plugin-dir."
    fi

    info "installing the plugin"
    for dll in com.github.nglmercer.PEAKapi.dll PeakRestApi.Core.dll; do
        if [ ! -f "$plugin_src/$dll" ]; then
            die "'$dll' is missing in '$plugin_src'"
        fi
        run mkdir -p "$GAME_DIR/BepInEx/plugins"
        run cp -f -- "$plugin_src/$dll" "$GAME_DIR/BepInEx/plugins/$dll"
        manifest_record_installed "BepInEx/plugins/$dll" "$(sha256_of "$plugin_src/$dll")"
        ok "installed $dll"
    done

    if [ -n "$BEPINEX_FILE" ]; then
        if [ ! -f "$BEPINEX_FILE" ]; then
            die "the BepInEx pack '$BEPINEX_FILE' does not exist"
        fi
        info "installing the BepInEx pack"
        run mkdir -p "$GAME_DIR"
        run unzip -o -q "$BEPINEX_FILE" -d "$GAME_DIR"
        ok "unpacked $(basename "$BEPINEX_FILE")"
    fi

    if [ -n "$PROXY_FILE" ]; then
        if [ ! -f "$PROXY_FILE" ]; then
            die "the proxy '$PROXY_FILE' does not exist"
        fi
        info "installing the doorstop proxy"
        run cp -f -- "$PROXY_FILE" "$GAME_DIR/winhttp.dll"
        manifest_record_installed "winhttp.dll" "$(sha256_of "$PROXY_FILE")"
        ok "installed winhttp.dll (sha256 $(sha256_of "$PROXY_FILE"))"
    fi

    if [ ! -f "$GAME_DIR/doorstop_config.ini" ]; then
        info "writing doorstop_config.ini"
        run cat >"$GAME_DIR/doorstop_config.ini" <<'EOF'
[General]
enabled = true
target_assembly = BepInEx\core\BepInEx.Preloader.dll
redirect_output_log = true
boot_config_override =
ignore_disable_switch = false

[UnityMono]
dll_search_path_override =
debug_enabled = false
debug_address = 127.0.0.1:10000
debug_suspend = false
EOF
        manifest_record_installed "doorstop_config.ini" "$(sha256_of "$GAME_DIR/doorstop_config.ini")"
        ok "wrote doorstop_config.ini"
    fi

    ok "the installation is done"
    info "set the Steam launch option for PEAK"
    printf '  %s\n' 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
}

cmd_uninstall() {
    detect_game_dir
    detect_prefix
    backup_init

    if [ ! -f "$MANIFEST" ]; then
        die "no backup manifest at '$MANIFEST'. Run 'backup' before 'uninstall'."
    fi

    info "removing the PEAKapi plugin"
    local rel
    for rel in \
        "BepInEx/plugins/com.github.nglmercer.PEAKapi.dll" \
        "BepInEx/plugins/PeakRestApi.Core.dll" \
        "BepInEx/plugins/Newtonsoft.Json.dll"; do
        if [ -e "$GAME_DIR/$rel" ]; then
            run rm -f -- "$GAME_DIR/$rel"
            ok "removed $rel"
        fi
    done

    info "restoring the loader files"
    cmd_restore

    ok "PEAKapi was removed; the game folder is back to the backed up state"
}

# --------------------------------------------------------------------------
# EOS fallback (opt-in, degraded mode)
# --------------------------------------------------------------------------

cmd_eos_disable() {
    detect_game_dir
    backup_init

    local eos="$GAME_DIR/$EOS_DLL_REL"
    local off="$eos.disabled"

    if [ ! -f "$eos" ]; then
        if [ -f "$off" ]; then
            ok "the EOS SDK is already disabled"
            return 0
        fi
        die "there is no EOS SDK at '$eos' to disable"
    fi

    if [ -e "$off" ]; then
        die "'$off' already exists. Refusing to overwrite it."
    fi

    if ! confirm "Temporarily disable the EOS SDK? Online and crossplay features may stop working."; then
        info "cancelled"
        return 0
    fi

    backup_file "$eos"
    local digest
    digest="$(sha256_of "$eos")"
    run mv -- "$eos" "$off"
    manifest_record_moved "$EOS_DLL_REL" "$digest" "$EOS_DLL_REL.disabled"
    ok "the EOS SDK is disabled. This is a degraded mode, not a fix."
    ok "run '$0 eos-restore' to put it back"
}

cmd_eos_restore() {
    detect_game_dir
    backup_init

    local eos="$GAME_DIR/$EOS_DLL_REL"
    local off="$eos.disabled"

    if [ ! -f "$off" ]; then
        if [ -f "$eos" ]; then
            ok "the EOS SDK is already in place"
            return 0
        fi
        die "there is no disabled EOS SDK at '$off' to restore"
    fi

    if [ -e "$eos" ]; then
        die "'$eos' already exists. Refusing to overwrite it."
    fi

    local recorded
    recorded="$(manifest_lookup "$EOS_DLL_REL" | cut -f1)"
    if [ -n "$recorded" ] && [ "$recorded" != "absent" ]; then
        local current
        current="$(sha256_of "$off")"
        if [ "$current" != "$recorded" ]; then
            err "the disabled EOS SDK does not match the backed up copy (expected $recorded, found $current)"
            err "it was changed while it was disabled. Put the original back by hand."
            return 1
        fi
        ok "the disabled EOS SDK matches the backed up copy"
    fi

    run mv -- "$off" "$eos"
    ok "the EOS SDK is back in place"
}

# --------------------------------------------------------------------------
# diagnose
# --------------------------------------------------------------------------

# Removes the home directory prefix and the bearer token from a line.
redact_line() {
    sed -e "s#$HOME#~#g" -e 's#'"$GAME_DIR"'#<PEAK>#g' -e 's#'"${PREFIX_DIR:-/nonexistent}"'#<prefix>#g'
}

cmd_diagnose() {
    detect_game_dir
    detect_prefix
    backup_init

    local out="${OUT_FILE:-./peak-diagnose-$(date +%Y%m%d-%H%M%S).txt}"

    {
        printf 'PEAKapi diagnostic report\n'
        printf 'generated: %s\n' "$(now_iso)"
        printf 'host: %s\n' "$(uname -srm)"
        printf '\n== environment\n'
        printf 'steam root candidates:\n'
        steam_root_candidates | redact_line | sed 's/^/  /'
        printf 'library folders:\n'
        library_roots | redact_line | sed 's/^/  /'
        printf 'game directory: %s\n' "$(printf '%s' "$GAME_DIR" | redact_line)"
        printf 'proton prefix: %s\n' "$(printf '%s' "${PREFIX_DIR:-<not created>}" | redact_line)"
        printf 'proton versions:\n'
        local p
        for p in "$GAME_LIB_ROOT/steamapps/common/Proton"*; do
            [ -d "$p" ] || continue
            printf '  %s (%s)\n' "$(basename "$p")" "$(cat "$p/version" 2>/dev/null || echo unknown)"
        done
        printf 'gpu:\n'
        if command -v lspci >/dev/null 2>&1; then
            lspci 2>/dev/null | grep -i -E 'vga|3d controller|display' | sed 's/^/  /'
        fi

        printf '\n== installed files\n'
        local rel
        for rel in \
            "winhttp.dll" \
            "doorstop_config.ini" \
            ".doorstop_version" \
            "$EOS_DLL_REL" \
            "$EOS_DLL_REL.disabled" \
            "BepInEx/core/BepInEx.Preloader.dll" \
            "BepInEx/plugins/com.github.nglmercer.PEAKapi.dll" \
            "BepInEx/plugins/PeakRestApi.Core.dll" \
            "BepInEx/config/org.peak.rest-api.cfg"; do
            if [ -e "$GAME_DIR/$rel" ]; then
                printf '  %-52s %s\n' "$rel" "$(sha256_of "$GAME_DIR/$rel")"
            else
                printf '  %-52s MISSING\n' "$rel"
            fi
        done

        printf '\n== doorstop_config.ini\n'
        if [ -f "$GAME_DIR/doorstop_config.ini" ]; then
            redact_line <"$GAME_DIR/doorstop_config.ini" | sed 's/^/  /'
        fi

        printf '\n== configuration (token redacted)\n'
        if [ -f "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" ]; then
            sed -e 's/^\(AuthenticationToken[[:space:]]*=[[:space:]]*\).*/\1<redacted>/' \
                "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" | redact_line | sed 's/^/  /'
        fi

        printf '\n== log freshness\n'
        for rel in "BepInEx/LogOutput.log" "output_log.txt"; do
            if [ -e "$GAME_DIR/$rel" ]; then
                printf '  %-52s %s\n' "$rel" "$(human_age "$(file_mtime "$GAME_DIR/$rel")")"
            else
                printf '  %-52s MISSING\n' "$rel"
            fi
        done

        printf '\n== BepInEx log (last 80 lines, token redacted)\n'
        if [ -f "$GAME_DIR/BepInEx/LogOutput.log" ]; then
            tail -80 "$GAME_DIR/BepInEx/LogOutput.log" \
                | sed -e 's/Bearer [A-Za-z0-9+/=]\{20,\}/Bearer <redacted>/g' \
                | redact_line | sed 's/^/  /'
        fi

        printf '\n== game log tail (last 60 lines)\n'
        if [ -f "$GAME_DIR/output_log.txt" ]; then
            tail -60 "$GAME_DIR/output_log.txt" | redact_line | sed 's/^/  /'
        fi

        printf '\n== crash folders\n'
        local crashroot
        for crashroot in "$PREFIX_DIR"/drive_c/users/*/AppData/Local/Temp/LandCrab/PEAK/Crashes \
            "$PREFIX_DIR"/drive_c/users/*/Temp/LandCrab/PEAK/Crashes; do
            [ -d "$crashroot" ] || continue
            local c
            for c in "$crashroot"/*; do
                [ -d "$c" ] || continue
                printf '  %s\n' "$(basename "$c")"
            done
        done
    } >"$out"

    ok "the diagnostic report was written to '$out'"
    warn "the report still contains the game version and the file hashes; check it before sharing it"
}

# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------

parse_args() {
    while [ $# -gt 0 ]; do
        case "$1" in
            --dry-run) DRY_RUN=1 ;;
            --game-dir) GAME_DIR_OVERRIDE="${2:-}"; shift ;;
            --steam-root) STEAM_ROOT_OVERRIDE="${2:-}"; shift ;;
            --prefix) PREFIX_OVERRIDE="${2:-}"; shift ;;
            --proxy) PROXY_FILE="${2:-}"; shift ;;
            --bepinex) BEPINEX_FILE="${2:-}"; shift ;;
            --plugin-dir) PLUGIN_DIR="${2:-}"; shift ;;
            --adopt-proxy) ADOPT_PROXY=1 ;;
            --force) FORCE=1 ;;
            --yes) ASSUME_YES=1 ;;
            --out) OUT_FILE="${2:-}"; shift ;;
            -h | --help) usage 0 ;;
            *) die "unknown option '$1' (try --help)" ;;
        esac
        shift
    done
}

main() {
    [ $# -ge 1 ] || usage 1
    local command="$1"
    shift
    parse_args "$@"

    case "$command" in
        detect) cmd_detect ;;
        status) cmd_status ;;
        verify) cmd_verify ;;
        diagnose) cmd_diagnose ;;
        backup) cmd_backup ;;
        restore) cmd_restore ;;
        install) cmd_install ;;
        uninstall) cmd_uninstall ;;
        eos-disable) cmd_eos_disable ;;
        eos-restore) cmd_eos_restore ;;
        -h | --help | help) usage 0 ;;
        *) err "unknown command '$command' (try --help)"; exit 1 ;;
    esac
}

main "$@"
