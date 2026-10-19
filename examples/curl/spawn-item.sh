#!/usr/bin/env bash
# Hand a real item to a random eligible player.
#
# The item is taken from the catalog, so this never sends an id that the
# running game does not have.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=common.sh
source ./common.sh

catalog=$(peak_get "/api/catalog/items")

item_id=$(printf '%s' "$catalog" | tr ',' '\n' | grep '"spawnable":true' -B4 | grep '"id"' | head -1 | cut -d'"' -f4)
if [ -z "$item_id" ]; then
  echo "No spawnable item found. Is a level loaded?" >&2
  exit 1
fi

echo "Spawning $item_id"

peak_post /api/spawn/item "{\"itemId\":\"$item_id\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}"
echo
