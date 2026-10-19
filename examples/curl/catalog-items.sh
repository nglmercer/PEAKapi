#!/usr/bin/env bash
# List the items that the running game actually has.
#
# The ids in the answer are the ids a spawn request uses. The script picks
# the first one that the server reports as spawnable, so it never sends an
# id that does not exist.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=common.sh
source ./common.sh

catalog=$(peak_get "/api/catalog/items")
echo "$catalog" | head -c 2000
echo

item_id=$(printf '%s' "$catalog" | tr ',' '\n' | grep -A0 -B0 '"spawnable":true' -B4 | grep '"id"' | head -1 | cut -d'"' -f4)

if [ -z "$item_id" ]; then
  echo "No spawnable item found. Is a level loaded?" >&2
  exit 1
fi

echo "First spawnable item: $item_id"
