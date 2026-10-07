"""Tests for infra/environment-outputs.sh, against a stand-in for the az command.

    python3 -m unittest discover -s infra/tests
"""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "environment-outputs.sh"

# Answers the two az commands the script uses from a JSON file of deployments.
FAKE_AZ = r'''#!/usr/bin/env python3
import json, os, sys
deployments = json.load(open(os.environ["FAKE_DEPLOYMENTS"]))
args = sys.argv[1:]
def opt(name):
    return args[args.index(name) + 1]
if args[:3] == ["deployment", "group", "list"]:
    done = sorted((d for d in deployments if d["name"].startswith("card-arena-") and d["state"] == "Succeeded"),
                  key=lambda d: d["timestamp"])
    if done:
        print(done[-1]["name"])
elif args[:3] == ["deployment", "group", "show"]:
    print(json.dumps(next(d for d in deployments if d["name"] == opt("--name"))["outputs"]))
else:
    sys.exit("fake az: unexpected command " + " ".join(args))
'''


def outputs(api, storage, swa, telemetry=None):
    values = {"apiUrl": {"value": api}, "storageAccount": {"value": storage}, "staticWebAppName": {"value": swa}}
    if telemetry is not None:
        values["appInsightsConnectionString"] = {"value": telemetry}
    return values


class EnvironmentOutputsTests(unittest.TestCase):
    def run_script(self, deployments, *args):
        folder = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(folder)]))
        az = folder / "az"
        az.write_text(FAKE_AZ)
        az.chmod(0o755)
        (folder / "deployments.json").write_text(json.dumps(deployments))
        env = dict(os.environ, PATH=f"{folder}:{os.environ['PATH']}", FAKE_DEPLOYMENTS=str(folder / "deployments.json"))
        return subprocess.run(["bash", str(SCRIPT), *args], capture_output=True, text=True, env=env)

    def test_prints_the_addresses_from_the_newest_successful_deployment(self):
        result = self.run_script([
            {"name": "card-arena-7-abc", "state": "Succeeded", "timestamp": "2026-10-07T10:00:00Z",
             "outputs": outputs("https://old.example", "stold", "swa-old", "InstrumentationKey=old")},
            {"name": "card-arena-9-def", "state": "Succeeded", "timestamp": "2026-10-07T12:00:00Z",
             "outputs": outputs("https://ca-cardarena-api.staging.example", "stcardarenaxyz", "swa-cardarena-xyz", "InstrumentationKey=new")},
            {"name": "card-arena-10-def", "state": "Failed", "timestamp": "2026-10-07T13:00:00Z",
             "outputs": outputs("https://broken.example", "stbroken", "swa-broken")},
            {"name": "something-else", "state": "Succeeded", "timestamp": "2026-10-07T14:00:00Z", "outputs": {}},
        ], "rg-card-arena-staging")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([
            "api=https://ca-cardarena-api.staging.example",
            "avatars=https://stcardarenaxyz.blob.core.windows.net",
            "telemetry=InstrumentationKey=new",
            "swa=swa-cardarena-xyz",
        ], result.stdout.splitlines())

    def test_telemetry_is_empty_when_the_deployment_has_none(self):
        result = self.run_script([
            {"name": "card-arena-1-abc", "state": "Succeeded", "timestamp": "2026-10-07T10:00:00Z",
             "outputs": outputs("https://api.example", "st", "swa")},
        ], "rg")
        self.assertIn("telemetry=", result.stdout.splitlines())

    def test_says_what_to_do_when_nothing_was_deployed(self):
        result = self.run_script([], "rg-card-arena-staging")
        self.assertEqual(1, result.returncode)
        self.assertIn("Run the Deploy to Azure workflow first", result.stderr)
        self.assertEqual("", result.stdout)

    def test_needs_a_resource_group(self):
        self.assertEqual(2, self.run_script([]).returncode)


if __name__ == "__main__":
    unittest.main()
