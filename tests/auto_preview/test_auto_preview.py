"""Counterexample tests for tools/auto_preview (python3 -m unittest tests.auto_preview.test_auto_preview).

Pure-logic tests always run. Tests that need the local-only native base / a real official `ait build`
(Builds/ is gitignored) skip with a reason when those files are absent (e.g. in CI)."""
from __future__ import annotations

import copy
import datetime as dt
import http.server
import json
import os
import re
import shutil
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "auto_preview"))
import native_base as nb  # noqa: E402
import pipeline as pl  # noqa: E402
from redact import Redactor  # noqa: E402

FAKE_AIT = Path(__file__).resolve().parent / "fake_ait.py"
LOCAL = ROOT / "Builds" / "AutoPreviewPreparation-2026-10-27"
NATIVE_TAR = LOCAL / "native-base" / "few-moves-native-base-r3.tar.gz"
DRY_RUN = LOCAL / "dry-run-3"
DEP = "01a17d97-eabf-7ff0-9651-8e1c4ab05fae"
HEAD = pl.git("rev-parse", "HEAD").strip()


def env(**kw):
    base = {"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REPOSITORY": pl.TRUSTED_REPOSITORY, "GITHUB_REF": "refs/heads/codex/x",
            "PREVIEW_MODE": "dry-run", "FEW_MOVES_NATIVE_BASE_TAG": "native-base-r3"}
    base.update(kw)
    return base


