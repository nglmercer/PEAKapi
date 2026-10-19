#!/usr/bin/env bash
# Show the health of the running PEAKapi.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=common.sh
source ./common.sh

peak_get /health
echo
