"""Tests for infra/shared-base.sh, against a stand-in for the az command.

    python3 -m unittest discover -s infra/tests
"""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "shared-base.sh"

# Answers the two az commands the script uses: the shared deployment's outputs (absent when the
# shared base isn't deployed) and the databases on the shared server.
FAKE_AZ = r'''#!/usr/bin/env python3
import json, os, sys
state = json.load(open(os.environ["FAKE_STATE"]))
args = sys.argv[1:]
def opt(name):
    return args[args.index(name) + 1]
if args[:3] == ["deployment", "group", "show"]:
    if opt("--name") != "portfolio-shared" or state["outputs"] is None:
        sys.exit("ERROR: (DeploymentNotFound)")
    print(json.dumps(state["outputs"]))
elif args[:3] == ["sql", "db", "list"]:
    assert opt("--server") == state["outputs"]["sqlServerName"]["value"]
    query = opt("--query")
    name = query.split("'")[1]
    print(sum(1 for database in state["databases"] if database == name))
else:
    sys.exit("fake az: unexpected command " + " ".join(args))
'''

OUTPUTS = {
    "environmentName": {"value": "cae-portfolio-abc"},
    "sqlServerName": {"value": "sql-portfolio-abc"},
    "location": {"value": "centralus"},
}


class SharedBaseTests(unittest.TestCase):
    def run_script(self, *args, outputs=OUTPUTS, databases=()):
        folder = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: subprocess.run(["rm", "-rf", str(folder)]))
        az = folder / "az"
        az.write_text(FAKE_AZ)
        az.chmod(0o755)
        (folder / "state.json").write_text(json.dumps({"outputs": outputs, "databases": list(databases)}))
        env = dict(os.environ, PATH=f"{folder}:{os.environ['PATH']}", FAKE_STATE=str(folder / "state.json"))
        return subprocess.run(["bash", str(SCRIPT), *args], capture_output=True, text=True, env=env)

    def test_production_is_ready_once_its_database_is_on_the_shared_server(self):
        result = self.run_script("rg-portfolio-shared", "production", databases=["cardarena", "portfolio-slc"])

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([
            "group=rg-portfolio-shared",
            "environment=cae-portfolio-abc",
            "server=sql-portfolio-abc",
            "location=centralus",
            "database=cardarena",
            "ready=true",
        ], result.stdout.splitlines())

    def test_staging_has_a_database_of_its_own(self):
        result = self.run_script("rg-portfolio-shared", "staging", databases=["cardarena"])

        self.assertIn("database=cardarena-staging", result.stdout.splitlines())
        self.assertIn("ready=false", result.stdout.splitlines())

    def test_not_ready_until_the_data_has_been_moved(self):
        result = self.run_script("rg-portfolio-shared", "production")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("ready=false", result.stdout.splitlines())

    def test_says_what_to_do_when_the_shared_base_isnt_deployed(self):
        result = self.run_script("rg-portfolio-shared", "production", outputs=None)

        self.assertEqual(1, result.returncode)
        self.assertIn("Run infra/setup-shared.ps1 first", result.stderr)
        self.assertEqual("", result.stdout)

    def test_needs_a_group_and_an_environment(self):
        self.assertEqual(2, self.run_script("rg-portfolio-shared").returncode)
        self.assertEqual(2, self.run_script("rg-portfolio-shared", "dev").returncode)


if __name__ == "__main__":
    unittest.main()