class GateTests(unittest.TestCase):
    T = "refs/heads/codex/few-moves-preview"

    def test_default_off_and_refusals(self):
        self.assertEqual(pl.TRUSTED_REF, self.T)
        t = {"GITHUB_REF": self.T}
        self.assertEqual(pl.gate(env(GITHUB_EVENT_NAME="pull_request", **t))["state"], "refused")
        self.assertEqual(pl.gate(env(GITHUB_EVENT_NAME="pull_request_target", **t))["state"], "refused")
        self.assertEqual(pl.gate(env(GITHUB_REPOSITORY="someone/fork", **t))["state"], "refused")
        self.assertEqual(pl.gate(env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/heads/main"))["state"], "refused")
        self.assertEqual(pl.gate(env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/heads/release/few-moves-preview"))["state"], "refused")
        self.assertEqual(pl.gate(env(FEW_MOVES_NATIVE_BASE_TAG="", **t))["state"], "off")
        self.assertEqual(pl.gate(env(PREVIEW_MODE="publish", **t))["state"], "off", "publish needs the explicit enable variable")
        self.assertEqual(pl.gate(env(PREVIEW_MODE="release", **t))["state"], "refused")

    def test_dispatch_is_also_bound_to_the_trusted_ref(self):
        for mode in ("dry-run", "publish"):
            for ref in ("refs/heads/codex/x", "refs/tags/v1", "refs/heads/main"):
                self.assertEqual(pl.gate(env(GITHUB_REF=ref, PREVIEW_MODE=mode, FEW_MOVES_PREVIEW_ENABLED="true"))["state"], "refused", (mode, ref))
        armed = pl.gate(env(GITHUB_REF=self.T, PREVIEW_MODE="publish", FEW_MOVES_PREVIEW_ENABLED="true"))
        self.assertEqual((armed["state"], armed["run_publish"]), ("armed", True))
        self.assertEqual(pl.gate(env(GITHUB_REF=self.T, PREVIEW_MODE="publish", FEW_MOVES_PREVIEW_ENABLED="TRUE"))["state"], "off", "exact 'true' only")

    def test_dry_run_paths(self):
        dry = pl.gate(env(GITHUB_REF=self.T))
        self.assertEqual((dry["state"], dry["run_build"], dry["run_publish"]), ("dry_run", True, False))
        push_off = pl.gate(env(GITHUB_EVENT_NAME="push", GITHUB_REF=self.T))
        self.assertEqual((push_off["state"], push_off["run_build"], push_off["run_publish"]), ("dry_run", True, False),
                         "trusted push without the enable variable builds and verifies only")
        self.assertEqual(pl.gate(env(GITHUB_EVENT_NAME="push", GITHUB_REF=self.T, FEW_MOVES_PREVIEW_ENABLED="true"))["state"], "armed")


class ClassifyTests(unittest.TestCase):
    def test_categories(self):
        self.assertEqual(pl.classify_path("Assets/Nectorial/Runtime/GameBootstrap.cs"), "native")
        self.assertEqual(pl.classify_path("Assets/WebGLTemplates/TurnEscape/index.html"), "native", "Unity bakes templates")
        self.assertEqual(pl.classify_path("ProjectSettings/ProjectSettings.asset"), "native")
        self.assertEqual(pl.classify_path("Packages/manifest.json"), "native")
        self.assertEqual(pl.classify_path("web/platforms/toss/r6/toss-ui.css"), "ui_overlay")
        self.assertEqual(pl.classify_path("web/content/style.css"), "ui_overlay")
        self.assertEqual(pl.classify_path("tools/prepare_solo_release.py"), "ui_overlay")
        self.assertEqual(pl.classify_path("tools/auto_preview/pipeline.py"), "pipeline")
        self.assertEqual(pl.classify_path("servers/FewMoves.Coop/Program.cs"), "other")

    def test_every_changed_path_is_listed(self):
        result = pl.classify(None)
        listed = {f["path"] for f in result["files"]}
        porcelain = {line[3:] for line in pl.git("status", "--porcelain=v1", "--untracked-files=all").splitlines()}
        self.assertEqual(listed, porcelain)
        self.assertEqual(sum(result["counts"].values()), len(listed))


class NativeBaseTests(unittest.TestCase):
    def setUp(self):
        self.files = {"solo/Build/a.wasm": b"w", "raid/Build/b.data": b"d", "shared/Build/c.wasm": b"s", nb.BRIDGE_REL: b"bridge"}
        self.data = nb.deterministic_tar(self.files)
        self.lock = {"tar": {"sha256": nb.sha256_bytes(self.data), "bytes": len(self.data)},
                     "files": {k: {"sha256": nb.sha256_bytes(v), "bytes": len(v)} for k, v in self.files.items()}}

    def test_deterministic_and_verifies(self):
        self.assertEqual(self.data, nb.deterministic_tar(dict(reversed(list(self.files.items())))))
        self.assertEqual(nb.verify_tar(self.data, self.lock), self.files)

    def test_wrong_sha_broken_extra_unsafe(self):
        broken = bytearray(self.data); broken[len(broken) // 2] ^= 0xFF
        with self.assertRaisesRegex(nb.NativeBaseError, "sha256/bytes"):
            nb.verify_tar(bytes(broken), self.lock)
        extra = nb.deterministic_tar({**self.files, "solo/Build/x.js": b"x"})
        with self.assertRaisesRegex(nb.NativeBaseError, "file set differs"):
            nb.verify_tar(extra, {**self.lock, "tar": {"sha256": nb.sha256_bytes(extra), "bytes": len(extra)}})
        changed = nb.deterministic_tar({**self.files, "raid/Build/b.data": b"D"})
        with self.assertRaisesRegex(nb.NativeBaseError, "file differs: raid/Build/b.data"):
            nb.verify_tar(changed, {**self.lock, "tar": {"sha256": nb.sha256_bytes(changed), "bytes": len(changed)}})
        unsafe = nb.deterministic_tar({"../escape": b"x"})
        with self.assertRaisesRegex(nb.NativeBaseError, "unsafe"):
            nb.read_tar(unsafe)

    def test_committed_lock_binding_and_shape(self):
        lock = nb.load_lock()
        self.assertEqual(lock["binding"], pl.BIND)
        self.assertEqual(len(lock["tar"]["sha256"]), 64)
        self.assertIn(nb.BRIDGE_REL, lock["files"])
        self.assertTrue(lock["native_inputs"]["files"])

    def test_native_change_stops_with_rebuild_request(self):
        lock = copy.deepcopy(nb.load_lock())
        current = nb.native_input_fingerprint()
        target = "Assets/Nectorial/Runtime/GameBootstrap.cs"
        lock["native_inputs"]["files"] = {**current, target: "0" * 64}  # modified
        with tempfile.TemporaryDirectory() as tmp, self.assertRaises(pl.Stop) as stop:
            pl.check_native(lock, Path(tmp))
        self.assertEqual(stop.exception.code, pl.EXIT_NATIVE)
        lock["native_inputs"]["files"] = {k: v for k, v in current.items() if k != target}  # file added since the lock
        with tempfile.TemporaryDirectory() as tmp, self.assertRaises(pl.Stop) as stop:
            pl.check_native(lock, Path(tmp))
        self.assertEqual(stop.exception.code, pl.EXIT_NATIVE)
        with tempfile.TemporaryDirectory() as tmp:
            lock["native_inputs"]["files"] = current
            pl.check_native(lock, Path(tmp))
            self.assertFalse((Path(tmp) / "native-rebuild-request.json").exists())

    def test_rebuild_request_content(self):
        lock = copy.deepcopy(nb.load_lock())
        lock["native_inputs"]["files"] = {**nb.native_input_fingerprint(), "ProjectSettings/ProjectSettings.asset": "f" * 64}
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(pl.Stop):
                pl.check_native(lock, Path(tmp))
            request = json.loads((Path(tmp) / "native-rebuild-request.json").read_text())
        self.assertEqual(request["state"], "blocked_native_change")
        self.assertEqual([c["path"] for c in request["changes"]], ["ProjectSettings/ProjectSettings.asset"])
        self.assertIn("Licensed Unity", request["needed"][0])


def run(**over):
    base = {"id": 1, "path": ".github/workflows/verify.yml", "name": "Verify", "head_sha": HEAD, "head_branch": "codex/few-moves-preview",
            "event": "push", "status": "completed", "conclusion": "success", "created_at": "2026-10-27T10:00:00Z", "run_attempt": 1,
            "repository": {"full_name": pl.TRUSTED_REPOSITORY}, "head_repository": {"full_name": pl.TRUSTED_REPOSITORY}}
    base.update(over)
    return base


class StatusServer(http.server.BaseHTTPRequestHandler):
    reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "CREATED"}}
    verify_replies = [[run()]]  # consumed one per request; the last repeats
    verify_status = 200
    seen = []

    def do_GET(self):
        StatusServer.seen.append((self.path, self.headers.get("Authorization")))
        if "/actions/workflows/verify.yml/runs" in self.path:
            if StatusServer.verify_status != 200:
                self.send_response(StatusServer.verify_status); self.end_headers(); return
            runs = StatusServer.verify_replies.pop(0) if len(StatusServer.verify_replies) > 1 else StatusServer.verify_replies[0]
            body = json.dumps({"total_count": len(runs), "workflow_runs": runs}).encode()
        else:
            body = json.dumps(StatusServer.reply).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers(); self.wfile.write(body)

    def log_message(self, *args):
        pass


class PublishTests(unittest.TestCase):
    KEY = "test-deploy-key-0123456789"

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.ait = self.tmp / "few-moves.ait"
        self.ait.write_bytes(b"AITBUNDL" + b"x" * 100)
        self.receipt = {"kind": "few-moves-toss-preview-build", "state": "verified", "created_utc": pl.now(), "commit": HEAD, "binding": pl.BIND,
                        "native_base": {"lock_sha256": nb.sha256_file(nb.LOCK)},
                        "ait": {"sha256": nb.sha256_file(self.ait), "deploymentId": DEP, "content_sha256": "c" * 64}}
        self.write_receipt()
        self.ledger = self.tmp / "ledger.json"
        self.calls = self.tmp / "calls.jsonl"
        self.server = http.server.HTTPServer(("127.0.0.1", 0), StatusServer)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        StatusServer.seen, StatusServer.reply = [], {"resultType": "SUCCESS", "success": {"reviewStatus": "CREATED"}}
        StatusServer.verify_replies, StatusServer.verify_status = [[run()]], 200
        self.cred = self.tmp / "runner-home" / ".ait" / "credentials"
        self.cred.parent.parent.mkdir()
        self.env = {"AUTO_PREVIEW_TEST_FAKE_AIT": str(FAKE_AIT), "AUTO_PREVIEW_TEST_STATUS_BASE": f"http://127.0.0.1:{self.server.server_port}/api",
                    "AUTO_PREVIEW_TEST_GITHUB_API": f"http://127.0.0.1:{self.server.server_port}", "AUTO_PREVIEW_TEST_VERIFY_POLL": "3,0",
                    "AUTO_PREVIEW_TEST_CREDENTIALS": str(self.cred), "FAKE_AIT_CREDENTIALS": str(self.cred), "AUTO_PREVIEW_TEST_REMOTE_HEAD": HEAD,
                    "FAKE_EXPECT_HOME": os.environ.get("HOME", ""), "GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted",
                    "FAKE_AIT_DEPLOYMENT_ID": DEP, "FAKE_AIT_CALLS": str(self.calls), "GITHUB_SHA": HEAD, "FEW_MOVES_AIT_DEPLOY_KEY": self.KEY}
        self.home_before = os.environ.get("HOME")
        os.chmod(FAKE_AIT, 0o755)

    def tearDown(self):
        self.server.shutdown()
        self.assertEqual(os.environ.get("HOME"), self.home_before, "HOME never changed")
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write_receipt(self):
        (self.tmp / "build-receipt.json").write_text(json.dumps(self.receipt))

    def publish(self, *extra, behavior="success", **env_over):
        args = ["publish", "--receipt", str(self.tmp / "build-receipt.json"), "--ait", str(self.ait), "--project", str(self.tmp),
                "--ledger", str(self.ledger), "--out", str(self.tmp / "publish.json"), "--log", str(self.tmp / "publish.log"), *extra]
        with mock.patch.dict(os.environ, {**self.env, "FAKE_AIT_BEHAVIOR": behavior, **env_over}, clear=False):
            return pl.main(args)

    def calls_made(self):
        return self.calls.read_text().splitlines() if self.calls.exists() else []

    def test_success_records_link_and_status_and_never_tests(self):
        self.assertEqual(self.publish(), 0)
        out = json.loads((self.tmp / "publish.json").read_text())
        self.assertEqual((out["state"], out["uploaded"]), ("uploaded", True))
        self.assertEqual(out["private_link"], f"intoss-private://few-moves?_deploymentId={DEP}&host=appsInTossHost")
        self.assertEqual((out["isTested_ready"], out["user_tested"]), (False, False))
        self.assertTrue(out["fake_cli_for_tests"])
        self.assertEqual(json.loads(self.ledger.read_text())["entries"][0]["status"], "created")
        status_calls = [c for c in StatusServer.seen if "/deployments/" in c[0]]
        self.assertEqual(status_calls, [(f"/api/bundles/few-moves/deployments/{DEP}", f"Bearer {self.KEY}")])
        verify_calls = [c for c in StatusServer.seen if "/actions/" in c[0]]
        self.assertTrue(verify_calls and all(c[1] != f"Bearer {self.KEY}" for c in verify_calls), "deploy key never sent to GitHub")
        self.assertEqual(out["verify_gate"]["verify_run_id"], 1)
        self.assertEqual(len(self.calls_made()), 1)

    def test_same_deployment_or_content_is_not_uploaded_again(self):
        self.assertEqual(self.publish(), 0)
        self.assertEqual(self.publish(), 0)
        self.assertEqual(json.loads((self.tmp / "publish.json").read_text())["state"], "skipped_duplicate_content")
        self.assertEqual(len(self.calls_made()), 1, "no second upload, no second notification")

    def test_failure_then_resume_never_reuploads(self):
        self.assertEqual(self.publish(behavior="fail"), pl.EXIT_FAIL)
        entry = json.loads(self.ledger.read_text())["entries"][0]
        self.assertEqual(entry["status"], "unknown_after_failure")
        self.assertEqual(self.publish(), pl.EXIT_REFUSED, "re-run must not upload the same deployment again")
        self.assertEqual(len(self.calls_made()), 1)
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), 0)
        self.assertEqual(json.loads(self.ledger.read_text())["entries"][0]["status"], "created")
        self.assertEqual(self.publish(), 0)
        self.assertEqual(json.loads((self.tmp / "publish.json").read_text())["state"], "skipped_duplicate_content")

    def test_previous_success_is_preserved_on_new_failure(self):
        self.assertEqual(self.publish(), 0)
        first = json.loads(self.ledger.read_text())["entries"][0]
        self.ait.write_bytes(b"AITBUNDL" + b"y" * 100)
        self.receipt["ait"] = {"sha256": nb.sha256_file(self.ait), "deploymentId": "01a17d97-0000-7000-8000-000000000001", "content_sha256": "d" * 64}
        self.write_receipt()
        self.assertEqual(self.publish(behavior="fail", FAKE_AIT_DEPLOYMENT_ID="01a17d97-0000-7000-8000-000000000001"), pl.EXIT_FAIL)
        entries = json.loads(self.ledger.read_text())["entries"]
        self.assertEqual(entries[0], first)
        self.assertEqual(entries[1]["status"], "unknown_after_failure")

    def test_stale_or_wrong_inputs_are_refused_before_any_call(self):
        self.ait.write_bytes(b"AITBUNDL tampered")
        self.assertEqual(self.publish(), pl.EXIT_REFUSED)
        self.ait.write_bytes(b"AITBUNDL" + b"x" * 100)
        self.assertEqual(self.publish("--approved-ait-sha", "0" * 64), pl.EXIT_REFUSED)
        self.receipt["commit"] = "0" * 40; self.write_receipt()
        self.assertEqual(self.publish(), pl.EXIT_REFUSED)
        self.receipt["commit"] = HEAD
        self.receipt["created_utc"] = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=30)).strftime("%Y-%m-%dT%H:%M:%SZ"); self.write_receipt()
        self.assertEqual(self.publish(), pl.EXIT_REFUSED)
        self.receipt["created_utc"] = pl.now()
        self.receipt["binding"] = {**pl.BIND, "appName": "other-app"}; self.write_receipt()
        self.assertEqual(self.publish(), pl.EXIT_INPUT)
        self.receipt["binding"] = {**pl.BIND, "miniAppId": 1}; self.write_receipt()
        self.assertEqual(self.publish(), pl.EXIT_INPUT)
        self.receipt["binding"] = pl.BIND
        self.receipt["native_base"]["lock_sha256"] = "0" * 64; self.write_receipt()
        self.assertEqual(self.publish(), pl.EXIT_REFUSED)
        self.assertEqual(self.calls_made(), [])
        self.assertFalse(self.ledger.exists())

    def test_missing_key_refuses_without_prompting(self):
        self.assertEqual(self.publish(FEW_MOVES_AIT_DEPLOY_KEY=""), pl.EXIT_INPUT)
        self.assertEqual(self.calls_made(), [])

    def test_other_app_ledger_refused(self):
        self.ledger.write_text(json.dumps({"appName": "other", "miniAppId": 1, "entries": []}))
        self.assertEqual(self.publish(), pl.EXIT_INPUT)

    def test_dry_run_reads_no_key_and_calls_nothing(self):
        self.assertEqual(self.publish("--dry-run", FEW_MOVES_AIT_DEPLOY_KEY=""), 0)
        self.assertEqual(json.loads((self.tmp / "publish.json").read_text())["state"], "dry_run_would_upload")
        self.assertEqual((self.calls_made(), StatusServer.seen), ([], []))
        self.assertFalse(self.ledger.exists())

    def test_logs_and_receipts_are_redacted(self):
        self.assertEqual(self.publish(behavior="leak"), 0)
        for name in ("publish.log", "publish.json", "ledger.json"):
            text = (self.tmp / name).read_text()
            self.assertNotIn(self.KEY, text, name)
            self.assertNotIn("X-Amz-Signature=abc", text, name)

    def rebuild(self, dep, content, fill=b"r"):
        self.ait.write_bytes(b"AITBUNDL" + fill * 100)
        self.receipt["ait"] = {"sha256": nb.sha256_file(self.ait), "deploymentId": dep, "content_sha256": content}
        self.write_receipt()

    def entries(self):
        return {e["deploymentId"]: e["status"] for e in json.loads(self.ledger.read_text())["entries"]}

    def test_not_created_is_not_reported_as_deployment(self):
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "BUILDING"}}
        self.assertEqual(self.publish(), pl.EXIT_FAIL)
        out = json.loads((self.tmp / "publish.json").read_text())
        self.assertEqual((out["state"], out["uploaded"]), ("uploaded_not_created", False))
        self.assertEqual(self.entries(), {DEP: "uploaded_unconfirmed"})

    def test_unconfirmed_upload_blocks_all_new_uploads_until_status(self):
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "PREPARE"}}
        self.assertEqual(self.publish(), pl.EXIT_FAIL)
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "CREATED"}}
        id_same, id_other = "01a17d97-0000-7000-8000-0000000000b1", "01a17d97-0000-7000-8000-0000000000b2"
        self.rebuild(id_same, "c" * 64, b"s")  # same content, new deploymentId: must not be skipped as uploaded
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=id_same), pl.EXIT_REFUSED)
        self.rebuild(id_other, "9" * 64, b"o")  # different content, new deploymentId
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=id_other), pl.EXIT_REFUSED)
        self.assertEqual(len(self.calls_made()), 1)
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), 0)
        self.assertEqual(self.entries()[DEP], "created")
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=id_other), 0, "after confirmation, different content proceeds")
        self.assertEqual(len(self.calls_made()), 2)

    def test_still_building_status_stays_pending(self):
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "BUILDING"}}
        self.assertEqual(self.publish(behavior="fail"), pl.EXIT_FAIL)
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), 0)
        self.assertEqual(self.entries()[DEP], "uploaded_unconfirmed")
        self.rebuild("01a17d97-0000-7000-8000-0000000000b3", "8" * 64)
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID="01a17d97-0000-7000-8000-0000000000b3"), pl.EXIT_REFUSED)

    def test_build_failed_deployment_is_never_redeployed(self):
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "BUILD_FAILED"}}
        self.assertEqual(self.publish(), pl.EXIT_FAIL)  # CLI ok, server then reports BUILD_FAILED
        self.assertEqual(self.entries(), {DEP: "build_failed"})
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "CREATED"}}
        self.assertEqual(self.publish(), pl.EXIT_REFUSED, "same deploymentId again")
        self.rebuild("01a17d97-0000-7000-8000-0000000000c1", "c" * 64)
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID="01a17d97-0000-7000-8000-0000000000c1"), pl.EXIT_REFUSED, "same failed content")
        self.assertEqual(len(self.calls_made()), 1)
        self.rebuild("01a17d97-0000-7000-8000-0000000000c2", "7" * 64)
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID="01a17d97-0000-7000-8000-0000000000c2"), 0, "changed content may upload")

    def test_dry_run_and_skip_never_claim_upload(self):
        self.assertEqual(self.publish("--dry-run", FEW_MOVES_AIT_DEPLOY_KEY=""), 0)
        self.assertIs(json.loads((self.tmp / "publish.json").read_text())["uploaded"], False)

    def test_credentials_created_exclusively_and_removed(self):
        self.assertEqual(self.publish(), 0)
        self.assertFalse(self.cred.exists(), "our credentials file is removed after success")
        self.assertFalse(self.cred.parent.exists(), "the .ait dir we created is removed")
        self.assertEqual(self.publish(behavior="fail", FAKE_AIT_DEPLOYMENT_ID=DEP), 0, "duplicate content skips before any credentials")
        self.assertFalse(self.cred.exists())

    def test_existing_credentials_file_is_never_touched(self):
        self.cred.parent.mkdir()
        self.cred.write_text('{"someone": "theirs"}')
        self.assertEqual(self.publish(), pl.EXIT_INPUT)
        self.assertEqual(self.cred.read_text(), '{"someone": "theirs"}')
        self.assertEqual(self.calls_made(), [])

    def test_existing_ait_dir_is_kept(self):
        self.cred.parent.mkdir()
        (self.cred.parent / "other").write_text("x")
        self.assertEqual(self.publish(), 0)
        self.assertFalse(self.cred.exists())
        self.assertTrue((self.cred.parent / "other").exists())

    def test_credentials_removed_after_failure(self):
        self.assertEqual(self.publish(behavior="fail"), pl.EXIT_FAIL)
        self.assertFalse(self.cred.exists())

    def test_local_publish_and_status_are_ci_only(self):
        self.assertEqual(self.publish(GITHUB_ACTIONS="", RUNNER_ENVIRONMENT=""), pl.EXIT_INPUT)
        self.assertEqual(self.publish(RUNNER_ENVIRONMENT="self-hosted"), pl.EXIT_INPUT)
        self.assertFalse(self.cred.exists())
        self.assertEqual(self.calls_made(), [])
        with mock.patch.dict(os.environ, {**self.env, "GITHUB_ACTIONS": ""}):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), pl.EXIT_INPUT)

    def test_dry_run_writes_no_credentials(self):
        self.assertEqual(self.publish("--dry-run"), 0)
        self.assertFalse(self.cred.exists())
        self.assertFalse(self.cred.parent.exists())

    def test_trusted_branch_head_must_be_the_receipt_commit(self):
        self.assertEqual(self.publish(AUTO_PREVIEW_TEST_REMOTE_HEAD="f" * 40), pl.EXIT_REFUSED)
        self.assertEqual(self.publish(AUTO_PREVIEW_TEST_REMOTE_HEAD="<network-error>"), pl.EXIT_REFUSED)
        self.assertEqual(self.calls_made(), [])
        self.assertFalse(self.ledger.exists(), "nothing recorded before the remote check passes")

    def test_unknown_previous_upload_blocks_same_content_with_new_id(self):
        self.assertEqual(self.publish(behavior="fail"), pl.EXIT_FAIL)
        new_id = "01a17d97-0000-7000-8000-0000000000aa"
        self.ait.write_bytes(b"AITBUNDL" + b"z" * 100)  # rebuilt: same content, new deploymentId
        self.receipt["ait"] = {"sha256": nb.sha256_file(self.ait), "deploymentId": new_id, "content_sha256": "c" * 64}
        self.write_receipt()
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=new_id), pl.EXIT_REFUSED)
        self.receipt["ait"]["content_sha256"] = "e" * 64  # even different content waits
        self.write_receipt()
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=new_id), pl.EXIT_REFUSED)
        self.assertEqual(len(self.calls_made()), 1)
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), 0)
        self.receipt["ait"]["content_sha256"] = "c" * 64
        self.write_receipt()
        self.assertEqual(self.publish(FAKE_AIT_DEPLOYMENT_ID=new_id), 0)
        self.assertEqual(json.loads((self.tmp / "publish.json").read_text())["state"], "skipped_duplicate_content")
        self.assertEqual(len(self.calls_made()), 1, "original ID confirmed; no second upload")

    def gate_refuses(self, replies, status=200):
        StatusServer.verify_replies, StatusServer.verify_status = replies, status
        self.assertEqual(self.publish(FEW_MOVES_AIT_DEPLOY_KEY=""), pl.EXIT_REFUSED, "gate refuses before the key is even needed")
        self.assertEqual(self.calls_made(), [])
        self.assertFalse(self.ledger.exists())
        self.assertFalse(self.cred.exists())

    def test_verify_gate_counterexamples(self):
        self.gate_refuses([[run(conclusion="failure")]])
        self.gate_refuses([[run(conclusion="cancelled")]])
        self.gate_refuses([[]])
        self.gate_refuses([[run(status="in_progress", conclusion=None)]])
        self.gate_refuses([[run(path=".github/workflows/other.yml")]])
        self.gate_refuses([[run(head_sha="0" * 40)]])
        self.gate_refuses([[run(head_branch="main")]])
        self.gate_refuses([[run(event="pull_request")]])
        self.gate_refuses([[run(event="workflow_dispatch")]])
        self.gate_refuses([[run(head_repository={"full_name": "someone/fork"})]])
        self.gate_refuses([[run(repository={"full_name": "someone/fork"})]])
        self.gate_refuses([[run(id=1, created_at="2026-10-27T10:00:00Z"), run(id=2, created_at="2026-10-27T11:00:00Z", conclusion="failure")]])
        self.gate_refuses([[run(id=1, run_attempt=1), run(id=1, run_attempt=2, conclusion="failure")]])
        self.gate_refuses([[run()]], status=500)
        self.gate_refuses([[run()]], status=403)

    def test_verify_gate_bounded_poll_then_success(self):
        StatusServer.verify_replies = [[run(status="queued", conclusion=None)], [run(status="in_progress", conclusion=None)], [run()]]
        self.assertEqual(self.publish(), 0)
        self.assertEqual(json.loads((self.tmp / "publish.json").read_text())["verify_gate"]["conclusion"], "success")

    def test_verify_gate_poll_exhausted_is_refused(self):
        self.gate_refuses([[run(status="in_progress", conclusion=None)]] * 5)

    def test_verify_gate_network_error_is_refused(self):
        self.env["AUTO_PREVIEW_TEST_GITHUB_API"] = "http://127.0.0.1:9"
        self.assertEqual(self.publish(FEW_MOVES_AIT_DEPLOY_KEY=""), pl.EXIT_REFUSED)
        self.assertEqual(self.calls_made(), [])

    def test_verify_gate_cli_requires_ci_and_trusted_head(self):
        with mock.patch.dict(os.environ, {**self.env, "GITHUB_ACTIONS": ""}):
            self.assertEqual(pl.main(["verify-gate", "--commit", HEAD]), pl.EXIT_INPUT)
        with mock.patch.dict(os.environ, {**self.env, "AUTO_PREVIEW_TEST_REMOTE_HEAD": "f" * 40}):
            self.assertEqual(pl.main(["verify-gate", "--commit", HEAD]), pl.EXIT_REFUSED)
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["verify-gate", "--commit", HEAD]), 0)

    def test_dry_run_does_not_query_github(self):
        self.assertEqual(self.publish("--dry-run"), 0)
        self.assertEqual(StatusServer.seen, [])

    def test_server_build_failed_marks_ledger(self):
        self.assertEqual(self.publish(behavior="fail"), pl.EXIT_FAIL)
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "BUILD_FAILED"}}
        with mock.patch.dict(os.environ, self.env):
            self.assertEqual(pl.main(["status", "--deployment-id", DEP, "--ledger", str(self.ledger)]), 0)
        self.assertEqual(json.loads(self.ledger.read_text())["entries"][0]["status"], "build_failed")
        StatusServer.reply = {"resultType": "SUCCESS", "success": {"reviewStatus": "CREATED"}}
        self.assertEqual(self.publish(), pl.EXIT_REFUSED, "status-confirmed BUILD_FAILED deployment is never deployed again")
        self.assertEqual(len(self.calls_made()), 1)


