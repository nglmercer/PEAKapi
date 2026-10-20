#!/usr/bin/env bash
#
# peak-repro-matrix.sh - the five way A/B matrix for the PEAK loader.
#
# Every case is a fresh Steam launch of the real game. The cases are:
#
#   1 vanilla      no proxy, no BepInEx, the original EOS SDK
#   2 proxy-off   the proxy is present but doorstop is disabled, original EOS
#   3 bepinex      the proxy and BepInEx with an empty plugin folder, original EOS
#   4 peakapi     the proxy, BepInEx and PEAKapi, original EOS
#   5 peakapi-noeos   the same as 4 with the EOS SDK temporarily disabled
#
# Case 1 is the only true vanilla baseline: the proxy is physically absent,
# so no winhttp.dll of ours can be in the process. "doorstop disabled" is
# NOT a vanilla baseline, because Wine still loads the proxy from the game
# folder and its exports are still bound in the process.
#
# The script never deletes the EOS SDK: it is renamed and renamed back, and
# the rename is recorded in the backup manifest of peak-linux.sh.
#
# Usage:
#   scripts/peak-repro-matrix.sh [--case N] [--wait SECONDS] [--out FILE]
#                                [--keep-going] [--dry-run]
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"
PEAK_LINUX="$SCRIPT_DIR/peak-linux.sh"

APPID=3527290
EOS_REL="PEAK_Data/Plugins/x86_64/EOSSDK-Win64-Shipping.dll"
PROXY_REL="winhttp.dll"
CONFIG_REL="doorstop_config.ini"
PLUGIN_REL="BepInEx/plugins/com.github.nglmercer.PEAKapi.dll"
CORE_REL="BepInEx/plugins/PeakRestApi.Core.dll"

WAIT_SECONDS=150
ONLY_CASE=""
OUT_FILE=""
KEEP_GOING=0
DRY_RUN=0
REPORT_ONLY=0

GAME_DIR=""
PREFIX_DIR=""

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RESET=$'\033[0m'; C_INFO=$'\033[0;36m'; C_OK=$'\033[0;32m'; C_ERR=$'\033[0;31m'; C_WARN=$'\033[0;33m'
else
    C_RESET=""; C_INFO=""; C_OK=""; C_ERR=""; C_WARN=""
fi

info() { printf '%s==>%s %s\n' "$C_INFO" "$C_RESET" "$*"; }
ok() { printf '%s  ok%s %s\n' "$C_OK" "$C_RESET" "$*"; }
warn() { printf '%swarn%s %s\n' "$C_WARN" "$C_RESET" "$*" >&2; }
err() { printf '%sfail%s %s\n' "$C_ERR" "$C_RESET" "$*" >&2; }
die() { err "$*"; exit 1; }

