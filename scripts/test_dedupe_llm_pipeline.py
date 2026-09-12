"""
Unit tests for the execution-safety layer of dedupe_llm_pipeline.

The pipeline still drives a remote PostgreSQL via SSH (intentional design
choice — LLM is the right tool for semantic clustering and the LLM calls
live outside the C# codebase). These tests cover the bits that would
otherwise be silent: signed-report gate, post-filter re-derivation, and
report-vs-live drift detection. The DB-touching paths are exercised in
production.
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(os.path.dirname(__file__), "dedupe_llm_pipeline.py")


class _StubRunPsql:
    """Avoid touching the real production DB during tests."""

    def __init__(self):
        self.calls = []

    def __call__(self, sql):
        self.calls.append(sql)
        return "OK"


def _load_module():
    spec_path = SCRIPT
    if not os.path.exists(spec_path):
        raise FileNotFoundError(f"Script not found: {spec_path}")
    import importlib.util
    spec = importlib.util.spec_from_file_location("dedupe_llm_pipeline", spec_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FilterReproTest(unittest.TestCase):
    """The pre-fix bug: filter only shrank the displayed sample, but Apply
    kept the unfiltered removal set."""

    def test_filter_must_rebuild_loser_set(self):
        mod = _load_module()
        groups = [
            {
                "cluster_name": "wujun - rose [STUDIO_MASTER]",
                "winner": {"Id": 1},
                "losers": [{"Id": 2}, {"Id": 3}],
            },
            {
                "cluster_name": "someone else - other [STUDIO_MASTER]",
                "winner": {"Id": 10},
                "losers": [{"Id": 11}],
            },
        ]
        result = mod.apply_filter_to_losers(groups, "wujun")
        self.assertEqual(result, [2, 3])

    def test_no_filter_returns_all(self):
        mod = _load_module()
        groups = [
            {
                "cluster_name": "a - b",
                "winner": {"Id": 1},
                "losers": [{"Id": 2}, {"Id": 3}],
            },
        ]
        self.assertEqual(mod.apply_filter_to_losers(groups, None), [2, 3])


class SignedReportTest(unittest.TestCase):
    """SHA-256 sidecar: Apply must verify the file before mutating the DB."""

    def test_write_and_verify_round_trip(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "report.json")
            sha = mod.write_signed_report(out, {"soft_deleted_ids": [1, 2, 3]})
            self.assertEqual(len(sha), 64)
            self.assertTrue(os.path.exists(out + ".sha256"))
            manifest, actual_sha = mod.verify_signed_report(out, sha)
            self.assertEqual(actual_sha, sha)
            self.assertEqual(manifest["soft_deleted_ids"], [1, 2, 3])

    def test_verify_rejects_tampered_file(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "report.json")
            sha = mod.write_signed_report(out, {"soft_deleted_ids": [1]})
            with open(out, "w", encoding="utf-8") as f:
                f.write(json.dumps({"soft_deleted_ids": [999]}))
            with self.assertRaises(SystemExit) as ctx:
                mod.verify_signed_report(out, sha)
            self.assertIn("SHA-256 mismatch", str(ctx.exception))

    def test_verify_rejects_missing_sidecar(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "report.json")
            mod.write_signed_report(out, {"soft_deleted_ids": [1]})
            os.remove(out + ".sha256")
            with self.assertRaises(SystemExit) as ctx:
                mod.verify_signed_report(out, "deadbeef" * 8)
            self.assertIn("sidecar missing", str(ctx.exception))

    def test_verify_rejects_empty_sha(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "report.json")
            mod.write_signed_report(out, {"soft_deleted_ids": [1]})
            with self.assertRaises(SystemExit) as ctx:
                mod.verify_signed_report(out, "")
            self.assertIn("without --report-sha", str(ctx.exception))


class MainCLIGatesTest(unittest.TestCase):
    """Apply must abort without a signed report and matching hash, and must
    refuse to proceed when the on-disk report has drifted from a fresh
    dry-run."""

    def _make_groups(self, count):
        return [
            {
                "cluster_name": f"art{i} - song{i}",
                "winner": {"Id": 1000 + i, "FilePath": f"/p/{i}.mp3"},
                "losers": [{"Id": 2000 + i, "FilePath": f"/p/{i}_a.mp3"}],
            }
            for i in range(count)
        ]

    def test_apply_without_report_aborts(self):
        mod = _load_module()
        # Patch all DB-touching entry points so the test stays offline.
        mod.load_active_nas_candidates = lambda: []
        mod.cluster_and_classify_candidates = lambda x: []
        mod.evaluate_semantic_clusters = lambda cs: ([], 0)
        mod.write_signed_report = lambda *a, **k: (_ for _ in ()).throw(
            AssertionError("must not write a report when --out is absent")
        )
        captured = _StubRunPsql()
        mod.run_psql = captured
        old_argv = sys.argv
        try:
            sys.argv = ["dedupe_llm_pipeline.py", "--apply"]
            with self.assertRaises(SystemExit) as ctx:
                mod.main()
        finally:
            sys.argv = old_argv
        self.assertIn("signed dry-run report", str(ctx.exception))

    def test_apply_with_matching_sha_proceeds(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "r.json")
            mod.load_active_nas_candidates = lambda: []
            mod.cluster_and_classify_candidates = lambda x: [("c", x[:])]
            mod.evaluate_semantic_clusters = lambda cs: (
                [
                    {"cluster_name": "c", "winner": {"Id": 1, "FilePath": "/p", "Album": "X", "Genre": "Pop", "Year": 2020, "kbps": 1000, "keep_score": 100},
                     "losers": [{"Id": 2, "FilePath": "/q", "Album": "X", "Genre": "Pop", "Year": 2020, "kbps": 800, "keep_score": 80}]}
                ],
                1,
            )
            sha = mod.write_signed_report(out, {
                "soft_deleted_ids": [2],
                "groups": [{
                    "cluster_name": "c", "winner_id": 1, "winner_path": "/p",
                    "winner_score": 0, "losers": [{"id": 2, "path": "/q"}],
                }],
            })
            captured = _StubRunPsql()
            mod.run_psql = captured
            old_argv = sys.argv
            try:
                sys.argv = [
                    "dedupe_llm_pipeline.py", "--apply",
                    f"--out={out}", f"--report-sha={sha}",
                ]
                mod.main()
            finally:
                sys.argv = old_argv
            sqls = "\n".join(captured.calls)
            self.assertIn('BEGIN;', sqls)
            self.assertIn('COMMIT;', sqls)
            self.assertIn('UPDATE "MediaFiles" SET "IsDeleted" = true', sqls)
            self.assertIn('WHERE "Id" IN (2)', sqls)

    def test_apply_with_drift_aborts(self):
        mod = _load_module()
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "r.json")
            sha = mod.write_signed_report(out, {
                "soft_deleted_ids": [2],
                "groups": [{
                    "cluster_name": "c", "winner_id": 1, "winner_path": "/p",
                    "winner_score": 0, "losers": [{"id": 2, "path": "/q"}],
                }],
            })
            captured = _StubRunPsql()
            mod.run_psql = captured
            mod.load_active_nas_candidates = lambda: []
            mod.cluster_and_classify_candidates = lambda x: [("c", x[:])]
            mod.evaluate_semantic_clusters = lambda cs: (
                [
                    {"cluster_name": "c", "winner": {"Id": 1, "FilePath": "/p", "Album": "X", "Genre": "Pop", "Year": 2020, "kbps": 1000, "keep_score": 100},
                     "losers": [{"Id": 3, "FilePath": "/r", "Album": "X", "Genre": "Pop", "Year": 2020, "kbps": 800, "keep_score": 80}]}
                ],
                1,
            )
            old_argv = sys.argv
            try:
                sys.argv = [
                    "dedupe_llm_pipeline.py", "--apply",
                    f"--out={out}", f"--report-sha={sha}",
                ]
                with self.assertRaises(SystemExit) as ctx:
                    mod.main()
            finally:
                sys.argv = old_argv
            self.assertIn("drift detected", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