class RedactorTests(unittest.TestCase):
    def test_redactor(self):
        r = Redactor(["sekret-value-123"])
        text = r('k=sekret-value-123 https://a.b/c?X-Amz-Signature=1 intoss-private://few-moves?_deploymentId=1&host=x "token": "abc" Bearer abcdefghijkl')
        self.assertNotIn("sekret-value-123", text)
        self.assertNotIn("X-Amz-Signature=1", text)
        self.assertIn("intoss-private://few-moves?_deploymentId=1&host=x", text)
        self.assertNotIn('"token": "abc"', text)
        self.assertNotIn("abcdefghijkl", text)


@unittest.skipUnless((DRY_RUN / "build-receipt.json").is_file(), "local dry-run build absent (Builds/ is local-only)")
class RealArtifactTests(unittest.TestCase):
    """Counterexamples against the real official build output of the local dry-run."""

    def setUp(self):
        self.project = DRY_RUN / "ait-project"
        self.ait = self.project / "few-moves.ait"
        self.release = DRY_RUN / "release"
        self.base = nb.verify_tar(NATIVE_TAR.read_bytes(), nb.load_lock())
        self.tmp = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_real_artifact_passes(self):
        result = pl.verify_ait(self.project, self.ait, self.release, self.base)
        self.assertEqual((result["appName"], result["platform"]), ("few-moves", "WEB"))
        self.assertRegex(result["deploymentId"], pl.UUID7)

    def test_broken_header_and_truncated(self):
        bad = self.tmp / "bad.ait"
        data = self.ait.read_bytes()
        bad.write_bytes(b"NOTAAIT!" + data[8:])
        with self.assertRaises(pl.Stop):
            pl.verify_ait(self.project, bad, self.release, self.base)
        bad.write_bytes(data[: len(data) // 3])
        with self.assertRaises(pl.Stop):
            pl.verify_ait(self.project, bad, self.release, self.base)

    def test_wrong_app_binding(self):
        with mock.patch.object(pl, "BIND", {**pl.BIND, "appName": "other-app"}), self.assertRaisesRegex(pl.Stop, "appName"):
            pl.verify_ait(self.project, self.ait, self.release, self.base)

    def test_release_and_native_mismatch(self):
        release = self.tmp / "release"
        shutil.copytree(self.release, release)
        (release / "style.css").write_text("changed")
        with self.assertRaisesRegex(pl.Stop, "sources/ differ"):
            pl.verify_ait(self.project, self.ait, release, self.base)
        base = dict(self.base)
        wasm = next(k for k in base if k.endswith(".wasm"))
        base[wasm] = b"other native bytes"
        with self.assertRaisesRegex(pl.Stop, "native file not byte-identical"):
            pl.verify_ait(self.project, self.ait, self.release, base)
        page = dict(self.base)
        page["solo/index.html"] = page["solo/index.html"] + b"<!-- changed template -->"
        with self.assertRaisesRegex(pl.Stop, "game page changed"):
            pl.verify_ait(self.project, self.ait, self.release, page)

    def test_build_refuses_bad_archive_and_existing_work(self):
        bad = self.tmp / "base.tar.gz"
        bad.write_bytes(NATIVE_TAR.read_bytes()[:-10])
        with self.assertRaisesRegex(pl.Stop, "native base rejected"):
            pl.build(pl.argparse.Namespace(native_tar=str(bad), work=str(self.tmp / "w"), base_ref=None, offline=True))
        (self.tmp / "exists").mkdir()
        with self.assertRaisesRegex(pl.Stop, "work directory exists"):
            pl.build(pl.argparse.Namespace(native_tar=str(NATIVE_TAR), work=str(self.tmp / "exists"), base_ref=None, offline=True))


class WorkflowPolicyTests(unittest.TestCase):
    def setUp(self):
        self.text = (ROOT / ".github" / "workflows" / "few-moves-preview.yml").read_text()

    def test_triggers_and_permissions(self):
        self.assertNotIn("pull_request", self.text)
        self.assertNotIn("self-hosted", self.text)
        self.assertRegex(self.text, r"(?m)^permissions:\n  contents: read$")
        self.assertIn("workflow_dispatch:", self.text)
        self.assertIn("- 'codex/few-moves-preview'", self.text)
        self.assertNotIn("release/few-moves-preview", self.text)
        self.assertIn("github.ref == 'refs/heads/codex/few-moves-preview'", self.text, "publish job is ref-guarded")

    def test_actions_pinned_by_sha(self):
        uses = re.findall(r"uses:\s*(\S+)", self.text)
        self.assertTrue(uses)
        for ref in uses:
            self.assertRegex(ref, r"^[\w.-]+/[\w.-]+(/[\w.-]+)?@[0-9a-f]{40}$", ref)

    def test_secret_only_in_publish_job_and_no_test_hooks(self):
        jobs = re.split(r"(?m)^  (?=[a-z-]+:$)", self.text.split("\njobs:\n", 1)[1])
        for job in jobs:
            name = job.split(":", 1)[0]
            if "secrets." in job:
                self.assertEqual(name, "publish", f"secret referenced in job {name}")
        self.assertEqual(self.text.count("secrets."), 1)
        self.assertNotIn("approved_ait_sha", self.text, "rebuilds mint a new deploymentId, so a pre-approved .ait sha cannot match")
        self.assertNotIn("HOME", self.text)

    def test_verify_gate_step_precedes_the_key_without_secrets(self):
        publish = self.text.split("\n  publish:\n", 1)[1]
        self.assertIn("      actions: read", publish)
        gate_at = publish.index("pipeline.py verify-gate")
        key_at = publish.index("secrets.FEW_MOVES_AIT_DEPLOY_KEY")
        self.assertLess(gate_at, key_at)
        gate_step = publish[publish.rindex("- name:", 0, gate_at):gate_at]
        self.assertNotIn("secrets.", gate_step)
        build = self.text.split("\n  build:\n", 1)[1].split("\n  publish:\n", 1)[0]
        self.assertNotIn("actions: read", build)
        self.assertEqual((ROOT / ".github/workflows/verify.yml").read_bytes(), pl.subprocess.run(
            ["git", "-C", str(ROOT), "show", "HEAD:.github/workflows/verify.yml"], capture_output=True, check=True).stdout, "verify.yml unchanged")
        self.assertNotIn("AUTO_PREVIEW_TEST_", self.text)
        self.assertIn("node-version: '24'", self.text)
        self.assertIn("persist-credentials: false", self.text)


if __name__ == "__main__":
    unittest.main()
