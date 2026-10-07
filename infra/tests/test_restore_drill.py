"""Tests for infra/restore-drill/restore_drill.py, with the Azure CLI and the database reads faked.

    python3 -m unittest discover -s infra/tests
"""
import datetime as dt
import importlib.util
import io
import subprocess
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "restore-drill" / "restore_drill.py"
spec = importlib.util.spec_from_file_location("restore_drill", SCRIPT)
drill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(drill)
REAL_RECORD_RESULT = drill.record_result  # the tests below stub it out

NOW = dt.datetime(2026, 10, 7, 12, 30, 45, tzinfo=dt.timezone.utc)
SERVER = "sql-cardarena-abc.database.windows.net"


def database(migrations=("001_Initial", "002_Shop"), **tables):
    return {"migrations": list(migrations), "tables": tables or {"dbo.Players": 12, "dbo.Battles": 340}}


class FakeAzure:
    """Records az calls and answers the few the drill makes."""

    def __init__(self, earliest="2026-09-30T08:00:00Z", fail_restore=False):
        self.calls = []
        self.earliest = earliest
        self.fail_restore = fail_restore

    def __call__(self, *args):
        self.calls.append(args)
        command = " ".join(args[:4])
        if command.startswith("deployment group list"):
            return SERVER
        if command.startswith("sql db show"):
            return {"earliestRestoreDate": self.earliest}
        if command.startswith("sql db restore") and self.fail_restore:
            raise RuntimeError("az sql db restore failed: Conflict")
        if command.startswith("account get-access-token"):
            return "token"
        return None

    def ran(self, prefix):
        return [call for call in self.calls if " ".join(call).startswith(prefix)]

    def option(self, call, name):
        return call[call.index(name) + 1]


