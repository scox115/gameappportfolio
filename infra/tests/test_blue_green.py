"""Tests for infra/blue-green.sh, run against stand-ins for the az and curl commands.

The stand-ins keep a small Container App in a JSON file: its revisions (active or not, provisioned
or not, healthy or not) and which revision has the traffic. Run with:

    python3 -m unittest discover -s infra/tests
"""
import json
import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "blue-green.sh"

# Understands exactly the az commands the script uses, and records each call.
FAKE_AZ = r'''#!/usr/bin/env python3
import json, os, re, sys
path = os.environ["FAKE_STATE"]
state = json.load(open(path))
args = sys.argv[1:]
state["calls"].append(" ".join(a for a in args if not a.startswith("--") and a not in (
    "rg", "ca-api", "tsv", "none") and not a.startswith("[") and not a.startswith("sort_by")
    and not a.startswith("properties.")))

def opt(name):
    return args[args.index(name) + 1] if name in args else None

revisions = state["revisions"]
out = ""
cmd = " ".join(args[:4])
if cmd.startswith("containerapp ingress traffic show"):
    pinned = [t["revisionName"] for t in state["traffic"] if t.get("revisionName") and t["weight"] > 0]
    out = pinned[0] if pinned else ""
elif cmd.startswith("containerapp ingress traffic set"):
    name, weight = opt("--revision-weight").split("=")
    state["traffic"] = [{"revisionName": name, "weight": int(weight)}]
elif cmd.startswith("containerapp show"):
    out = state["latestReady"]
elif cmd.startswith("containerapp revision show"):
    revision = revisions[opt("--revision")]
    out = revision["fqdn"] if opt("--query") == "properties.fqdn" else revision["state"]
elif cmd.startswith("containerapp revision list"):
    query = opt("--query")
    active = sorted((r for r in revisions.values() if r["active"]), key=lambda r: r["created"])
    if query.startswith("sort_by"):
        live = re.search(r"name != '([^']*)'", query).group(1)
        others = [r["name"] for r in active if r["name"] != live]
        out = others[-1] if others else ""
    else:
        out = "\n".join(r["name"] for r in active)
elif cmd.startswith("containerapp revision deactivate"):
    revisions[opt("--revision")]["active"] = False
elif cmd.startswith("containerapp revision activate"):
    revisions[opt("--revision")]["active"] = True
else:
    sys.exit("fake az: unexpected command " + " ".join(args))

json.dump(state, open(path, "w"))
if out:
    print(out)
'''

