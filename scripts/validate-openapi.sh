#!/usr/bin/env bash
# Checks that the OpenAPI contract matches the routes the server actually
# serves and that it is a valid OpenAPI 3 document.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONTRACT="$ROOT/docs/openapi.json"

python3 - "$CONTRACT" <<'PY'
import json
import re
import sys

contract_path = sys.argv[1]

with open(contract_path, encoding="utf-8") as handle:
    contract = json.load(handle)

errors = []

if contract.get("openapi") != "3.0.3":
    errors.append("openapi must be 3.0.3")

# The routes the router implements.
implemented = {
    ("get", "/health"),
    ("get", "/api/version"),
    ("get", "/api/capabilities"),
    ("get", "/api/game/state"),
    ("get", "/openapi.json"),
    ("get", "/api/players"),
    ("get", "/api/catalog/items"),
    ("get", "/api/catalog/entities"),
    ("get", "/api/catalog/hazards"),
    ("get", "/api/entities"),
    ("post", "/api/spawn/item"),
    ("post", "/api/spawn/entity"),
    ("post", "/api/spawn/hazard"),
    ("post", "/api/player/effect"),
    ("post", "/api/entities/despawn"),
    ("post", "/api/entities/clear"),
}

documented = set()
for path, operations in contract.get("paths", {}).items():
    for method in operations:
        if method.lower() in {"get", "post", "put", "patch", "delete", "head"}:
            documented.add((method.lower(), path))

missing = implemented - documented
extra = documented - implemented

for method, path in sorted(missing):
    errors.append(f"the route {method.upper()} {path} is not documented")
for method, path in sorted(extra):
    errors.append(f"the route {method.upper()} {path} is documented but not implemented")

# Every $ref has to resolve.
def walk(node, seen):
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref" and isinstance(value, str):
                if value.startswith("#/"):
                    seen.add(value)
                else:
                    errors.append(f"the reference {value} is not local")
            else:
                walk(value, seen)
    elif isinstance(node, list):
        for item in node:
            walk(item, seen)


refs = set()
walk(contract, refs)
for ref in sorted(refs):
    target = contract
    for part in ref.lstrip("#/").split("/"):
        if not isinstance(target, dict) or part not in target:
            errors.append(f"the reference {ref} does not resolve")
            break
        target = target[part]

# Every mutation needs the idempotency key and a JSON body.
def resolve(node):
    """Resolves a local $ref so that its fields can be inspected."""
    if isinstance(node, dict) and "$ref" in node:
        target = contract
        for part in node["$ref"].lstrip("#/").split("/"):
            if not isinstance(target, dict) or part not in target:
                return {}
            target = target[part]
        return target
    return node


for path, operations in contract.get("paths", {}).items():
    operation = operations.get("post")
    if not operation:
        continue
    parameters = {resolve(p).get("name") for p in operation.get("parameters", [])}
    if "Idempotency-Key" not in parameters:
        errors.append(f"POST {path} does not require the Idempotency-Key header")
    if "application/json" not in operation.get("requestBody", {}).get("content", {}):
        errors.append(f"POST {path} has no JSON request body")

if errors:
    for error in errors:
        print("error:", error)
    raise SystemExit(1)

print(f"openapi.json is valid and documents {len(documented)} routes.")
PY

# The contract has to be embedded in the core assembly as well.
grep -q 'EmbeddedResource Include="../../docs/openapi.json"' \
  "$ROOT/src/PeakRestApi.Core/PeakRestApi.Core.csproj" \
  || { echo "error: docs/openapi.json is not embedded in PeakRestApi.Core" >&2; exit 1; }

echo "The contract is embedded in the core assembly."