usage() {
    sed -n '3,28p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------

resolve_game_dir() {
    if [ -n "${PEAK_GAME_DIR:-}" ]; then
        GAME_DIR="$PEAK_GAME_DIR"
    else
        # The detect command prints the game directory on the line after the
        # "game directory" header.
        # The output is captured first: piping it into an awk that exits early
        # would kill the producer with SIGPIPE.
        local detect_output
        detect_output="$("$PEAK_LINUX" detect 2>/dev/null || true)"
        GAME_DIR="$(awk '/^==> game directory/ { getline; print $1; found = 1 } END { if (!found) exit 1 }' <<<"$detect_output" || true)"
    fi

    [ -n "$GAME_DIR" ] && [ -d "$GAME_DIR" ] || die "the PEAK game directory could not be found"
    GAME_DIR="$(cd "$GAME_DIR" && pwd -P)"
}

# A stable place for the artifacts of a run.
ARTIFACTS="$REPO_ROOT/artifacts/matrix"

# Stops a running game. The binary is a Windows one, so the process name is
# the Windows one.
stop_game() {
    pkill -x PEAK.exe >/dev/null 2>&1 || true
    pkill -f "PEAK.exe" >/dev/null 2>&1 || true
    sleep 2
}

# Removes the log files so that a stale log can never be read as a result.
clear_logs() {
    rm -f "$GAME_DIR/output_log.txt" "$GAME_DIR/BepInEx/LogOutput.log"
    rm -rf "$GAME_DIR/BepInEx/cache"/* 2>/dev/null || true

    local plog
    plog="$(player_log)"
    [ -f "$plog" ] && rm -f "$plog"
    return 0
}

resolve_prefix() {
    if [ -n "${PEAK_PREFIX:-}" ]; then
        PREFIX_DIR="$PEAK_PREFIX"
        return
    fi

    PREFIX_DIR=""
    local root
    for root in \
        "$HOME/.local/share/Steam/steamapps/compatdata/$APPID/pfx" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/compatdata/$APPID/pfx" \
        "$HOME/.steam/root/steamapps/compatdata/$APPID/pfx"; do
        if [ -d "$root" ]; then
            PREFIX_DIR="$(cd "$root" && pwd -P)"
            return
        fi
    done
}

# The prefix holds the crash folders and the Player.log of the game.
crash_root() {
    local root
    for root in \
        "$PREFIX_DIR"/drive_c/users/*/AppData/Local/Temp/LandCrab/PEAK/Crashes \
        "$PREFIX_DIR"/drive_c/users/*/Temp/LandCrab/PEAK/Crashes; do
        [ -d "$root" ] && printf '%s\n' "$root" && return
    done
    printf '\n'
}

# The port the API binds to, read from the configuration.
api_port() {
    local port
    port="$(sed -n 's/^[[:space:]]*Port[[:space:]]*=[[:space:]]*\([0-9]*\).*/\1/p' \
        "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" 2>/dev/null || true)"
    port="${port%%$'\n'*}"
    printf '%s\n' "${port:-17454}"
}

api_token() {
    sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' \
        "$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg" 2>/dev/null | tr -d '\r\n'
}

# --------------------------------------------------------------------------
# Case configuration
# --------------------------------------------------------------------------

# The state that the matrix changes is moved aside, never deleted. The stash
# lives inside the backup directory, so it is covered by the manifest too.
STASH=""
STASH_LIST=""

stash_init() {
    STASH="$GAME_DIR/.peakapi-backup/stash"
    STASH_LIST="$GAME_DIR/.peakapi-backup/stash.list"
    mkdir -p "$STASH"
    : >"$STASH_LIST"
}

# stash_path <relative path> - moves a file out of the way.
stash_path() {
    local rel="$1" src="$GAME_DIR/$1" dst
    [ -e "$src" ] || return 0
    dst="$STASH/$rel"
    mkdir -p "$(dirname "$dst")"
    rm -rf -- "$dst"
    mv -- "$src" "$dst"
    printf '%s\n' "$rel" >>"$STASH_LIST"
    ok "stashed $rel"
}

# Puts every stashed file back, deepest first.
unstash_all() {
    [ -n "$STASH_LIST" ] && [ -f "$STASH_LIST" ] || return 0
    local rel
    while IFS= read -r rel; do
        [ -n "$rel" ] || continue
        [ -e "$STASH/$rel" ] || continue
        mkdir -p "$(dirname "$GAME_DIR/$rel")"
        rm -rf -- "${GAME_DIR:?}/$rel"
        mv -- "$STASH/$rel" "$GAME_DIR/$rel"
        ok "restored $rel"
    done <"$STASH_LIST"
    : >"$STASH_LIST"
}

# config_case <name> - puts the game folder into the state of the case.
config_case() {
    local case_name="$1"

    # Every case starts from the state the previous case left, with the
    # stashed files put back.
    unstash_all

    case "$case_name" in
        vanilla)
            info "case 1: true vanilla (no proxy, no BepInEx, original EOS)"
            stash_path "$PROXY_REL"
            stash_path "$CONFIG_REL"
            stash_path "BepInEx"
            restore_eos
            ;;
        proxy-off)
            info "case 2: the proxy is present, doorstop is disabled, original EOS"
            restore_proxy
            restore_eos
            write_config "false"
            ;;
        bepinex)
            info "case 3: the proxy and BepInEx with an empty plugin folder, original EOS"
            restore_proxy
            restore_eos
            write_config "true"
            rm -f "$GAME_DIR/$PLUGIN_REL" "$GAME_DIR/$CORE_REL"
            ;;
        peakapi)
            info "case 4: the proxy, BepInEx and PEAKapi, original EOS"
            restore_proxy
            restore_eos
            write_config "true"
            install_peakapi
            ;;
        peakapi-noeos)
            info "case 5: the same as 4 with the EOS SDK temporarily disabled"
            restore_proxy
            write_config "true"
            install_peakapi
            disable_eos
            ;;
        *)
            die "unknown case '$case_name'"
            ;;
    esac
}

