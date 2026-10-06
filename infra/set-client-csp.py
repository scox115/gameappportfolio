#!/usr/bin/env python3
"""Fills in the Content Security Policy in a published client's staticwebapp.config.json.

The policy itself lives in 4.Frontend/Game.Client/wwwroot/staticwebapp.config.json. Three things
in it are only known at deploy time, so they are placeholders:

  __API_ORIGIN__ / __API_WS_ORIGIN__   the API the client calls (HTTPS and the SignalR WebSocket)
  __AVATAR_ORIGIN__                    the blob storage that serves uploaded portraits
  __IMPORTMAP_HASH__                   the hash of the inline <script type="importmap"> that
                                       dotnet publish writes into index.html (it changes per build)

The browser tests do the same in 5.Tests/Game.E2E.Tests/ClientHost.cs.

Usage: set-client-csp.py <published wwwroot> <api url> <avatar origin>
"""
import base64
import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit


def origin(url: str) -> str:
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.netloc:
        sys.exit(f"Not an absolute http(s) URL: {url!r}")
    return f"{parts.scheme}://{parts.netloc}"


def main() -> None:
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    wwwroot, api_url, avatar_url = Path(sys.argv[1]), sys.argv[2], sys.argv[3]

    index = (wwwroot / "index.html").read_text(encoding="utf-8")
    maps = re.findall(r'<script type="importmap">(.*?)</script>', index, re.DOTALL)
    if len(maps) != 1:
        sys.exit(f"Expected one inline import map in index.html, found {len(maps)}.")
    digest = base64.b64encode(hashlib.sha256(maps[0].encode("utf-8")).digest()).decode()

    api = origin(api_url)
    values = {
        "__API_ORIGIN__": api,
        "__API_WS_ORIGIN__": api.replace("https://", "wss://", 1).replace("http://", "ws://", 1),
        "__AVATAR_ORIGIN__": origin(avatar_url),
        "__IMPORTMAP_HASH__": f"'sha256-{digest}'",
    }

    config_path = wwwroot / "staticwebapp.config.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    policy = config["globalHeaders"]["Content-Security-Policy"]
    for placeholder, value in values.items():
        if placeholder not in policy:
            sys.exit(f"{placeholder} is missing from the policy.")
        policy = policy.replace(placeholder, value)
    if "__" in policy:
        sys.exit(f"Unfilled placeholder left in the policy: {policy}")

    config["globalHeaders"]["Content-Security-Policy"] = policy
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    print(f"Content-Security-Policy: {policy}")


if __name__ == "__main__":
    main()
