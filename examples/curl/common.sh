#!/usr/bin/env bash
# Talk to a running PEAKapi.
#
# The token is read from the PEAKapi configuration by default. Override it
# with PEAK_API_TOKEN, and the address with PEAK_API_URL.
set -euo pipefail

PEAK_API_URL="${PEAK_API_URL:-http://127.0.0.1:17454}"

if [ -z "${PEAK_API_TOKEN:-}" ]; then
  for candidate in \
    "${PEAK_CONFIG:-}" \
    "$HOME/.local/share/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg" \
    "$HOME/.steam/steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg" \
    "C:/Program Files (x86)/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg"; do
    if [ -n "$candidate" ] && [ -f "$candidate" ]; then
      PEAK_API_TOKEN=$(sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' "$candidate" | tr -d '\r\n')
      break
    fi
  done
fi

if [ -z "${PEAK_API_TOKEN:-}" ]; then
  echo "Set PEAK_API_TOKEN, or point PEAK_CONFIG at the PEAKapi configuration." >&2
  exit 1
fi

# peak_get <path> [query]
peak_get() {
  curl -sS -H "Authorization: Bearer $PEAK_API_TOKEN" "$PEAK_API_URL$1"
}

# peak_post <path> <body> [idempotency-key]
peak_post() {
  curl -sS -X POST \
    -H "Authorization: Bearer $PEAK_API_TOKEN" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: ${3:-$(cat /proc/sys/kernel/random/uuid 2>/dev/null || echo "$$-$(date +%s)")}" \
    -d "$2" "$PEAK_API_URL$1"
}