write_config() {
    local enabled="$1"
    local live="$GAME_DIR/$CONFIG_REL"
    local original="$STASH/$CONFIG_REL.orig"

    if [ ! -f "$live" ]; then
        cp "$REPO_ROOT/tests/shell/fixtures/doorstop_config.ini" "$live" 2>/dev/null || true
    fi

    # The original file is kept byte for byte, so that the restore puts back
    # exactly what was there before, line endings included.
    if [ -f "$live" ] && [ ! -f "$original" ]; then
        mkdir -p "$(dirname "$original")"
        cp -p "$live" "$original"
    fi

    if [ -f "$live" ]; then
        sed -i "s/^[[:space:]]*enabled[[:space:]]*=.*/enabled = $enabled/" "$live"
    fi
}

# Puts the original configuration file back, byte for byte.
restore_config() {
    local original="$STASH/$CONFIG_REL.orig"
    [ -f "$original" ] || return 0
    cp -p "$original" "$GAME_DIR/$CONFIG_REL"
    ok "the original $CONFIG_REL was restored"
}

# The proxy is put back from the copy that was made before the matrix.
restore_proxy() {
    local store="$GAME_DIR/.peakapi-backup/files"
    if [ -f "$GAME_DIR/$PROXY_REL" ]; then
        return 0
    fi

    if [ -d "$store" ]; then
        local digest
        digest="$(awk -F'\t' '$1 == "file" && $2 == "winhttp.dll" { print $3; exit }' \
            "$GAME_DIR/.peakapi-backup/manifest.tsv" 2>/dev/null)"
        if [ -n "$digest" ] && [ -f "$store/$digest" ]; then
            cp -p "$store/$digest" "$GAME_DIR/$PROXY_REL"
            ok "the proxy was restored from the backup"
            return 0
        fi
    fi

    die "there is no winhttp.dll and no backup of it. Install the BepInEx pack first."
}

install_peakapi() {
    local src="$REPO_ROOT/src/PeakRestApi/bin/Release/netstandard2.1"
    if [ ! -f "$src/com.github.nglmercer.PEAKapi.dll" ]; then
        warn "the built plugin is not in '$src'. Run 'dotnet build -c Release' first."
        return 0
    fi
    mkdir -p "$GAME_DIR/BepInEx/plugins"
    cp -f "$src/com.github.nglmercer.PEAKapi.dll" "$src/PeakRestApi.Core.dll" "$GAME_DIR/BepInEx/plugins/"
    ok "PEAKapi was installed"
}

disable_eos() {
    local eos="$GAME_DIR/$EOS_REL"
    [ -f "$eos" ] || return 0
    mv "$eos" "$eos.disabled"
    ok "the EOS SDK was renamed for this run"
}

restore_eos() {
    local eos="$GAME_DIR/$EOS_REL"
    if [ -f "$eos" ]; then
        return 0
    fi
    if [ -f "$eos.disabled" ]; then
        mv "$eos.disabled" "$eos"
        ok "the EOS SDK was put back"
    fi
}

# --------------------------------------------------------------------------
# One run
# --------------------------------------------------------------------------

# The game writes two logs: doorstop redirects the Unity log to
# output_log.txt in the game folder, and the game itself always writes
# Player.log into the prefix. Without doorstop only the second one exists.
player_log() {
    local root
    for root in \
        "$PREFIX_DIR"/drive_c/users/*/AppData/LocalLow/LandCrab/PEAK/Player.log \
        "$PREFIX_DIR"/drive_c/users/*/AppData/Local/LandCrab/PEAK/Player.log; do
        [ -f "$root" ] && printf '%s\n' "$root" && return
    done
    printf '\n'
}

# The crash marker of the Unity crash handler.
has_crashed() {
    local log
    for log in "$GAME_DIR/output_log.txt" "$(player_log)"; do
        [ -f "$log" ] || continue
        if grep -q "^Crash!!!" "$log" 2>/dev/null; then
            return 0
        fi
    done
    return 1
}

# Waits for the game to crash or for the timeout. Prints "crashed",
# "running" or "gone".
wait_for_game() {
    local deadline=$((SECONDS + WAIT_SECONDS))
    local started=0

    while [ "$SECONDS" -lt "$deadline" ]; do
        if pgrep -x PEAK.exe >/dev/null 2>&1 || pgrep -f "PEAK[.]exe" >/dev/null 2>&1; then
            started=1
        elif [ "$started" -eq 1 ]; then
            # The game was running and is gone now.
            sleep 3
            printf 'gone\n'
            return
        fi

        if has_crashed; then
            printf 'crashed\n'
            return
        fi

        sleep 3
    done
    printf 'running\n'
}

