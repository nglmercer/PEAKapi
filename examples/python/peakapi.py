"""Talk to a running PEAKapi.

    PEAK_API_TOKEN=... python3 examples/python/peakapi.py health
    PEAK_API_TOKEN=... python3 examples/python/peakapi.py items
    PEAK_API_TOKEN=... python3 examples/python/peakapi.py spawn

The token can also be read from the PEAKapi configuration with
PEAK_CONFIG=/path/to/org.peak.rest-api.cfg.
"""

from __future__ import annotations

import json
import os
import pathlib
import sys
import urllib.error
import urllib.request
import uuid

BASE_URL = os.environ.get("PEAK_API_URL", "http://127.0.0.1:17454")


def read_token() -> str:
    token = os.environ.get("PEAK_API_TOKEN")
    if token:
        return token.strip()

    candidates = [
        os.environ.get("PEAK_CONFIG"),
        str(
            pathlib.Path.home()
            / ".local/share/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg"
        ),
    ]

    for candidate in candidates:
        if not candidate:
            continue
        path = pathlib.Path(candidate)
        if not path.is_file():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.strip().startswith("AuthenticationToken"):
                return line.split("=", 1)[1].strip()

    raise SystemExit("Set PEAK_API_TOKEN or PEAK_CONFIG.")


TOKEN = read_token()


def peak_get(path: str) -> tuple[int, dict]:
    request = urllib.request.Request(
        f"{BASE_URL}{path}", headers={"Authorization": f"Bearer {TOKEN}"}
    )
    return _send(request)


def peak_post(path: str, body: dict) -> tuple[int, dict]:
    request = urllib.request.Request(
        f"{BASE_URL}{path}",
        method="POST",
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Type": "application/json",
            "Idempotency-Key": str(uuid.uuid4()),
        },
    )
    return _send(request)


def _send(request: urllib.request.Request) -> tuple[int, dict]:
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read().decode("utf-8"))


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "health"

    if command == "health":
        status, body = peak_get("/health")
    elif command == "capabilities":
        status, body = peak_get("/api/capabilities")
    elif command == "items":
        status, body = peak_get("/api/catalog/items")
    elif command == "spawn":
        # Take an id that the running game really has.
        _, catalog = peak_get("/api/catalog/items")
        item = next((entry for entry in catalog.get("items", []) if entry.get("spawnable")), None)
        if item is None:
            print("No spawnable item. Is a level loaded?", file=sys.stderr)
            return 1
        status, body = peak_post(
            "/api/spawn/item",
            {
                "itemId": item["id"],
                "quantity": 1,
                "position": {"mode": "random_player"},
            },
        )
    else:
        print(f"Unknown command: {command}", file=sys.stderr)
        return 1

    print(status, json.dumps(body, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
