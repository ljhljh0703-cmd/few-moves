#!/usr/bin/env python3
"""Few Moves — Apps in Toss self-only preview pipeline (UI-only fast path).

Stages (each writes a JSON receipt; any failure exits non-zero and nothing later runs):
  gate      decide the automation state from the event and repository settings (no secrets read)
  classify  list every changed path with its category (native / ui_overlay / packaging / pipeline / other)
  build     native base archive -> sha and file check -> native-input check (stop on any Unity input change)
            -> current Toss UI overlay -> official `ait build` -> verify -> build-receipt.json   (no credentials)
  verify    re-check an .ait against its build receipt
  publish   official `ait deploy --location <the verified .ait>` (uploads as-is; unlisted test only)
            with duplicate / stale / resume protection and redacted output                   (needs the deploy key)
  status    read-only deployment status for resume (same GET the official CLI uses)

Never: review, release, rollback, terms, account, payment. Not on this path (API key + GitHub-hosted runner): test push
(no official CLI/API-key route) and Unity rebuilds (no license on hosted runners; the licensed Mac does that locally). Exit codes: 0 ok/skip, 1 failure,
2 bad input/config, 3 native change (Unity rebuild needed on the licensed Mac), 4 duplicate/stale refusal.
"""
from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools"))
import native_base as nb  # noqa: E402
from redact import Redactor  # noqa: E402

CONFIG = json.loads((HERE / "config.json").read_text(encoding="utf-8"))
BIND = CONFIG["binding"]
UUID7 = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
TRUSTED_REPOSITORY = "ljhljh0703-cmd/few-moves"
TRUSTED_REF = "refs/heads/codex/few-moves-preview"  # proposed; the branch does not exist yet
EXIT_FAIL, EXIT_INPUT, EXIT_NATIVE, EXIT_REFUSED = 1, 2, 3, 4
PAGE_TAGS = (b'  <script type="module" src="../platform/ait-unity-bridge.js"></script>\n',
             b'  <link rel="stylesheet" href="../platform/toss-ui.css">\n',
             b'  <script type="module" src="../platform/toss-safearea.js"></script>\n')


class Stop(Exception):
    def __init__(self, code: int, message: str, **detail):
        super().__init__(message)
        self.code, self.detail = code, detail


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def git(*args: str) -> str:
    return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, check=True).stdout


def ui_paths() -> list[str]:
    ui = CONFIG["ui_overlay"]
    files = [p.relative_to(ROOT).as_posix() for p in sorted((ROOT / ui["toss_ui_dir"]).glob("*")) if p.is_file()]
    return files + ui["portal_files"] + [ui["release_profile"]] + ui["packaging_tools"]


def ui_fingerprint() -> dict[str, str]:
    return {rel: nb.sha256_file(ROOT / rel) for rel in ui_paths()}


def config_digest() -> str:
    items = {p.relative_to(ROOT).as_posix(): nb.sha256_file(p) for p in sorted(HERE.rglob("*"))
             if p.is_file() and "__pycache__" not in p.parts and "node_modules" not in p.parts}
    return nb.tree_digest(items)


# ---------------------------------------------------------------- gate
def gate(env: dict[str, str]) -> dict:
    event, repo, ref = env.get("GITHUB_EVENT_NAME", "local"), env.get("GITHUB_REPOSITORY", ""), env.get("GITHUB_REF", "")
    requested = env.get("PREVIEW_MODE", "dry-run")
    enabled = env.get("FEW_MOVES_PREVIEW_ENABLED", "") == "true"
    base_tag = env.get("FEW_MOVES_NATIVE_BASE_TAG", "")
    result = {"event": event, "repository": repo, "ref": ref, "enabled_variable": enabled, "native_base_tag_set": bool(base_tag),
              "run_build": False, "run_publish": False}
    if event not in ("workflow_dispatch", "push", "local"):
        return {**result, "state": "refused", "reason": f"event {event} is never allowed (no pull_request / pull_request_target)"}
    if event != "local" and repo != TRUSTED_REPOSITORY:
        return {**result, "state": "refused", "reason": "not the trusted repository (forks never run this)"}
    if event != "local" and ref != TRUSTED_REF:
        return {**result, "state": "refused", "reason": f"runs only on {TRUSTED_REF} (push and dispatch)"}
    if requested not in ("dry-run", "publish"):
        return {**result, "state": "refused", "reason": f"unknown mode {requested}"}
    if event == "push":
        # Push uses the workflow file of the pushed commit (no default-branch requirement): publish when enabled,
        # otherwise build + verify only.
        requested = "publish" if enabled else "dry-run"
    if not base_tag:
        return {**result, "state": "off", "reason": "FEW_MOVES_NATIVE_BASE_TAG not set (reviewed native base not available to CI)"}
    if requested == "dry-run":
        return {**result, "state": "dry_run", "run_build": True, "reason": "build and verify only; no credentials"}
    if not enabled:
        return {**result, "state": "off", "reason": "FEW_MOVES_PREVIEW_ENABLED is not 'true'; publish disabled"}
    return {**result, "state": "armed", "run_build": True, "run_publish": True, "reason": "publish job may run (needs the deploy key secret)"}