run_case() {
    local name="$1" dir="$ARTIFACTS/$1"
    mkdir -p "$dir"

    info "running case '$name'"
    config_case "$name"
    clear_logs
    stop_game

    if [ "$DRY_RUN" -eq 1 ]; then
        warn "dry run: the game is not started"
        return 0
    fi

    local started
    started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    steam -applaunch "$APPID" >/dev/null 2>&1 || warn "the Steam launch command failed"

    local outcome
    outcome="$(wait_for_game)"

    local ended
    ended="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    # The API is probed while the game is still up, if it is.
    local api_answer="not probed"
    local port token
    port="$(api_port)"
    token="$(api_token)"
    if [ -n "$token" ] && command -v curl >/dev/null 2>&1; then
        api_answer="$(curl -s -m 5 -H "Authorization: Bearer $token" "http://127.0.0.1:$port/health" 2>&1 || true)"
        [ -n "$api_answer" ] || api_answer="(empty answer)"
    fi

    stop_game

    {
        printf 'case: %s\n' "$name"
        printf 'started: %s\n' "$started"
        printf 'ended: %s\n' "$ended"
        printf 'outcome: %s\n' "$outcome"
        printf 'api: %s\n' "$api_answer"
        printf 'eos present: %s\n' "$([ -f "$GAME_DIR/$EOS_REL" ] && echo yes || echo no)"
        printf 'proxy present: %s\n' "$([ -f "$GAME_DIR/$PROXY_REL" ] && echo yes || echo no)"
        printf 'bepinex log: %s\n' "$([ -f "$GAME_DIR/BepInEx/LogOutput.log" ] && echo yes || echo no)"
    } >"$dir/summary.txt"

    [ -f "$GAME_DIR/output_log.txt" ] && cp "$GAME_DIR/output_log.txt" "$dir/output_log.txt"
    [ -f "$GAME_DIR/BepInEx/LogOutput.log" ] && cp "$GAME_DIR/BepInEx/LogOutput.log" "$dir/LogOutput.log"
    local plog
    plog="$(player_log)"
    [ -f "$plog" ] && cp "$plog" "$dir/Player.log"

    local crashes
    crashes="$(crash_root)"
    if [ -n "$crashes" ] && [ -d "$crashes" ]; then
        find "$crashes" -maxdepth 2 -newermt "$started" -type d >"$dir/crash_folders.txt" 2>/dev/null || true
    fi

    ok "case '$name' finished: $outcome"
}

# --------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------

write_report() {
    local report="$1"

    {
        printf '# The runtime matrix\n\n'
        printf 'Generated: %s\n\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf 'The raw artifacts of the run are in `artifacts/matrix/`. Every case is a\n'
        printf 'fresh Steam launch with the logs cleared beforehand, so a stale log can\n'
        printf 'never be read as a result. The analysis is in\n'
        printf '[LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md).\n\n'
        printf '| Case | Result | The game | BepInEx | The API |\n'
        printf '|---|---|---|---|---|\n'

        local name label outcome api bepinex eos
        while IFS='|' read -r label name; do
            label="$(printf '%s' "$label" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
            name="$(printf '%s' "$name" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
            [ -n "$label" ] || continue

            local dir="$ARTIFACTS/$name"
            if [ ! -f "$dir/summary.txt" ]; then
                printf '| %s | NOT TESTED | no artifacts | | |\n' "$label"
                continue
            fi

            outcome="$(sed -n 's/^outcome: //p' "$dir/summary.txt")"
            api="$(sed -n 's/^api: //p' "$dir/summary.txt")"
            bepinex="$(sed -n 's/^bepinex log: //p' "$dir/summary.txt")"
            eos="$(sed -n 's/^eos present: //p' "$dir/summary.txt")"

            case "$outcome" in
                crashed) outcome="FAIL (the game crashed)" ;;
                gone) outcome="FAIL (the game exited)" ;;
                running) outcome="PASS (no crash)" ;;
                *) outcome="NOT TESTED" ;;
            esac

            if [ "$bepinex" = "yes" ]; then
                bepinex="loaded"
            else
                bepinex="not loaded"
            fi

            if [ "$eos" = "yes" ]; then
                eos="the original EOS SDK"
            else
                eos="the EOS SDK is disabled"
            fi

            if [ -z "$api" ] || [ "$api" = "not probed" ]; then
                api="no answer"
            else
                api="answered"
            fi

            printf '| %s | %s | %s | %s | %s |\n' "$label" "$outcome" "$eos" "$bepinex" "$api"
        done <<'EOF'
