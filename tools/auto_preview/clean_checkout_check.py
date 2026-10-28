#!/usr/bin/env python3
"""Clean-checkout-equivalent check: HEAD (git archive) + only the declared commit candidates, layer by layer.

Usage: python3 -B tools/auto_preview/clean_checkout_check.py --native-tar ARCHIVE --out REPORT_DIR [--offline]
Each layer runs in a fresh temporary git repository with no Builds/, caches or untracked files; the native base
archive is supplied from outside exactly as CI downloads it. Nothing in the real repository is changed.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

LAYERS = [
    ("pipeline", "the harness itself", [
        "tools/auto_preview", "tests/auto_preview", ".github/workflows/few-moves-preview.yml"]),
    ("native_build_entries", "reviewed r3 Unity build entries (-aitNative); the native base lock fingerprints Assets/ with them", [
        "Assets/Editor/RaidBuild.cs", "Assets/Editor/Preview3DBuild.cs"]),
    ("packaging", "solo-first packaging tool (--ait-bridge, --toss-ui) and its release profile", [
        "tools/prepare_solo_release.py", "web/release-profiles/solo-first/profile.json"]),
    ("toss_ui", "Toss UI overlay revision named in config.json (r6, reviewed)", [
        "web/platforms/toss/r6/toss-ui.css", "web/platforms/toss/r6/toss-safearea.js"]),
]


def sh(cmd: list[str], cwd: Path, env: dict | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, env=env)


def files_of(rel: str) -> list[str]:
    path = ROOT / rel
    if path.is_dir():
        return sorted(p.relative_to(ROOT).as_posix() for p in path.rglob("*")
                      if p.is_file() and "__pycache__" not in p.parts and "node_modules" not in p.parts)
    if not path.is_file():
        raise SystemExit(f"declared candidate missing: {rel}")
    return [rel]


def make_tree(dest: Path, candidates: list[str]) -> None:
    archive = subprocess.run(["git", "-C", str(ROOT), "archive", "--format=tar", "HEAD"], capture_output=True, check=True).stdout
    subprocess.run(["tar", "-x", "-C", str(dest)], input=archive, check=True)
    for rel in candidates:
        target = dest / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / rel, target)
    git = ["git", "-c", "user.name=clean-check", "-c", "user.email=clean-check@localhost", "-c", "commit.gpgsign=false"]
    for cmd in (["init", "-q"], ["add", "-A"], ["commit", "-q", "-m", "clean checkout equivalent"]):
        subprocess.run(git + cmd, cwd=dest, check=True, capture_output=True)


def run_layer(index: int, native_tar: Path, offline: bool) -> dict:
    declared = [f for _, _, rels in LAYERS[: index + 1] for rel in rels for f in files_of(rel)]
    tmp = Path(tempfile.mkdtemp(prefix="few-moves-clean-"))
    try:
        tree = tmp / "repo"
        tree.mkdir()
        make_tree(tree, declared)
        env = {k: v for k, v in os.environ.items() if not k.startswith(("GITHUB_", "FEW_MOVES_", "AUTO_PREVIEW_"))}
        steps = {}
        tests = sh([sys.executable, "-B", "-m", "unittest", "-v", "-b", "tests.auto_preview.test_auto_preview"], tree, env)
        lines = tests.stderr.splitlines()
        steps["tests"] = {"exit": tests.returncode,
                          "executed_passed": sum(1 for l in lines if l.endswith(" ... ok")),
                          "skipped": sum(1 for l in lines if " ... skipped" in l),
                          "failed_or_error": sum(1 for l in lines if l.endswith((" ... FAIL", " ... ERROR")))}
        build_cmd = [sys.executable, "-B", "tools/auto_preview/pipeline.py", "build", "--native-tar", str(native_tar), "--work", str(tmp / "work")]
        build = sh(build_cmd + (["--offline"] if offline else []), tree, env)
        steps["build"] = {"exit": build.returncode, "error": (json.loads(build.stderr.strip().splitlines()[-1]).get("error") if build.returncode else None)}
        if build.returncode == 3:
            request = json.loads((tmp / "work" / "native-rebuild-request.json").read_text())
            steps["build"]["native_changes"] = [(c["path"], c["change"]) for c in request["changes"]]
        if build.returncode == 0:
            receipt = json.loads((tmp / "work" / "build-receipt.json").read_text())
            steps["build"].update(ait_sha256=receipt["ait"]["sha256"], content_sha256=receipt["ait"]["content_sha256"], uploaded=receipt["uploaded"])
            shutil.copy2(tree / "tools/auto_preview/ledger.seed.json", tmp / "ledger.json")
            pub = sh([sys.executable, "-B", "tools/auto_preview/pipeline.py", "publish", "--dry-run", "--receipt", str(tmp / "work/build-receipt.json"),
                      "--ait", str(tmp / "work/ait-project/few-moves.ait"), "--project", str(tmp / "work/ait-project"), "--ledger", str(tmp / "ledger.json"),
                      "--out", str(tmp / "publish.json"), "--log", str(tmp / "publish.log")], tree, env)
            steps["publish_dry_run"] = {"exit": pub.returncode, "state": json.loads((tmp / "publish.json").read_text())["state"] if pub.returncode == 0 else pub.stderr[-300:]}
        return {"layer": LAYERS[index][0], "files_added_on_top_of_HEAD": declared, "steps": steps}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--native-tar", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=False)
    head = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    results = [run_layer(i, Path(args.native_tar).resolve(), args.offline) for i in range(len(LAYERS))]
    report = {"head": head, "native_tar": str(args.native_tar), "layers": [{"name": n, "why": w, "paths": p} for n, w, p in LAYERS], "results": results}
    (out / "clean-checkout-report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    for r in results:
        print(json.dumps({"layer": r["layer"], **r["steps"]}, ensure_ascii=False))
    final = results[-1]["steps"]
    return 0 if final["build"]["exit"] == 0 and final["tests"]["exit"] == 0 and final.get("publish_dry_run", {}).get("exit") == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