# ---------------------------------------------------------------- classify
def classify_path(rel: str) -> str:
    if rel.startswith(tuple(CONFIG["native"]["input_prefixes"])):
        return "native"
    if rel in ui_paths() or rel.startswith(CONFIG["ui_overlay"]["toss_ui_dir"] + "/"):
        return "ui_overlay"
    if rel.startswith("tools/auto_preview/") or rel in (".github/workflows/few-moves-preview.yml",):
        return "pipeline"
    return "other"


def classify(base_ref: str | None) -> dict:
    changed: dict[str, str] = {}
    if base_ref:
        for line in git("diff", "--name-status", "--no-renames", f"{base_ref}..HEAD").splitlines():
            status, rel = line.split("\t", 1)
            changed[rel] = status
    for line in git("status", "--porcelain=v1", "--untracked-files=all").splitlines():
        rel = line[3:]
        if " -> " not in rel:
            changed.setdefault(rel, line[:2].strip() or "?")
    items = [{"path": rel, "git": st, "category": classify_path(rel)} for rel, st in sorted(changed.items())]
    counts: dict[str, int] = {}
    for item in items:
        counts[item["category"]] = counts.get(item["category"], 0) + 1
    return {"base_ref": base_ref, "head": git("rev-parse", "HEAD").strip(), "counts": counts, "files": items}


def check_native(lock: dict, out_dir: Path) -> None:
    changes = nb.compare_native_inputs(lock, nb.native_input_fingerprint())
    if changes:
        request = {
            "state": "blocked_native_change", "created_utc": now(), "base_id": lock["base_id"],
            "pinned_native_inputs_sha256": lock["native_inputs"]["sha256"], "changes": changes,
            "why": "Unity inputs differ from the reviewed native base; reusing its wasm/data/framework.js would ship stale native code.",
            "needed": [
                "Licensed Unity 6000.4.3f1: GitHub-hosted runners have no license, so this path cannot rebuild; the licensed Mac can run the supervised native builds (raid-ait + preview3d-ait, -aitNative) as a local scripted step",
                "Independent native review of the new build",
                "native_base.py pack -> new archive + new tools/auto_preview/native-base.lock.json, distributed per tools/auto_preview/README.md",
            ],
        }
        write_json(out_dir / "native-rebuild-request.json", request)
        raise Stop(EXIT_NATIVE, f"{len(changes)} native input change(s); Unity rebuild required", request="native-rebuild-request.json")