1. true vanilla|vanilla
2. proxy present, doorstop disabled|proxy-off
3. proxy and BepInEx, no plugin|bepinex
4. proxy, BepInEx and PEAKapi|peakapi
5. proxy, BepInEx and PEAKapi, EOS disabled|peakapi-noeos
EOF

        printf '\n## What each case proves\n\n'
        printf '| Case | What it proves |\n|---|---|\n'
        printf '| 1. true vanilla | The game runs with the original EOS SDK when the proxy is not in the process. |\n'
        printf '| 2. proxy present, doorstop disabled | The proxy is still loaded by Wine, so this is **not** a vanilla baseline. |\n'
        printf '| 3. proxy and BepInEx, no plugin | The loader works without PEAKapi. |\n'
        printf '| 4. proxy, BepInEx and PEAKapi | The shipped configuration. |\n'
        printf '| 5. like 4 with the EOS SDK disabled | A degraded mode, only for a local diagnosis. |\n'
    } >"$report"

    ok "the report was written to '$report'"
}

# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------

# The game folder is put back into the state it was in before, also when the
# script is interrupted. Without this a killed run would leave the game with
# a disabled loader or without the plugin, and the next run would measure the
# wrong thing.
restore_game_folder() {
    info "restoring the game folder"
    unstash_all
    restore_eos
    restore_proxy
    install_peakapi

    # The configuration file is put back byte for byte, so that the game
    # folder is exactly as it was before the matrix.
    restore_config
}

on_interrupt() {
    warn "the matrix was interrupted, the game folder is restored"
    restore_game_folder
    stop_game
    exit 130
}

trap on_interrupt INT TERM

main() {
    while [ $# -gt 0 ]; do
        case "$1" in
            --case) ONLY_CASE="${2:-}"; shift ;;
            --wait) WAIT_SECONDS="${2:-150}"; shift ;;
            --out) OUT_FILE="${2:-}"; shift ;;
            --keep-going) KEEP_GOING=1 ;;
            --report-only) REPORT_ONLY=1 ;;
            --dry-run) DRY_RUN=1 ;;
            -h | --help) usage 0 ;;
            *) die "unknown option '$1' (try --help)" ;;
        esac
        shift
    done

    if [ "$REPORT_ONLY" -eq 1 ]; then
        resolve_game_dir
        resolve_prefix
        write_report "${OUT_FILE:-$REPO_ROOT/docs/RUNTIME_MATRIX.md}"
        return 0
    fi

    resolve_game_dir
    resolve_prefix
    mkdir -p "$ARTIFACTS"
    stash_init

    info "the game directory is '$GAME_DIR'"
    info "the proton prefix is '$PREFIX_DIR'"

    # The state before the matrix is recorded, so that every case can be put
    # back and the EOS SDK is never lost.
    "$PEAK_LINUX" backup --game-dir "$GAME_DIR" >/dev/null || warn "the backup failed"

    local cases="vanilla proxy-off bepinex peakapi peakapi-noeos"
    local failed=0
    for c in $cases; do
        if [ -n "$ONLY_CASE" ]; then
            case "$ONLY_CASE" in
                1) c="vanilla" ;;
                2) c="proxy-off" ;;
                3) c="bepinex" ;;
                4) c="peakapi" ;;
                5) c="peakapi-noeos" ;;
                *) die "unknown case '$ONLY_CASE'" ;;
            esac
        fi

        if ! run_case "$c"; then
            failed=$((failed + 1))
            if [ "$KEEP_GOING" -eq 0 ]; then
                err "case '$c' failed and --keep-going was not given"
                break
            fi
        fi

        # With --case only the selected case is run.
        [ -n "$ONLY_CASE" ] && break
    done

    # The game folder is put back into the state it was in before.
    restore_game_folder
    stop_game

    local report="${OUT_FILE:-$REPO_ROOT/docs/RUNTIME_MATRIX.md}"
    write_report "$report"

    if [ "$failed" -ne 0 ]; then
        err "$failed case(s) failed"
        return 1
    fi
    ok "the matrix finished"
}

main "$@"
