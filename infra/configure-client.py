#!/usr/bin/env python3
"""Fills in a published client's deploy-time settings: its Content Security Policy and browser telemetry.

The policy lives in 4.Frontend/Game.Client/wwwroot/staticwebapp.config.json. Some things in it are
only known at deploy time, so they are placeholders:

  __API_ORIGIN__ / __API_WS_ORIGIN__   the API the client calls (HTTPS and the SignalR WebSocket)
  __AVATAR_ORIGIN__                    the blob storage that serves uploaded portraits
  __TELEMETRY_ORIGIN__                 Application Insights' ingestion endpoint (nothing without telemetry)
  __IMPORTMAP_HASH__                   the hash of the inline <script type="importmap"> that
                                       dotnet publish writes into index.html (it changes per build)

With an Application Insights connection string, it also goes into index.html's
telemetry-connection-string meta tag, with the API's origin next to it, which turns on browser
telemetry (wwwroot/js/telemetry.js). The connection string only lets a browser send telemetry, the
same as every site using the Application Insights JavaScript SDK.

The browser tests do the same in 5.Tests/Game.E2E.Tests/ClientHost.cs.

Before publishing, the "settings" form writes the client's appsettings.Production.json: the API it
calls, and for staging and pull request previews a label shown in a strip across the top of every
page (Layout/EnvironmentBanner.razor).

Usage: configure-client.py <published wwwroot> <api url> <avatar origin> [<app insights connection string>]
       configure-client.py settings <source wwwroot> <api url> [<environment label>]
"""
import base64
import hashlib
import html
import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

# Where the SDK sends telemetry when the connection string doesn't name an endpoint.
DEFAULT_INGESTION = "https://dc.services.visualstudio.com"


def origin(url: str) -> str:
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.netloc:
        sys.exit(f"Not an absolute http(s) URL: {url!r}")
    return f"{parts.scheme}://{parts.netloc}"


def ingestion_origin(connection_string: str) -> str:
    """The origin the browser sends telemetry to, from a connection string like
    'InstrumentationKey=...;IngestionEndpoint=https://eastus2-3.in.applicationinsights.azure.com/;...'."""
    settings = dict(part.split("=", 1) for part in connection_string.split(";") if "=" in part)
    settings = {key.strip().lower(): value.strip() for key, value in settings.items()}
    if not settings.get("instrumentationkey"):
        sys.exit("The Application Insights connection string has no InstrumentationKey.")
    return origin(settings.get("ingestionendpoint") or DEFAULT_INGESTION)


def set_meta(index: str, name: str, value: str) -> str:
    tag = f'<meta name="{name}" content="" />'
    if index.count(tag) != 1:
        sys.exit(f"Expected one empty {name} meta tag in index.html.")
    return index.replace(tag, f'<meta name="{name}" content="{html.escape(value)}" />')


def configure(wwwroot: Path, api_url: str, avatar_url: str, connection_string: str = "") -> str:
    index_path = wwwroot / "index.html"
    index = index_path.read_text(encoding="utf-8")
    maps = re.findall(r'<script type="importmap">(.*?)</script>', index, re.DOTALL)
    if len(maps) != 1:
        sys.exit(f"Expected one inline import map in index.html, found {len(maps)}.")
    digest = base64.b64encode(hashlib.sha256(maps[0].encode("utf-8")).digest()).decode()

    api = origin(api_url)
    values = {
        "__API_ORIGIN__": api,
        "__API_WS_ORIGIN__": api.replace("https://", "wss://", 1).replace("http://", "ws://", 1),
        "__AVATAR_ORIGIN__": origin(avatar_url),
        "__TELEMETRY_ORIGIN__": ingestion_origin(connection_string) if connection_string else "",
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
    policy = re.sub(r" {2,}", " ", policy).replace(" ;", ";")  # an empty placeholder leaves a gap

    config["globalHeaders"]["Content-Security-Policy"] = policy
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")

    if connection_string:
        index = set_meta(index, "telemetry-connection-string", connection_string)
        index = set_meta(index, "telemetry-api-origin", api)
        index_path.write_text(index, encoding="utf-8")
        # dotnet publish also writes compressed copies; drop them so nothing serves the old page.
        for stale in (wwwroot / "index.html.br", wwwroot / "index.html.gz"):
            stale.unlink(missing_ok=True)
    return policy


def write_settings(wwwroot: Path, api_url: str, label: str = "") -> dict:
    settings = {"ApiBaseUrl": origin(api_url)}
    if label.strip():
        settings["EnvironmentLabel"] = label.strip()
    (wwwroot / "appsettings.Production.json").write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")
    return settings


def main() -> None:
    if len(sys.argv) >= 2 and sys.argv[1] == "settings":
        if len(sys.argv) not in (4, 5):
            sys.exit(__doc__)
        settings = write_settings(Path(sys.argv[2]), sys.argv[3], sys.argv[4] if len(sys.argv) == 5 else "")
        print(f"appsettings.Production.json: {json.dumps(settings)}")
        return
    if len(sys.argv) not in (4, 5):
        sys.exit(__doc__)
    policy = configure(Path(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) == 5 else "")
    print(f"Content-Security-Policy: {policy}")
    print(f"Browser telemetry: {'on' if len(sys.argv) == 5 and sys.argv[4] else 'off'}")


if __name__ == "__main__":
    main()