# Succeeds only for revisions marked healthy.
FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, sys
state = json.load(open(os.environ["FAKE_STATE"]))
url = sys.argv[-1]
healthy = any(r["healthy"] and url.startswith("https://" + r["fqdn"] + "/") for r in state["revisions"].values())
sys.exit(0 if healthy else 22)
'''


def revision(name, created, active=True, healthy=True, state="Provisioned"):
    return {"name": name, "created": created, "active": active, "healthy": healthy,
            "state": state, "fqdn": f"ca-api--{name}.example.azurecontainerapps.io"}


class BlueGreenTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        for name, body in (("az", FAKE_AZ), ("curl", FAKE_CURL)):
            (bin_dir / name).write_text(body)
            (bin_dir / name).chmod(0o755)
        self.state_path = self.dir / "state.json"
        self.env = {**os.environ, "PATH": f"{bin_dir}:{os.environ['PATH']}",
                    "FAKE_STATE": str(self.state_path), "SMOKE_ATTEMPTS": "3", "SMOKE_DELAY": "0"}

    def given(self, *revisions, traffic=None, latest_ready=""):
        self.state_path.write_text(json.dumps({
            "revisions": {r["name"]: r for r in revisions},
            "traffic": traffic or [],
            "latestReady": latest_ready,
            "calls": [],
        }))

    def run_script(self, *args):
        return subprocess.run(["bash", str(SCRIPT), *args], env=self.env, capture_output=True, text=True)

    @property
    def state(self):
        return json.loads(self.state_path.read_text())

    def live(self):
        return [t["revisionName"] for t in self.state["traffic"] if t["weight"] > 0]

    def active(self):
        return sorted(name for name, r in self.state["revisions"].items() if r["active"])

    # --- live ---

    def test_live_is_the_pinned_revision(self):
        self.given(revision("v1", 1), revision("v2", 2), traffic=[{"revisionName": "v1", "weight": 100}], latest_ready="v2")
        self.assertEqual(self.run_script("live", "rg", "ca-api").stdout.strip(), "v1")

    def test_live_before_blue_green_is_the_latest_ready_revision(self):
        # Single-revision mode: traffic follows "latest revision" and names none.
        self.given(revision("v1", 1), traffic=[{"latestRevision": True, "weight": 100}], latest_ready="v1")
        self.assertEqual(self.run_script("live", "rg", "ca-api").stdout.strip(), "v1")

    # --- release ---

    def test_a_healthy_revision_gets_the_traffic_and_the_old_one_stays_on_standby(self):
        self.given(revision("v0", 0), revision("v1", 1), revision("v2", 2),
                   traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("release", "rg", "ca-api", "v2", "v1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.live(), ["v2"])
        self.assertEqual(self.active(), ["v1", "v2"])  # v0 deactivated, v1 kept for rollback

    def test_an_unhealthy_revision_never_gets_traffic_and_is_deactivated(self):
        self.given(revision("v1", 1), revision("v2", 2, healthy=False),
                   traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("release", "rg", "ca-api", "v2", "v1")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("never became healthy", result.stderr)
        self.assertEqual(self.live(), ["v1"])
        self.assertEqual(self.active(), ["v1"])
        self.assertNotIn("containerapp ingress traffic set", " | ".join(self.state["calls"]))

    def test_a_revision_that_fails_to_provision_never_gets_traffic(self):
        self.given(revision("v1", 1), revision("v2", 2, state="Failed"),
                   traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("release", "rg", "ca-api", "v2", "v1")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("failed to provision", result.stderr)
        self.assertEqual(self.live(), ["v1"])

    def test_a_revision_still_provisioning_is_waited_for_then_fails_if_it_never_finishes(self):
        self.given(revision("v1", 1), revision("v2", 2, state="Provisioning"),
                   traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("release", "rg", "ca-api", "v2", "v1")

        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.live(), ["v1"])

    def test_releasing_the_live_revision_again_changes_nothing(self):
        # A re-run of the same commit creates no new revision.
        self.given(revision("v1", 1), traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("release", "rg", "ca-api", "v1", "v1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.state["calls"], [])

    def test_the_first_release_has_no_previous_revision_to_keep(self):
        self.given(revision("v1", 1))

        result = self.run_script("release", "rg", "ca-api", "v1", "")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.live(), ["v1"])

    # --- rollback ---

    def test_rollback_returns_traffic_to_the_standby_revision(self):
        self.given(revision("v1", 1), revision("v2", 2), traffic=[{"revisionName": "v2", "weight": 100}])

        result = self.run_script("rollback", "rg", "ca-api")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "v1")
        self.assertEqual(self.live(), ["v1"])

    def test_rollback_to_a_named_revision_reactivates_it_first(self):
        self.given(revision("v1", 1, active=False), revision("v2", 2), revision("v3", 3),
                   traffic=[{"revisionName": "v3", "weight": 100}])

        result = self.run_script("rollback", "rg", "ca-api", "v1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.live(), ["v1"])
        self.assertIn("v1", self.active())

    def test_rollback_never_picks_a_revision_that_failed_its_release(self):
        # v3 failed its smoke test and was deactivated; v1 is the real standby.
        self.given(revision("v1", 1), revision("v2", 2), revision("v3", 3, active=False, healthy=False),
                   traffic=[{"revisionName": "v2", "weight": 100}])

        self.assertEqual(self.run_script("rollback", "rg", "ca-api").stdout.strip(), "v1")

    def test_rollback_to_an_unhealthy_revision_keeps_the_traffic_where_it_is(self):
        self.given(revision("v1", 1, healthy=False), revision("v2", 2), traffic=[{"revisionName": "v2", "weight": 100}])

        result = self.run_script("rollback", "rg", "ca-api")

        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.live(), ["v2"])

    def test_rollback_with_nothing_to_go_back_to_fails_clearly(self):
        self.given(revision("v1", 1), traffic=[{"revisionName": "v1", "weight": 100}])

        result = self.run_script("rollback", "rg", "ca-api")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no other revision", result.stderr)


if __name__ == "__main__":
    unittest.main()
