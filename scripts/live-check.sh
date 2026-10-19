#!/bin/bash
# Launches PEAK through Steam and captures the live PEAKapi responses.
# Usage: scripts/live-check.sh [output-file]
set -u

OUT="${1:-/tmp/peak-live-capture.txt}"
GAME_DIR="$HOME/.local/share/Steam/steamapps/common/PEAK"
CONFIG="$GAME_DIR/BepInEx/config/org.peak.rest-api.cfg"
API="http://127.0.0.1:17454"

: > "$OUT"

pkill -x PEAK.exe 2>/dev/null
for _ in $(seq 1 30); do
  ss -ltn 2>/dev/null | grep -q ':17454' || break
  sleep 1
done

echo "launching PEAK through Steam at $(date -Is)" >> "$OUT"
steam -applaunch 3527290 >> "$OUT" 2>&1

up=0
for _ in $(seq 1 120); do
  code=$(curl -s -m 2 -o /dev/null -w "%{http_code}" "$API/health" 2>/dev/null)
  if [ "$code" != "000" ]; then
    up=1
    echo "api up after ${_}s (http $code)" >> "$OUT"
    break
  fi
  sleep 1
done

if [ "$up" != "1" ]; then
  echo "api did not come up" >> "$OUT"
  exit 1
fi

TOKEN=$(sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' "$CONFIG" 2>/dev/null | tr -d '\r\n')
AUTH="Authorization: Bearer $TOKEN"

call() {
  echo "=== $1 $2 ===" >> "$OUT"
  curl -s -m 15 -X "$1" -H "$AUTH" -H "Content-Type: application/json" \
    ${3:+--data-binary "$3"} "$API$2" >> "$OUT" 2>&1
  echo >> "$OUT"
}

call GET  /health
call GET  /api/version
call GET  /api/capabilities
call GET  /api/game/state
call GET  /api/players
call GET  /api/catalog/items
call GET  /api/catalog/entities
call GET  /api/catalog/hazards
call GET  /api/entities
call GET  /openapi.json
call POST /api/spawn/item '{"itemId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/spawn/entity '{"entityId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/spawn/hazard '{"hazardId":"__missing__","quantity":1,"position":{"mode":"random_player"}}'
call POST /api/entities/clear '{}'

echo "capture complete at $(date -Is)" >> "$OUT"
