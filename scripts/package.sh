#!/usr/bin/env bash
# Builds the release package of PEAKapi.
#
#   ./scripts/package.sh
#
# The result is a zip that can be unpacked into the PEAK folder, and a
# Thunderstore compatible manifest next to it.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION=$(grep -oE '<Version>[^<]+</Version>' src/PeakRestApi/PeakRestApi.csproj | head -1 | cut -d'>' -f2 | cut -d'<' -f1)
STAGE="artifacts/package/PEAKapi-$VERSION"
DIST="artifacts/dist"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# The SDK is often installed in ~/.dotnet and not on the PATH.
if ! command -v dotnet > /dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "dotnet was not found. Install the .NET 10 SDK." >&2
    exit 1
  fi
fi

echo "Building PEAKapi $VERSION"
dotnet build -c Release -v minimal

BIN="src/PeakRestApi/bin/Release/netstandard2.1"
rm -rf "$STAGE"
mkdir -p "$STAGE/plugins"

# The plugin itself and the libraries it needs. The game already provides
# Newtonsoft.Json, so it is not part of the package.
cp "$BIN/com.github.nglmercer.PEAKapi.dll" "$STAGE/plugins/"
cp "$BIN/PeakRestApi.Core.dll" "$STAGE/plugins/"

cp README.md "$STAGE/"
cp LICENSE "$STAGE/"
cp CHANGELOG.md "$STAGE/"
cp docs/INSTALLATION.md "$STAGE/"

cat > "$STAGE/manifest.json" <<EOF
{
  "name": "PEAKapi",
  "version_number": "$VERSION",
  "website_url": "https://github.com/nglmercer/PEAKapi",
  "description": "A local, authenticated REST API for PEAK. Lets a live stream spawn items, entities and hazards in a running game.",
  "dependencies": [
    "BepInEx-BepInExPack_PEAK-5.4.75301"
  ]
}
EOF

mkdir -p "$DIST"
python3 - "$DIST/PEAKapi-$VERSION.zip" "artifacts/package/PEAKapi-$VERSION" <<'PY'
import os
import sys
import zipfile

archive, source = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
    for root, _dirs, files in os.walk(source):
        for name in files:
            path = os.path.join(root, name)
            bundle.write(path, os.path.relpath(path, os.path.dirname(source)))
PY
cp "$STAGE/manifest.json" "$DIST/manifest.json"

echo "Package: $DIST/PEAKapi-$VERSION.zip"
echo "Install: unzip it into the PEAK folder and copy plugins/ into BepInEx/plugins/."
