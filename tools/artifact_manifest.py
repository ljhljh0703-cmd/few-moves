#!/usr/bin/env python3
"""Fail-closed source/build tree consistency receipts for Nectorial."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath
from typing import Any
WATCHED_DIRS = ("Assets", "Packages", "ProjectSettings")
WATCHED_FILE = "global.json"
PROVENANCE_NAMES = frozenset(("unity-build.provenance.json", "artifact-provenance.json", "provenance.json"))
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
GIT_HEAD_RE = re.compile(r"^[0-9a-f]{40,64}$")
class ParityError(Exception):
    """A path, schema, git, or parity error."""
def _dupes(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result: raise ParityError(f"duplicate JSON key: {key}")
        result[key] = value
    return result
def _load(value: str, label: str) -> tuple[dict[str, Any], bytes]:
    path = Path(value)
    if not path.is_file():
        raise ParityError(f"{label} is not a file: {path}")
    try:
        raw = path.read_bytes(); data = json.loads(raw.decode("utf-8"), object_pairs_hook=_dupes)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError, ParityError) as exc:
        raise ParityError(f"invalid {label} {path}: {exc}") from exc
    if not isinstance(data, dict):
        raise ParityError(f"{label} root must be an object: {path}")
    return data, raw
def _need(data: dict[str, Any], fields: tuple[str, ...], label: str) -> None:
    missing = [field for field in fields if field not in data]
    if missing: raise ParityError(f"{label} missing field(s): {', '.join(missing)}")
def _rel(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value or value.startswith("/") or "\\" in value:
        raise ParityError(f"{label} must be a relative POSIX path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts): raise ParityError(f"{label} escapes its root: {value!r}")
    return path.as_posix()
def _safe(repo: Path, value: Any, label: str, file_required: bool = False) -> Path:
    relative = _rel(value, label)
    path = repo.joinpath(*PurePosixPath(relative).parts)
    current = repo
    for part in PurePosixPath(relative).parts:
        current /= part
        if current.is_symlink(): raise ParityError(f"{label} contains a symlink: {relative}")
    try:
        path.resolve(strict=False).relative_to(repo.resolve())
    except ValueError as exc: raise ParityError(f"{label} escapes repository: {relative}") from exc
    if file_required and not path.is_file(): raise ParityError(f"missing or unreadable file for {label}: {relative}")
    return path
def _repo(value: str) -> Path:
    path = Path(value)
    if not path.is_dir(): raise ParityError(f"repo is not a directory: {path}")
    return path.resolve()
def _check_links(repo: Path, path: Path) -> None:
    try:
        relative = path.absolute().relative_to(repo.absolute())
    except ValueError: return
    current = repo
    for part in relative.parts:
        current /= part
        if current.is_symlink(): raise ParityError(f"path contains a symlink: {relative}")
def _build(repo: Path, value: str) -> tuple[Path, str]:
    path = Path(value)
    if not path.is_absolute():
        path = repo / path
    _check_links(repo, path)
    try:
        relative = path.resolve(strict=False).relative_to(repo).as_posix()
    except ValueError as exc: raise ParityError(f"build directory must be inside repo: {path}") from exc
    relative = _rel(relative, "build_root")
    safe = _safe(repo, relative, "build_root")
    if not safe.is_dir(): raise ParityError(f"build directory is missing or unreadable: {path}")
    return safe, relative
def _output(repo: Path, value: str, build: Path | None = None) -> Path:
    path = Path(value)
    if path.is_symlink(): raise ParityError("receipt output must not be a symlink")
    resolved = path.resolve(strict=False)
    if build is not None:
        try:
            resolved.relative_to(build.resolve())
        except ValueError: pass
        else: raise ParityError("receipt output must be outside the build directory")
    try:
        relative = resolved.relative_to(repo).as_posix()
    except ValueError: relative = ""
    if relative and (relative == WATCHED_FILE or relative.split("/", 1)[0] in WATCHED_DIRS): raise ParityError("receipt output must not create a watched source file")
    if path.exists() and path.is_dir(): raise ParityError(f"receipt output is a directory: {path}")
    return path
def _write(path: Path, data: dict[str, Any]) -> None:
    try:
        path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except OSError as exc: raise ParityError(f"cannot write receipt {path}: {exc}") from exc
def _git(repo: Path, *args: str) -> bytes:
    try:
        result = subprocess.run(["git", *args], cwd=repo, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    except OSError as exc: raise ParityError(f"cannot run git: {exc}") from exc
    if result.returncode: raise ParityError(f"git {' '.join(args)} failed: {result.stderr.decode('utf-8', 'replace').strip()}")
    return result.stdout
def _names(repo: Path, *args: str) -> list[str]:
    try:
        text = _git(repo, *args).decode("utf-8")
    except UnicodeDecodeError as exc: raise ParityError("git returned a non-UTF-8 path") from exc
    return sorted(item for item in text.split("\0") if item)
def _head(repo: Path) -> str:
    try:
        value = _git(repo, "rev-parse", "HEAD").decode("ascii").strip()
    except UnicodeError as exc: raise ParityError("git HEAD is not ASCII") from exc
    if not GIT_HEAD_RE.fullmatch(value): raise ParityError(f"unexpected git HEAD: {value!r}")
    return value
def _clean(repo: Path) -> None:
    changed = _names(repo, "diff", "--name-only", "-z", "HEAD", "--", *WATCHED_DIRS, WATCHED_FILE)
    untracked = _names(repo, "ls-files", "--others", "-z", "--", *WATCHED_DIRS, WATCHED_FILE)
    if paths := sorted(set(changed + untracked)): raise ParityError("watched inputs are modified or untracked: " + ", ".join(paths))


def _watch_roots(repo: Path) -> None:
    for relative in WATCHED_DIRS:
        if not _safe(repo, relative, "watched directory").is_dir(): raise ParityError(f"missing watched directory: {relative}")
def _file(path: Path, relative: str, label: str) -> dict[str, Any]:
    try:
        raw = path.read_bytes()
    except OSError as exc: raise ParityError(f"cannot read {label} {relative}: {exc}") from exc
    return {"path": relative, "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}
def _sources(repo: Path) -> list[dict[str, Any]]:
    _watch_roots(repo)
    paths = _names(repo, "ls-files", "-z", "--", *WATCHED_DIRS, WATCHED_FILE)
    if not paths: raise ParityError("no tracked watched source files")
    result = []
    for relative in paths:
        result.append(_file(_safe(repo, relative, "source path", True), relative, "source path"))
    return result
def _build_files(build: Path) -> list[dict[str, Any]]:
    result = []
    def onerror(error: OSError) -> None:
        raise ParityError(f"cannot read build directory: {error}")
    for root, dirs, files in os.walk(build, topdown=True, followlinks=False, onerror=onerror):
        for directory in dirs:
            if (Path(root) / directory).is_symlink(): raise ParityError(f"build tree contains a symlink: {Path(root) / directory}")
        for name in files:
            path = Path(root) / name
            if path.is_symlink() or not path.is_file(): raise ParityError(f"build tree contains a non-regular file: {path}")
            result.append(_file(path, _rel(path.relative_to(build).as_posix(), "build file path"), "build file"))
    return sorted(result, key=lambda item: item["path"])
def _tree(records: list[dict[str, Any]]) -> str:
    digest = hashlib.sha256()
    for item in records:
        digest.update(item["path"].encode() + b"\0" + str(item["bytes"]).encode() + b"\0" + item["sha256"].encode() + b"\n")
    return digest.hexdigest()
def _records(value: Any, label: str) -> list[dict[str, Any]]:
    if not isinstance(value, list): raise ParityError(f"{label} must be an array")
    result = []
    for index, item in enumerate(value):
        if not isinstance(item, dict): raise ParityError(f"{label}[{index}] must be an object")
        _need(item, ("path", "bytes", "sha256"), f"{label}[{index}]")
        path, size, checksum = _rel(item["path"], f"{label}[{index}].path"), item["bytes"], item["sha256"]
        if isinstance(size, bool) or not isinstance(size, int) or size < 0 or not isinstance(checksum, str) or not SHA256_RE.fullmatch(checksum):
            raise ParityError(f"invalid file record at {label}[{index}]")
        result.append({"path": path, "bytes": size, "sha256": checksum})
    paths = [item["path"] for item in result]
    if paths != sorted(paths) or len(paths) != len(set(paths)): raise ParityError(f"{label} paths must be sorted and unique")
    return result
def _git_sha(value: Any, label: str) -> str:
    if not isinstance(value, str) or not GIT_HEAD_RE.fullmatch(value): raise ParityError(f"{label} must be a git commit SHA")
    return value
def _snapshot(value: str) -> dict[str, Any]:
    data, _ = _load(value, "snapshot")
    _need(data, ("schema_version", "kind", "git_head", "source_files", "source_sha256"), "snapshot")
    if data["schema_version"] != 1 or data["kind"] != "nectorial-source-snapshot": raise ParityError("unsupported snapshot schema")
    source = _records(data["source_files"], "snapshot.source_files")
    if data["source_sha256"] != _tree(source): raise ParityError("snapshot source SHA does not match its file records")
    return {"git_head": _git_sha(data["git_head"], "snapshot.git_head"), "source_files": source}
def _manifest(value: str) -> dict[str, Any]:
    data, _ = _load(value, "manifest")
    fields = ("schema_version", "kind", "snapshot_git_head", "seal_git_head", "build_root", "source_files", "source_sha256", "build_files", "build_sha256", "provenance_files")
    _need(data, fields, "manifest")
    if data["schema_version"] != 1 or data["kind"] != "nectorial-artifact-manifest": raise ParityError("unsupported artifact manifest schema")
    source, build = _records(data["source_files"], "manifest.source_files"), _records(data["build_files"], "manifest.build_files")
    if data["source_sha256"] != _tree(source) or data["build_sha256"] != _tree(build): raise ParityError("manifest tree SHA does not match its file records")
    root = _rel(data["build_root"], "manifest.build_root")
    provenance = data["provenance_files"]
    if not isinstance(provenance, list) or not provenance or any(not isinstance(item, str) for item in provenance): raise ParityError("manifest.provenance_files must be a non-empty array")
    provenance = [_rel(item, "manifest.provenance_files") for item in provenance]
    paths = {item["path"] for item in build}
    if provenance != sorted(set(provenance)) or not set(provenance) <= paths or not any(PurePosixPath(item).name in PROVENANCE_NAMES for item in provenance): raise ParityError("manifest provenance files must be sorted, present, and named")
    return {"snapshot_git_head": _git_sha(data["snapshot_git_head"], "manifest.snapshot_git_head"), "seal_git_head": _git_sha(data["seal_git_head"], "manifest.seal_git_head"), "build_root": root, "source_files": source, "build_files": build, "provenance_files": provenance}
def snapshot(repo_value: str, out_value: str) -> None:
    repo = _repo(repo_value)
    out = _output(repo, out_value)
    head = _head(repo)
    _clean(repo)
    source = _sources(repo)
    _write(out, {"schema_version": 1, "kind": "nectorial-source-snapshot", "watched_dirs": list(WATCHED_DIRS), "watched_file_if_tracked": WATCHED_FILE, "git_head": head, "source_files": source, "source_sha256": _tree(source)})
def seal(repo_value: str, snapshot_value: str, build_value: str, out_value: str) -> None:
    repo = _repo(repo_value)
    snap = _snapshot(snapshot_value)
    build, build_root = _build(repo, build_value)
    out = _output(repo, out_value, build)
    head = _head(repo)
    _clean(repo)
    source = _sources(repo)
    if source != snap["source_files"]: raise ParityError("watched source tree differs from snapshot")
    artifacts = _build_files(build)
    provenance = sorted(item["path"] for item in artifacts if PurePosixPath(item["path"]).name in PROVENANCE_NAMES)
    if not provenance: raise ParityError("build must contain unity-build.provenance.json, artifact-provenance.json, or provenance.json")
    _write(out, {"schema_version": 1, "kind": "nectorial-artifact-manifest", "snapshot_git_head": snap["git_head"], "seal_git_head": head, "git_head_changed": snap["git_head"] != head, "build_root": build_root, "source_files": source, "source_sha256": _tree(source), "build_files": artifacts, "build_sha256": _tree(artifacts), "provenance_files": provenance})
def verify(repo_value: str, manifest_value: str, build_value: str) -> dict[str, Any]:
    repo = _repo(repo_value)
    manifest = _manifest(manifest_value)
    build, build_root = _build(repo, build_value)
    if build_root != manifest["build_root"]: raise ParityError(f"build root differs from manifest: {build_root} != {manifest['build_root']}")
    head = _head(repo)
    _clean(repo)
    source, artifacts = _sources(repo), _build_files(build)
    if source != manifest["source_files"]: raise ParityError("watched source tree differs from artifact manifest")
    if artifacts != manifest["build_files"]: raise ParityError("build tree differs from artifact manifest")
    return {"kind": "nectorial-artifact-parity-verification", "status": "pass", "snapshot_git_head": manifest["snapshot_git_head"], "seal_git_head": manifest["seal_git_head"], "verified_git_head": head, "git_head_changed": head != manifest["seal_git_head"], "source_sha256": _tree(source), "build_sha256": _tree(artifacts), "provenance_files": manifest["provenance_files"], "runtime_execution": "not_performed", "build_execution_proof": "not_proven_by_manifest"}
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    snap = sub.add_parser("snapshot")
    snap.add_argument("--repo", required=True)
    snap.add_argument("--out", required=True)
    seal_parser = sub.add_parser("seal")
    seal_parser.add_argument("--repo", required=True)
    seal_parser.add_argument("--snapshot", required=True)
    seal_parser.add_argument("--build", required=True)
    seal_parser.add_argument("--out", required=True)
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--repo", required=True)
    verify_parser.add_argument("--manifest", required=True)
    verify_parser.add_argument("--build", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "snapshot":
            snapshot(args.repo, args.out)
        elif args.command == "seal":
            seal(args.repo, args.snapshot, args.build, args.out)
        else:
            print(json.dumps(verify(args.repo, args.manifest, args.build), indent=2, sort_keys=True))
        return 0
    except (ParityError, OSError, ValueError, UnicodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
if __name__ == "__main__":
    raise SystemExit(main())
