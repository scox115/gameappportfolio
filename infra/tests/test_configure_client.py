"""Tests for infra/configure-client.py, on a stand-in for a published client.

    python3 -m unittest discover -s infra/tests
"""
import importlib.util
import json
import shutil
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "infra" / "configure-client.py"
spec = importlib.util.spec_from_file_location("configure_client", SCRIPT)
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)

SOURCE = REPO / "4.Frontend" / "Game.Client" / "wwwroot"
CONNECTION = ("InstrumentationKey=00000000-0000-0000-0000-000000000001;"
              "IngestionEndpoint=https://eastus2-3.in.applicationinsights.azure.com/;"
              "LiveEndpoint=https://eastus2.livediagnostics.monitor.azure.com/;ApplicationId=abc")
API = "https://ca-cardarena-api.example.eastus2.azurecontainerapps.io"
AVATARS = "https://stcardarena.blob.core.windows.net"


class ConfigureClientTests(unittest.TestCase):
    def setUp(self):
        self.wwwroot = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.wwwroot)
        shutil.copy(SOURCE / "staticwebapp.config.json", self.wwwroot)
        # The real index.html, with the import map dotnet publish would fill in.
        index = (SOURCE / "index.html").read_text(encoding="utf-8")
        (self.wwwroot / "index.html").write_text(
            index.replace('<script type="importmap"></script>', '<script type="importmap">{"imports":{}}</script>'),
            encoding="utf-8")
        for stale in ("index.html.br", "index.html.gz"):
            (self.wwwroot / stale).write_bytes(b"old")

    def policy(self):
        config = json.loads((self.wwwroot / "staticwebapp.config.json").read_text(encoding="utf-8"))
        return config["globalHeaders"]["Content-Security-Policy"]

    def index(self):
        return (self.wwwroot / "index.html").read_text(encoding="utf-8")

    def test_telemetry_lets_the_browser_reach_only_the_ingestion_endpoint(self):
        client.configure(self.wwwroot, API, AVATARS, CONNECTION)

        self.assertIn(f"connect-src 'self' {API} wss://ca-cardarena-api.example.eastus2.azurecontainerapps.io "
                      "https://eastus2-3.in.applicationinsights.azure.com;", self.policy())
        self.assertNotIn("livediagnostics", self.policy())

    def test_telemetry_puts_the_connection_string_and_api_in_the_page(self):
        client.configure(self.wwwroot, API, AVATARS, CONNECTION)

        self.assertIn(f'<meta name="telemetry-connection-string" content="{CONNECTION}" />', self.index())
        self.assertIn(f'<meta name="telemetry-api-origin" content="{API}" />', self.index())
        # The compressed copies of the old page would otherwise still be there to serve.
        self.assertFalse((self.wwwroot / "index.html.br").exists())
        self.assertFalse((self.wwwroot / "index.html.gz").exists())

    def test_without_a_connection_string_telemetry_stays_off(self):
        client.configure(self.wwwroot, API, AVATARS)

        self.assertIn('<meta name="telemetry-connection-string" content="" />', self.index())
        self.assertNotIn("applicationinsights.azure.com", self.policy())
        self.assertNotIn("  ", self.policy())
        self.assertIn("wss://ca-cardarena-api.example.eastus2.azurecontainerapps.io;", self.policy())

    def test_a_connection_string_without_an_endpoint_uses_the_global_one(self):
        client.configure(self.wwwroot, API, AVATARS, "InstrumentationKey=00000000-0000-0000-0000-000000000001")

        self.assertIn("https://dc.services.visualstudio.com;", self.policy())

    def test_a_connection_string_without_a_key_is_refused(self):
        with self.assertRaises(SystemExit):
            client.configure(self.wwwroot, API, AVATARS, "IngestionEndpoint=https://eastus2-3.in.applicationinsights.azure.com/")

    def test_every_placeholder_is_filled(self):
        client.configure(self.wwwroot, API, AVATARS, CONNECTION)

        self.assertNotIn("__", self.policy())
        self.assertRegex(self.policy(), r"script-src 'self' 'wasm-unsafe-eval' 'sha256-[A-Za-z0-9+/]+=*';")


if __name__ == "__main__":
    unittest.main()


class ClientSettingsTests(unittest.TestCase):
    def setUp(self):
        self.wwwroot = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.wwwroot)

    def settings(self):
        return json.loads((self.wwwroot / "appsettings.Production.json").read_text(encoding="utf-8"))

    def test_production_gets_the_api_and_no_banner(self):
        client.write_settings(self.wwwroot, API + "/")
        self.assertEqual({"ApiBaseUrl": API}, self.settings())

    def test_staging_gets_a_banner_label(self):
        client.write_settings(self.wwwroot, API, "Staging")
        self.assertEqual({"ApiBaseUrl": API, "EnvironmentLabel": "Staging"}, self.settings())

    def test_a_relative_api_url_is_refused(self):
        with self.assertRaises(SystemExit):
            client.write_settings(self.wwwroot, "/api")
