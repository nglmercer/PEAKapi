#!/usr/bin/env bash
# Launches PEAK through Steam and captures the live PEAKapi responses.
#
# Usage: scripts/live-check.sh [output-file]
#
# The game directory is found with scripts/peak-linux.sh, so the script works
# with a Steam installation in a non default location as well.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
OUT="${1:-/tmp/peak-live-capture.txt}"

GAME_DIR="$("$SCRIPT_DIR/peak-linux.sh" detect 2>/dev/null | awk '/^==> game directory/ { getline; print $1; exit }')"
if [ -z "$GAME_DIR" ]; then
    echo "the PEAK game directory could not be found" >&2
    exit 1
fi

CONFIG="$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg"
API="http://127.0.0.1:17454"

: >"$OUT"

pkill -x PEAK.exe 2>/dev/null
for _ in $(seq 1 30); do
    ss -ltn 2>/dev/null | grep -q ':17454' || break
    sleep 1
done

echo "launching PEAK through Steam at $(date -Is)" >>"$OUT"
steam -applaunch 3527290 >>"$OUT" 2>&1

up=0
for _ in $(seq 1 120); do
    code=$(curl -s -m 2 -o /dev/null -w "%{http_code}" "$API/health" 2>/dev/null)
    if [ "$code" != "000" ]; then
        up=1
        echo "api up after ${_}s (http $code)" >>"$OUT"
        break
    fi
    sleep 1
done

if [ "$up" != "1" ]; then
    echo "api did not come up" >>"$OUT"
    exit 1
fi

TOKEN=$(sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' "$CONFIG" 2>/dev/null | tr -d '\r\n')
AUTH="Authorization: Bearer $TOKEN"

call() {
    local method="$1" path="$2" body="${3:-}"
    echo "=== $method $path ===" >>"$OUT"
    if [ -n "$body" ]; then
        curl -s -m 15 -X "$method" \
            -H "$AUTH" \
            -H "Content-Type: application/json" \
            -H "Idempotency-Key: live-check-$(date +%s%N)-$RANDOM" \
            --data-binary "$body" \
            "$API$path" >>"$OUT" 2>&1
    else
        curl -s -m 15 -X "$method" \
            -H "$AUTH" \
            -H "Content-Type: application/json" \
            "$API$path" >>"$OUT" 2>&1
    fi
    echo >>"$OUT"
}

call GET /health
call GET /api/version
call GET /api/capabilities
call GET /api/game/state
call GET /api/players
call GET /api/catalog/items
call GET /api/catalog/entities
call GET /api/catalog/hazards
call GET /api/entities
call GET /openapi.json

# The mutations use a made up id on purpose: the answer proves that the
# request reaches the router, the validation and the game, without changing
# anything in the level.
call POST /api/spawn/item '{"itemId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/spawn/entity '{"entityId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/spawn/hazard '{"hazardId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/entities/clear '{}'

echo "capture complete at $(date -Is)" >>"$OUT"