class RestoreDrillTests(unittest.TestCase):
    def setUp(self):
        self.azure = FakeAzure()
        self.databases = {"GameDb": database(), "GameDb-drill-202610071230": database()}
        self.recorded = []
        patches = [
            mock.patch.object(drill, "az", self.azure),
            mock.patch.object(drill, "now", lambda: NOW),
            mock.patch.object(drill, "public_ip", lambda: "20.1.2.3"),
            mock.patch.object(drill, "inspect_database", lambda fqdn, name, token: self.databases[name]),
            mock.patch.object(drill, "record_result", lambda fqdn, name, token, passed, detail:
                              self.recorded.append((name, passed, detail))),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def run_drill(self, *args):
        with redirect_stderr(io.StringIO()) as errors, mock.patch("sys.stdout", io.StringIO()) as output:
            code = drill.main(["--resource-group", "rg", *args])
        return code, output.getvalue(), errors.getvalue()

    def test_a_good_backup_passes_and_the_copy_is_deleted(self):
        code, report, _ = self.run_drill()

        self.assertEqual(code, 0)
        self.assertIn("Restore drill passed", report)
        restore = self.azure.ran("sql db restore")[0]
        self.assertEqual(self.azure.option(restore, "--server"), "sql-cardarena-abc")
        self.assertEqual(self.azure.option(restore, "--name"), "GameDb")
        self.assertEqual(self.azure.option(restore, "--dest-name"), "GameDb-drill-202610071230")
        self.assertEqual(self.azure.option(restore, "--time"), "2026-10-07T12:20:00")  # 10 minutes back
        self.assertEqual(self.azure.option(self.azure.ran("sql db delete")[0], "--name"), "GameDb-drill-202610071230")

    def test_only_this_machine_is_let_through_the_firewall_and_only_for_the_drill(self):
        self.run_drill()

        rule = self.azure.ran("sql server firewall-rule create")[0]
        self.assertEqual(self.azure.option(rule, "--start-ip-address"), "20.1.2.3")
        self.assertEqual(self.azure.option(rule, "--end-ip-address"), "20.1.2.3")
        removed = self.azure.ran("sql server firewall-rule delete")[0]
        self.assertEqual(self.azure.option(removed, "--name"), self.azure.option(rule, "--name"))

    def test_the_report_compares_every_table(self):
        self.databases["GameDb"] = database(**{"dbo.Players": 13, "dbo.Battles": 351})
        self.databases["GameDb-drill-202610071230"] = database(**{"dbo.Players": 12, "dbo.Battles": 340})

        code, report, _ = self.run_drill()

        self.assertEqual(code, 0)  # players kept playing after the restore point; that's expected
        self.assertIn("| dbo.Players | 13 | 12 |", report)
        self.assertIn("| dbo.Battles | 351 | 340 |", report)

    def test_an_empty_copy_of_a_database_with_data_fails(self):
        self.databases["GameDb-drill-202610071230"] = database(**{"dbo.Players": 0, "dbo.Battles": 0})

        code, report, errors = self.run_drill()

        self.assertEqual(code, 1)
        self.assertIn("Restore drill failed", report)
        self.assertIn("::error::The restored database is empty", errors)
        self.assertTrue(self.azure.ran("sql db delete"))

    def test_a_copy_without_migrations_fails(self):
        self.databases["GameDb-drill-202610071230"] = database(migrations=())

        code, _, errors = self.run_drill()

        self.assertEqual(code, 1)
        self.assertIn("no migration history", errors)

    def test_a_migration_after_the_restore_point_is_not_a_failure(self):
        self.databases["GameDb"] = database(migrations=("001_Initial", "002_Shop", "003_Guilds"),
                                            **{"dbo.Players": 12, "dbo.Battles": 340, "dbo.Guilds": 1})

        code, _, _ = self.run_drill()

        self.assertEqual(code, 0)

    def test_missing_tables_on_the_same_migration_fail(self):
        self.databases["GameDb-drill-202610071230"] = database(**{"dbo.Players": 12})

        code, _, errors = self.run_drill()

        self.assertEqual(code, 1)
        self.assertIn("dbo.Battles", errors)

    def test_the_database_token_is_fetched_before_the_restore(self):
        # In GitHub Actions the CLI can't get a token for a new resource once its 5-minute OIDC sign-in
        # has expired, and a restore takes far longer than that.
        self.run_drill()

        commands = [" ".join(call[:3]) for call in self.azure.calls]
        self.assertLess(commands.index("account get-access-token --resource"), commands.index("sql db restore"))

    def test_a_passing_drill_is_recorded_on_the_live_database_for_the_status_page(self):
        self.run_drill()

        [(name, passed, detail)] = self.recorded
        self.assertEqual(name, "GameDb")
        self.assertTrue(passed)
        self.assertRegex(detail, r"^Restored in \d+\.\d minutes; all 2 tables checked")

    def test_a_failing_drill_is_recorded_as_failed(self):
        self.databases["GameDb-drill-202610071230"] = database(migrations=())

        self.run_drill()

        [(_, passed, detail)] = self.recorded
        self.assertFalse(passed)
        self.assertIn("no migration history", detail)

    def test_a_failed_restore_is_recorded_without_error_details(self):
        # The status page is public; the az error stays in the workflow log.
        self.azure.fail_restore = True

        self.run_drill()

        [(_, passed, detail)] = self.recorded
        self.assertFalse(passed)
        self.assertNotIn("Conflict", detail)

    def test_recording_the_result_never_fails_the_drill(self):
        failure = subprocess.CompletedProcess([], 1, stdout="", stderr="Invalid object name 'OperationsEvents'.")
        with mock.patch.object(drill, "database_app", return_value=failure), redirect_stderr(io.StringIO()) as errors:
            REAL_RECORD_RESULT(SERVER, "GameDb", "token", True, "Restored")

        self.assertIn("::warning::Couldn't record the result", errors.getvalue())

    def test_a_restore_point_older_than_the_backups_fails_before_touching_anything(self):
        self.azure.earliest = "2026-10-07T12:25:00Z"

        code, _, errors = self.run_drill()

        self.assertEqual(code, 1)
        self.assertIn("Backups only go back to", errors)
        self.assertFalse(self.azure.ran("sql server firewall-rule create"))
        self.assertFalse(self.azure.ran("sql db restore"))

    def test_a_failed_restore_still_cleans_up(self):
        self.azure.fail_restore = True

        code, _, errors = self.run_drill()

        self.assertEqual(code, 1)
        self.assertIn("Conflict", errors)
        self.assertTrue(self.azure.ran("sql db delete"))
        self.assertTrue(self.azure.ran("sql server firewall-rule delete"))

    def test_keep_leaves_the_copy_for_a_closer_look(self):
        code, _, errors = self.run_drill("--keep")

        self.assertEqual(code, 0)
        self.assertFalse(self.azure.ran("sql db delete"))
        self.assertIn("Kept GameDb-drill-202610071230", errors)
        self.assertTrue(self.azure.ran("sql server firewall-rule delete"))

    def test_the_server_comes_from_the_latest_deployment_unless_named(self):
        self.run_drill()
        self.assertTrue(self.azure.ran("deployment group list"))

        self.azure.calls.clear()
        self.run_drill("--server", "sql-other.database.windows.net")
        self.assertFalse(self.azure.ran("deployment group list"))
        self.assertEqual(self.azure.option(self.azure.ran("sql db restore")[0], "--server"), "sql-other")


if __name__ == "__main__":
    unittest.main()