# ---------------------------------------------------------------- build
def derive_profile(lock: dict, out: Path) -> Path:
    profile = json.loads((ROOT / CONFIG["ui_overlay"]["release_profile"]).read_text(encoding="utf-8"))
    profile["compiled_residuals"]["allowed"] = {f"Build/{lock['framework']['name']}": {"/api/coop/v1": lock["framework"]["coop_api_residual"]}}
    profile["derived_from"] = {"path": CONFIG["ui_overlay"]["release_profile"], "sha256": nb.sha256_file(ROOT / CONFIG["ui_overlay"]["release_profile"]),
                               "changed": "compiled_residuals.allowed key only (native framework.js name)"}
    out.write_text(json.dumps(profile, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return out


def run(cmd: list[str], cwd: Path, log: Path, env: dict | None = None, redactor: Redactor | None = None) -> subprocess.CompletedProcess:
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, env=env)
    text = (proc.stdout or "") + (proc.stderr or "")
    if redactor:
        text = redactor(text)
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open("a", encoding="utf-8") as handle:
        handle.write(f"$ {' '.join(redactor(c) if redactor else c for c in cmd)}\n{text}\n[exit {proc.returncode}]\n")
    return proc


def inspect_ait(project: Path, ait: Path) -> dict:
    modules = project / "node_modules" / ".pnpm"
    candidates = sorted(modules.glob("@apps-in-toss+ait-format@1.0.0*/node_modules"))
    if not candidates:
        raise Stop(EXIT_FAIL, "official @apps-in-toss/ait-format 1.0.0 not installed in the AIT project")
    proc = subprocess.run(["node", str(HERE / "ait_inspect.mjs"), str(candidates[0]), str(ait)], capture_output=True, text=True)
    if proc.returncode != 0:
        raise Stop(EXIT_FAIL, "ait_inspect failed", stderr=proc.stderr[-400:])
    return json.loads(proc.stdout)


def verify_ait(project: Path, ait: Path, release: Path, base_files: dict[str, bytes]) -> dict:
    data = ait.read_bytes()
    info = inspect_ait(project, ait)
    problems = []
    if data[:8] != b"AITBUNDL" or info.get("magic") != "AITBUNDL":
        problems.append("missing AITBUNDL header")
    if not info.get("ok"):
        problems.append(f"official reader rejected the file: {info.get('error')}")
        raise Stop(EXIT_FAIL, "; ".join(problems), inspect=info)
    if info.get("appName") != BIND["appName"]:
        problems.append(f"appName {info.get('appName')!r} != {BIND['appName']!r}")
    if not UUID7.match(str(info.get("deploymentId", ""))):
        problems.append("embedded deploymentId is not a UUID v7")
    if info.get("platform") != BIND["platform"] or info.get("sdkVersion") != BIND["sdkVersion"]:
        problems.append(f"platform/sdk {info.get('platform')}/{info.get('sdkVersion')} != {BIND['platform']}/{BIND['sdkVersion']}")
    span = info["payload"]
    # The official reader accepts a truncated file (it does not bound-check the payload span), so check it here.
    if not isinstance(span.get("offset"), int) or not isinstance(span.get("length"), int) or span["offset"] + span["length"] > len(data):
        raise Stop(EXIT_FAIL, "truncated or inconsistent .ait: payload span exceeds the file", inspect=info)
    try:
        payload = zipfile.ZipFile(io.BytesIO(data[span["offset"]:span["offset"] + span["length"]]))
        bad = payload.testzip()
    except (zipfile.BadZipFile, EOFError, ValueError) as error:
        raise Stop(EXIT_FAIL, f"broken .ait payload: {type(error).__name__}", inspect=info)
    if bad is not None:
        raise Stop(EXIT_FAIL, f"broken .ait payload: CRC error in {bad}", inspect=info)
    sources = {i.filename[len("sources/"):]: nb.sha256_bytes(payload.read(i)) for i in payload.infolist()
               if i.filename.startswith("sources/") and not i.is_dir()}
    uncompressed = sum(i.file_size for i in payload.infolist())
    expected = {p.relative_to(release).as_posix(): nb.sha256_file(p) for p in sorted(release.rglob("*")) if p.is_file()}
    if sources != expected:
        problems.append("sources/ differ from the release directory")
    for rel, blob in base_files.items():
        if rel.endswith("/index.html"):
            # Unity template page: packaging may only add the known tag lines; everything else stays byte-identical.
            page = payload.read(f"sources/{rel}")
            for tag in PAGE_TAGS:
                page = page.replace(tag, b"", 1)
            if page != blob:
                problems.append(f"game page changed beyond the bridge/Toss UI tags: {rel}")
        elif sources.get(rel) != nb.sha256_bytes(blob):
            problems.append(f"native file not byte-identical to the base: {rel}")
    if uncompressed > CONFIG["limits"]["uncompressed_max_bytes"]:
        problems.append(f"uncompressed {uncompressed} > {CONFIG['limits']['uncompressed_max_bytes']}")
    if problems:
        raise Stop(EXIT_FAIL, "; ".join(problems), inspect=info)
    return {"sha256": nb.sha256_bytes(data), "bytes": len(data), "appName": info["appName"], "deploymentId": info["deploymentId"],
            "platform": info["platform"], "sdkVersion": info["sdkVersion"], "runtimeVersion": info["runtimeVersion"], "isGame": info["isGame"],
            "entries": len(payload.infolist()), "uncompressed_bytes": uncompressed, "content_sha256": nb.tree_digest(sources),
            "native_files_identical": True, "sources": sources}


def build(args: argparse.Namespace) -> dict:
    work = Path(args.work).resolve()
    if work.exists():
        raise Stop(EXIT_INPUT, f"work directory exists: {work}")
    work.mkdir(parents=True)
    lock = nb.load_lock()
    tar = Path(args.native_tar)
    if not tar.is_file():
        raise Stop(EXIT_INPUT, f"native base archive not found: {tar}")
    try:
        base_files = nb.verify_tar(tar.read_bytes(), lock)
    except nb.NativeBaseError as error:
        raise Stop(EXIT_INPUT, f"native base rejected: {error}")
    check_native(lock, work)
    source = work / "source"
    for rel, blob in base_files.items():
        if rel != nb.BRIDGE_REL:
            (source / rel).parent.mkdir(parents=True, exist_ok=True)
            (source / rel).write_bytes(blob)
    for rel in CONFIG["ui_overlay"]["portal_files"]:
        shutil.copy2(ROOT / rel, source / Path(rel).name)
    bridge = work / "bridge" / "ait-unity-bridge.js"
    bridge.parent.mkdir()
    bridge.write_bytes(base_files[nb.BRIDGE_REL])
    profile = derive_profile(lock, work / "release-profile.json")
    release = work / "release"
    logs = work / "logs"
    proc = run([sys.executable, "-B", str(ROOT / "tools/prepare_solo_release.py"), "--source", str(source), "--out", str(release),
                "--profile", str(profile), "--ait-bridge", str(bridge), "--toss-ui", str(ROOT / CONFIG["ui_overlay"]["toss_ui_dir"])], work, logs / "prepare.log")
    if proc.returncode != 0:
        raise Stop(EXIT_FAIL, "prepare_solo_release failed", log="logs/prepare.log")
    project = work / "ait-project"
    shutil.copytree(ROOT / CONFIG["ait_project"], project)
    install = ["pnpm", "install", "--frozen-lockfile"] + (["--offline"] if args.offline else [])
    if run(install, project, logs / "pnpm-install.log").returncode != 0:
        raise Stop(EXIT_FAIL, "pnpm install --frozen-lockfile failed", log="logs/pnpm-install.log")
    shutil.copytree(release, project / "dist" / "web")
    if run(["node", "ait-patch-cli.mjs"], project, logs / "ait-patch-cli.log").returncode != 0:
        raise Stop(EXIT_FAIL, "ait-patch-cli.mjs failed", log="logs/ait-patch-cli.log")
    if run([str(project / "node_modules" / ".bin" / "ait"), "build"], project, logs / "ait-build.log").returncode != 0:
        raise Stop(EXIT_FAIL, "official ait build failed", log="logs/ait-build.log")
    ait = project / f"{BIND['appName']}.ait"
    verified = verify_ait(project, ait, release, base_files)
    receipt = {
        "kind": "few-moves-toss-preview-build", "state": "verified", "created_utc": now(),
        "commit": os.environ.get("GITHUB_SHA") or git("rev-parse", "HEAD").strip(),
        "binding": BIND, "native_base": {"base_id": lock["base_id"], "tar_sha256": lock["tar"]["sha256"],
                                          "lock_sha256": nb.sha256_file(nb.LOCK), "native_inputs_sha256": lock["native_inputs"]["sha256"]},
        "ui_inputs": ui_fingerprint(), "ui_inputs_sha256": nb.tree_digest(ui_fingerprint()), "pipeline_sha256": config_digest(),
        "classification": classify(args.base_ref), "ait": {k: v for k, v in verified.items() if k != "sources"},
        "ait_path": str(ait.relative_to(work)), "release_files": verified["sources"],
        "server": "not contacted", "uploaded": False, "test_push": "not part of this path",
    }
    write_json(work / "build-receipt.json", receipt)
    return receipt


# ---------------------------------------------------------------- publish / status
def load_ledger(path: Path) -> dict:
    if path.is_file():
        ledger = json.loads(path.read_text(encoding="utf-8"))
        if ledger.get("appName") != BIND["appName"] or ledger.get("miniAppId") != BIND["miniAppId"]:
            raise Stop(EXIT_INPUT, "ledger belongs to another app")
        return ledger
    return {"appName": BIND["appName"], "miniAppId": BIND["miniAppId"], "workspaceId": BIND["workspaceId"], "entries": []}


def check_receipt(receipt: dict, ait: Path, approved_sha: str | None, max_age_h: float) -> None:
    if receipt.get("kind") != "few-moves-toss-preview-build" or receipt.get("state") != "verified":
        raise Stop(EXIT_INPUT, "not a verified build receipt")
    if receipt["binding"] != BIND:
        raise Stop(EXIT_INPUT, "build receipt is bound to another app/workspace")
    actual = nb.sha256_file(ait)
    if actual != receipt["ait"]["sha256"]:
        raise Stop(EXIT_REFUSED, "stale or tampered artifact: .ait sha256 differs from the build receipt")
    if approved_sha and approved_sha != actual:
        raise Stop(EXIT_REFUSED, "stale approval: approved_ait_sha does not match this artifact")
    head = os.environ.get("GITHUB_SHA") or git("rev-parse", "HEAD").strip()
    if receipt["commit"] != head:
        raise Stop(EXIT_REFUSED, "stale receipt: built from another commit")
    if receipt["native_base"]["lock_sha256"] != nb.sha256_file(nb.LOCK):
        raise Stop(EXIT_REFUSED, "stale receipt: native base lock changed since the build")
    age = (dt.datetime.now(dt.timezone.utc) - dt.datetime.strptime(receipt["created_utc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=dt.timezone.utc)).total_seconds() / 3600
    if age > max_age_h:
        raise Stop(EXIT_REFUSED, f"stale receipt: {age:.1f} h old (max {max_age_h} h)")


def fetch_status(deployment_id: str, key: str) -> str:
    base = os.environ.get("AUTO_PREVIEW_TEST_STATUS_BASE")  # tests only; recorded in receipts
    url = (base.rstrip("/") + "/bundles/{appName}/deployments/{deploymentId}" if base else CONFIG["deploy"]["status_endpoint"]).format(
        appName=BIND["appName"], deploymentId=deployment_id)
    request = urllib.request.Request(url, method="GET", headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = json.loads(response.read().decode())
    except urllib.error.HTTPError as error:
        raise Stop(EXIT_FAIL, f"status HTTP {error.code}")
    except urllib.error.URLError as error:
        raise Stop(EXIT_FAIL, f"status network error: {type(error.reason).__name__}")
    if body.get("resultType") != "SUCCESS":
        raise Stop(EXIT_FAIL, f"status error: {(body.get('error') or {}).get('reason', 'unknown')}")
    return str(body["success"]["reviewStatus"])


def deploy_key(env: dict[str, str]) -> str:
    key = env.get("FEW_MOVES_AIT_DEPLOY_KEY", "")
    if not key.strip():
        raise Stop(EXIT_INPUT, "FEW_MOVES_AIT_DEPLOY_KEY is not set; refusing (the official CLI would prompt interactively)")
    return key.strip()


def ledger_status(server_status: str) -> str:
    """CREATED -> created, BUILD_FAILED -> build_failed; anything else (PREPARE/BUILDING/...) stays pending."""
    return {"CREATED": "created", "BUILD_FAILED": "build_failed"}.get(server_status, "uploaded_unconfirmed")


def testing() -> bool:
    return bool(os.environ.get("AUTO_PREVIEW_TEST_FAKE_AIT"))  # test doubles only; recorded in receipts


def require_ci() -> None:
    """Real publish/status run only on a GitHub-hosted runner (fresh, isolated HOME per job). Locally: --dry-run."""
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise Stop(EXIT_INPUT, "publish/status are CI-only (GitHub-hosted runner); use --dry-run locally")


def credentials_path() -> Path:
    # The official CLI 3.2.0 reads only os.homedir()/.ait/credentials (TokenStorage.path; no override exists).
    # HOME is never changed. Tests inject a path together with the fake CLI.
    injected = os.environ.get("AUTO_PREVIEW_TEST_CREDENTIALS")
    if injected and testing():
        return Path(injected)
    return Path.home() / ".ait" / "credentials"


class CiCredentials:
    """Exclusive-create the CLI credentials file with only our profile; remove exactly what we created."""

    def __init__(self, key: str):
        self.key, self.path = key, credentials_path()
        self.created_dir, self.identity = False, None

    def __enter__(self) -> Path:
        directory = self.path.parent
        if directory.is_symlink() or self.path.is_symlink() or self.path.exists():
            raise Stop(EXIT_INPUT, f"existing CLI credentials at {self.path}; refusing to overwrite or merge")
        if not directory.exists():
            directory.mkdir(mode=0o700)
            self.created_dir = True
        fd = os.open(self.path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump({CONFIG["deploy"]["profile_name"]: self.key}, handle)
        info = self.path.stat()
        self.identity = (info.st_dev, info.st_ino)
        return self.path

    def __exit__(self, *exc) -> None:
        try:
            info = self.path.lstat()
            if (info.st_dev, info.st_ino) == self.identity:
                self.path.unlink()
        except FileNotFoundError:
            pass
        if self.created_dir:
            try:
                self.path.parent.rmdir()
            except OSError:
                pass


def trusted_remote_head() -> str:
    injected = os.environ.get("AUTO_PREVIEW_TEST_REMOTE_HEAD")
    if injected is not None and testing():
        if injected == "<network-error>":
            raise Stop(EXIT_REFUSED, "trusted branch HEAD unknown (network); no new upload")
        return injected
    try:
        out = subprocess.run(["git", "ls-remote", f"https://github.com/{TRUSTED_REPOSITORY}.git", TRUSTED_REF],
                             capture_output=True, text=True, timeout=60, check=True).stdout.split()
    except (subprocess.SubprocessError, OSError):
        raise Stop(EXIT_REFUSED, "trusted branch HEAD unknown (network); no new upload")
    if not out:
        raise Stop(EXIT_REFUSED, f"{TRUSTED_REF} does not exist on the remote; no upload")
    return out[0]


PENDING = ("uploading", "unknown_after_failure", "uploaded_unconfirmed")  # outcome not confirmed: blocks every new upload
VERIFY_WORKFLOW = ".github/workflows/verify.yml"
TRUSTED_BRANCH = TRUSTED_REF.removeprefix("refs/heads/")


def _verify_runs(commit: str) -> list[dict]:
    """Read-only GitHub REST: runs of the exact verify.yml workflow file for this commit on the trusted branch."""
    base = os.environ.get("AUTO_PREVIEW_TEST_GITHUB_API") if testing() else None
    url = (f"{base or 'https://api.github.com'}/repos/{TRUSTED_REPOSITORY}/actions/workflows/verify.yml/runs"
           f"?head_sha={commit}&branch={TRUSTED_BRANCH}&event=push&per_page=50")
    headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
    token = os.environ.get("GH_TOKEN", "")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
            body = json.loads(response.read().decode())
    except urllib.error.HTTPError as error:
        raise Stop(EXIT_REFUSED, f"Verify run lookup failed (HTTP {error.code}); not publishing")
    except (urllib.error.URLError, OSError, ValueError) as error:
        raise Stop(EXIT_REFUSED, f"Verify run lookup failed ({type(error).__name__}); not publishing")
    return list(body.get("workflow_runs") or [])


def verify_gate(commit: str, polls: int = 20, interval: float = 30.0) -> dict:
    """Fail closed unless the newest exact verify.yml push run for this repo/commit/branch is completed + success."""
    for attempt in range(max(1, polls)):
        matching = [r for r in _verify_runs(commit)
                    if r.get("path") == VERIFY_WORKFLOW and r.get("head_sha") == commit and r.get("head_branch") == TRUSTED_BRANCH
                    and r.get("event") == "push" and (r.get("repository") or {}).get("full_name") == TRUSTED_REPOSITORY
                    and (r.get("head_repository") or {}).get("full_name") == TRUSTED_REPOSITORY]
        if matching:
            latest = max(matching, key=lambda r: (str(r.get("created_at", "")), int(r.get("run_attempt") or 0), int(r.get("id") or 0)))
            if latest.get("status") == "completed":
                if latest.get("conclusion") == "success":
                    return {"verify_run_id": latest.get("id"), "run_attempt": latest.get("run_attempt"), "conclusion": "success",
                            "path": VERIFY_WORKFLOW, "head_sha": commit, "branch": TRUSTED_BRANCH}
                raise Stop(EXIT_REFUSED, f"Verify run {latest.get('id')} for {commit[:12]} concluded {latest.get('conclusion')!r}; not publishing")
        if attempt + 1 < max(1, polls):
            time.sleep(interval)
    raise Stop(EXIT_REFUSED, f"no completed successful {VERIFY_WORKFLOW} push run for {commit[:12]} on {TRUSTED_BRANCH} after {polls} checks; not publishing")


def verify_gate_settings() -> tuple[int, float]:
    injected = os.environ.get("AUTO_PREVIEW_TEST_VERIFY_POLL")
    if injected and testing():
        polls, interval = injected.split(",")
        return int(polls), float(interval)
    return int(CONFIG["verify_gate"]["polls"]), float(CONFIG["verify_gate"]["interval_seconds"])


def publish(args: argparse.Namespace) -> dict:
    receipt_path, ait = Path(args.receipt), Path(args.ait)
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    check_receipt(receipt, ait, args.approved_ait_sha, CONFIG["limits"]["receipt_max_age_hours"])
    dep_id, content = receipt["ait"]["deploymentId"], receipt["ait"]["content_sha256"]
    ledger_path = Path(args.ledger)
    ledger = load_ledger(ledger_path)
    out = Path(args.out)
    result = {"kind": "few-moves-toss-preview-publish", "created_utc": now(), "dry_run": args.dry_run, "deploymentId": dep_id,
              "ait_sha256": receipt["ait"]["sha256"], "content_sha256": content, "commit": receipt["commit"], "binding": BIND,
              "test_push": "not available on this API-key + GitHub-hosted path (the official CLI/deploy key have no test-push route); possible outside it via the console MCP account session (root, once) or the console test QR",
              "isTested_ready": False, "user_tested": False, "uploaded": False}
    for entry in ledger["entries"]:
        if entry["content_sha256"] == content and entry["status"] == "created":  # only a confirmed CREATED counts
            result.update(state="skipped_duplicate_content", previous=entry)
            write_json(out, result)
            return result
    for entry in ledger["entries"]:
        if entry["deploymentId"] == dep_id:
            raise Stop(EXIT_REFUSED, f"deployment {dep_id} was already attempted (ledger status {entry['status']!r}); a deploymentId is never uploaded twice")
        if entry["content_sha256"] == content and entry["status"] == "build_failed":
            raise Stop(EXIT_REFUSED, f"this content already failed the server build as {entry['deploymentId']}; change the inputs before uploading again")
    pending = [e for e in ledger["entries"] if e["status"] in PENDING]
    if pending:
        ids = ", ".join(e["deploymentId"] for e in pending)
        raise Stop(EXIT_REFUSED, f"unresolved earlier upload(s) {ids}: resolve with `status` (same deployment only); no new upload until then")
    memo = f"auto preview · {receipt['commit'][:8]} · {receipt['ait']['sha256'][:8]}"[: CONFIG["deploy"]["memo_max_chars"]]
    ait_bin = Path(args.project) / "node_modules" / ".bin" / "ait"
    if testing():
        ait_bin, result["fake_cli_for_tests"] = Path(os.environ["AUTO_PREVIEW_TEST_FAKE_AIT"]), True
    command = [str(ait_bin), "deploy", "--location", str(ait.resolve()), "--profile", CONFIG["deploy"]["profile_name"], "--memo", memo, "--scheme-only"]
    result["command"] = command
    if args.dry_run:
        result.update(state="dry_run_would_upload", note="no key read, no credentials file, no network")
        write_json(out, result)
        return result
    require_ci()
    head = trusted_remote_head()
    if head != receipt["commit"]:
        raise Stop(EXIT_REFUSED, f"stale: {TRUSTED_REF} is at {head[:12]}, receipt was built from {receipt['commit'][:12]}")
    result["verify_gate"] = verify_gate(receipt["commit"], *verify_gate_settings())  # before the deploy key is read
    key = deploy_key(os.environ)
    redactor = Redactor([key])
    entry = {"deploymentId": dep_id, "content_sha256": content, "ait_sha256": receipt["ait"]["sha256"], "commit": receipt["commit"],
             "status": "uploading", "started_utc": now()}
    ledger["entries"].append(entry)
    write_json(ledger_path, ledger)
    env = {k: v for k, v in os.environ.items() if k != "FEW_MOVES_AIT_DEPLOY_KEY"}
    env.update(CI="true", NO_COLOR="1")
    with CiCredentials(key):
        proc = run(command, Path(args.project), Path(args.log), env=env, redactor=redactor)
    output = redactor((proc.stdout or "") + (proc.stderr or "")).strip()
    link = next((line.strip() for line in output.splitlines() if line.strip().startswith("intoss-private://")), None)
    expected = f"intoss-private://{BIND['appName']}?_deploymentId={dep_id}&host=appsInTossHost"
    if proc.returncode != 0 or link != expected:
        entry.update(status="unknown_after_failure", error=output[-300:], finished_utc=now())
        write_json(ledger_path, ledger)
        result.update(state="failed", error=output[-300:], resume="run `status` for this deploymentId; do not re-upload")
        write_json(out, result)
        raise Stop(EXIT_FAIL, "official ait deploy did not complete", publish_receipt=str(out))
    server_status = fetch_status(dep_id, key)
    entry.update(status=ledger_status(server_status), server_status=server_status, private_link=link, finished_utc=now())
    write_json(ledger_path, ledger)
    if server_status != "CREATED":
        result.update(state="uploaded_not_created", server_status=server_status, private_link=link)
        write_json(out, result)
        raise Stop(EXIT_FAIL, f"server status {server_status} is not CREATED; not reported as a deployment", publish_receipt=str(out))
    result.update(state="uploaded", uploaded=True, server_status=server_status, private_link=link,
                  note="unlisted test upload only; not reviewed, not released; real-device test not done")
    write_json(out, result)
    return result


def status(args: argparse.Namespace) -> dict:
    if not UUID7.match(args.deployment_id):
        raise Stop(EXIT_INPUT, "deployment id is not a UUID v7")
    require_ci()
    ledger_path = Path(args.ledger)
    ledger = load_ledger(ledger_path)
    key = deploy_key(os.environ)
    server_status = fetch_status(args.deployment_id, key)
    for entry in ledger["entries"]:
        if entry["deploymentId"] == args.deployment_id:
            entry.update(server_status=server_status, status=ledger_status(server_status), checked_utc=now())
    write_json(ledger_path, ledger)
    return {"deploymentId": args.deployment_id, "server_status": server_status}


# ---------------------------------------------------------------- cli
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("gate")
    c = sub.add_parser("classify"); c.add_argument("--base-ref")
    b = sub.add_parser("build"); b.add_argument("--native-tar", required=True); b.add_argument("--work", required=True)
    b.add_argument("--base-ref"); b.add_argument("--offline", action="store_true")
    p = sub.add_parser("publish"); p.add_argument("--receipt", required=True); p.add_argument("--ait", required=True)
    p.add_argument("--project", required=True); p.add_argument("--ledger", required=True); p.add_argument("--out", required=True)
    p.add_argument("--log", required=True); p.add_argument("--approved-ait-sha"); p.add_argument("--dry-run", action="store_true")
    s = sub.add_parser("status"); s.add_argument("--deployment-id", required=True); s.add_argument("--ledger", required=True)
    vg = sub.add_parser("verify-gate"); vg.add_argument("--commit", required=True)
    args = parser.parse_args(argv)
    try:
        if args.cmd == "gate":
            result = gate(dict(os.environ))
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
                    handle.write(f"state={result['state']}\nrun_build={str(result['run_build']).lower()}\nrun_publish={str(result['run_publish']).lower()}\n")
        elif args.cmd == "classify":
            result = classify(args.base_ref)
        elif args.cmd == "build":
            result = build(args)
            result = {k: v for k, v in result.items() if k not in ("release_files", "ui_inputs", "classification")}
        elif args.cmd == "publish":
            result = publish(args)
        elif args.cmd == "verify-gate":
            require_ci()
            if trusted_remote_head() != args.commit:
                raise Stop(EXIT_REFUSED, f"stale: {TRUSTED_REF} HEAD is not {args.commit[:12]}")
            result = verify_gate(args.commit, *verify_gate_settings())
        else:
            result = status(args)
        print(json.dumps({"ok": True, **result}, ensure_ascii=False, indent=1))
        return 0
    except Stop as stop:
        print(json.dumps({"ok": False, "exit": stop.code, "error": str(stop), **stop.detail}, ensure_ascii=False), file=sys.stderr)
        return stop.code
    except (nb.NativeBaseError, OSError, KeyError, ValueError, json.JSONDecodeError, subprocess.CalledProcessError) as error:
        print(json.dumps({"ok": False, "exit": EXIT_INPUT, "error": f"{type(error).__name__}: {error}"}), file=sys.stderr)
        return EXIT_INPUT


if __name__ == "__main__":
    sys.exit(main())
