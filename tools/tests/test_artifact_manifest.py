from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools" / "artifact_manifest.py"


class ArtifactManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp.name) / "repo"
        self.repo.mkdir()
        self.git("init", "-q")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "user.name", "Artifact Test")
        for directory in ("Assets", "Packages", "ProjectSettings"):
            (self.repo / directory).mkdir()
        self.write("Assets/scene.txt", "scene-v1\n")
        self.write("Packages/manifest.json", "{}\n")
        self.write("ProjectSettings/settings.txt", "settings-v1\n")
        self.write(".gitignore", "/ProjectSettings/UnityConnectSettings.asset\n/Assets/ignored-local.cs\n")
        self.git("add", "Assets", "Packages", "ProjectSettings", ".gitignore")
        self.git("commit", "-qm", "initial")
        self.build = self.repo / "Builds" / "WebGL"
        self.build.mkdir(parents=True)
        self.write_build("index.html", "<html>v1</html>\n")
        self.write_build("unity-build.provenance.json", '{"build":"test"}\n')
        self.snapshot = self.repo.parent / "snapshot.json"
        self.manifest = self.repo.parent / "manifest.json"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def git(self, *args: str) -> str:
        result = subprocess.run(["git", *args], cwd=self.repo, text=True, capture_output=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip()

    def write(self, relative: str, content: str) -> None:
        path = self.repo / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def write_build(self, relative: str, content: str) -> None:
        path = self.build / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def run_cli(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([os.fspath(__import__("sys").executable), str(SCRIPT), *args], cwd=ROOT, text=True, capture_output=True, check=False)

    def make_snapshot(self) -> None:
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertEqual(result.returncode, 0, result.stderr)

    def make_manifest(self) -> None:
        self.make_snapshot()
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(self.build), "--out", str(self.manifest))
        self.assertEqual(result.returncode, 0, result.stderr)

    def verify_manifest(self) -> subprocess.CompletedProcess[str]:
        return self.run_cli("verify", "--repo", str(self.repo), "--manifest", str(self.manifest), "--build", str(self.build))

    def test_snapshot_seal_verify_happy_path(self) -> None:
        self.make_manifest()
        result = self.verify_manifest()
        self.assertEqual(result.returncode, 0, result.stderr)
        receipt = json.loads(result.stdout)
        self.assertEqual(receipt["status"], "pass")
        self.assertEqual(receipt["runtime_execution"], "not_performed")
        self.assertEqual([], receipt["effective_local_inputs"])

    def test_ignored_unity_connect_is_effective_source_and_tamper_breaks_parity(self) -> None:
        local = "ProjectSettings/UnityConnectSettings.asset"
        self.write(local, "generated-local-v1\n")
        self.make_snapshot()
        snapshot = json.loads(self.snapshot.read_text(encoding="utf-8"))
        self.assertIn(local, snapshot["effective_local_inputs"])
        self.assertIn(local, [item["path"] for item in snapshot["source_files"]])
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(self.build), "--out", str(self.manifest))
        self.assertEqual(result.returncode, 0, result.stderr)
        verified = self.verify_manifest()
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertEqual([local], json.loads(verified.stdout)["effective_local_inputs"])
        self.write(local, "generated-local-v2\n")
        self.assertNotEqual(self.verify_manifest().returncode, 0)

    def test_ignored_unity_connect_change_after_snapshot_breaks_seal(self) -> None:
        self.write("ProjectSettings/UnityConnectSettings.asset", "generated-local-v1\n")
        self.make_snapshot()
        self.write("ProjectSettings/UnityConnectSettings.asset", "generated-local-v2\n")
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(self.build), "--out", str(self.manifest))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("differs from snapshot", result.stderr)

    def test_unknown_ignored_asset_remains_blocked(self) -> None:
        self.write("Assets/ignored-local.cs", "ignored but unknown\n")
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Assets/ignored-local.cs", result.stderr)

    def test_symlinked_unity_connect_is_rejected(self) -> None:
        os.symlink(self.repo / "ProjectSettings" / "settings.txt", self.repo / "ProjectSettings" / "UnityConnectSettings.asset")
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("symlink", result.stderr)

    def test_source_mutation_add_delete_fail(self) -> None:
        self.make_manifest()
        self.write("Assets/scene.txt", "changed\n")
        self.assertNotEqual(self.verify_manifest().returncode, 0)
        self.write("Assets/scene.txt", "scene-v1\n")
        self.write("Assets/new.txt", "new\n")
        self.assertNotEqual(self.verify_manifest().returncode, 0)
        (self.repo / "Assets/scene.txt").unlink()
        self.assertNotEqual(self.verify_manifest().returncode, 0)

    def test_artifact_tamper_add_delete_fail(self) -> None:
        self.make_manifest()
        self.write_build("index.html", "tampered\n")
        self.assertNotEqual(self.verify_manifest().returncode, 0)
        self.write_build("index.html", "<html>v1</html>\n")
        self.write_build("extra.js", "extra\n")
        self.assertNotEqual(self.verify_manifest().returncode, 0)
        (self.build / "index.html").unlink()
        self.assertNotEqual(self.verify_manifest().returncode, 0)

    def test_untracked_watched_source_rejected_at_snapshot(self) -> None:
        self.write("Assets/untracked.txt", "untracked\n")
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("untracked", result.stderr)

    def test_untracked_global_config_rejected_at_snapshot(self) -> None:
        self.write("global.json", '{"sdk":{"version":"10.0.203"}}\n')
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("global.json", result.stderr)

    def test_missing_watched_directory_rejected(self) -> None:
        self.git("rm", "-q", "Packages/manifest.json")
        self.git("commit", "-qm", "remove package input")
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("watched directory", result.stderr)

    def test_empty_tracked_source_inventory_rejected(self) -> None:
        for relative in ("Assets/scene.txt", "Packages/manifest.json", "ProjectSettings/settings.txt"):
            self.git("rm", "-q", relative)
        self.git("commit", "-qm", "remove all watched inputs")
        for directory in ("Assets", "Packages", "ProjectSettings"):
            (self.repo / directory).mkdir()
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no tracked watched source files", result.stderr)

    def test_symlink_rejected(self) -> None:
        os.symlink(self.repo / "Assets" / "scene.txt", self.repo / "Assets" / "link.txt")
        result = self.run_cli("snapshot", "--repo", str(self.repo), "--out", str(self.snapshot))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("untracked", result.stderr)

    def test_docs_only_commit_allows_head_difference(self) -> None:
        self.make_manifest()
        self.write("docs.txt", "report-only\n")
        self.git("add", "docs.txt")
        self.git("commit", "-qm", "docs only")
        result = self.verify_manifest()
        self.assertEqual(result.returncode, 0, result.stderr)
        receipt = json.loads(result.stdout)
        self.assertTrue(receipt["git_head_changed"])
        self.assertNotEqual(receipt["seal_git_head"], receipt["verified_git_head"])

    def test_receipt_output_inside_build_rejected(self) -> None:
        self.make_snapshot()
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(self.build), "--out", str(self.build / "manifest.json"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("outside the build", result.stderr)

    def test_build_escape_rejected(self) -> None:
        self.make_snapshot()
        outside = self.repo.parent / "outside-build"
        outside.mkdir()
        (outside / "unity-build.provenance.json").write_text("{}\n", encoding="utf-8")
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(outside), "--out", str(self.manifest))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("inside repo", result.stderr)

    def test_missing_provenance_rejected(self) -> None:
        self.make_snapshot()
        (self.build / "unity-build.provenance.json").unlink()
        result = self.run_cli("seal", "--repo", str(self.repo), "--snapshot", str(self.snapshot), "--build", str(self.build), "--out", str(self.manifest))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("provenance", result.stderr)


if __name__ == "__main__":
    unittest.main()
