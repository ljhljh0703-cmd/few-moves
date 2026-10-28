#!/usr/bin/env python3
"""Reviewed native base for the Toss preview pipeline.

The native base is the licensed-Unity output that a UI-only preview reuses byte for byte:
solo/** and raid/** (Build/*, provenance), shared/Build/<wasm>, and the SDK bridge bundle
platform/ait-unity-bridge.js. It is pinned by tools/auto_preview/native-base.lock.json, which also
records a fingerprint of every Unity input (Assets/, Packages/, ProjectSettings/) it was built from.

  python3 -B tools/auto_preview/native_base.py fingerprint
  python3 -B tools/auto_preview/native_base.py pack --source DIR --bridge FILE --out TAR --lock LOCK --provenance JSON
  python3 -B tools/auto_preview/native_base.py verify --tar TAR [--lock LOCK]
  python3 -B tools/auto_preview/native_base.py unpack --tar TAR --dest DIR [--lock LOCK]
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[2]
CONFIG = json.loads((Path(__file__).resolve().parent / "config.json").read_text(encoding="utf-8"))
LOCK = ROOT / CONFIG["native"]["lock"]
BASE_DIRS = ("solo/", "raid/", "shared/")
BRIDGE_REL = "platform/ait-unity-bridge.js"


class NativeBaseError(Exception):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tree_digest(entries: dict[str, str]) -> str:
    return sha256_bytes("".join(f"{k}\0{v}\n" for k, v in sorted(entries.items())).encode())


def native_input_fingerprint(root: Path = ROOT) -> dict[str, str]:
    """sha256 of every git-tracked and untracked-not-ignored file under the Unity input prefixes (working tree)."""
    prefixes = CONFIG["native"]["input_prefixes"]
    listed: set[str] = set()
    for args in (["ls-files", "-z", "--"], ["ls-files", "-z", "--others", "--exclude-standard", "--"]):
        out = subprocess.run(["git", "-C", str(root), *args, *prefixes], capture_output=True, check=True).stdout
        listed.update(p for p in out.decode().split("\0") if p)
    result = {}
    for rel in sorted(listed):
        path = root / rel
        if path.is_file():
            result[rel] = sha256_file(path)
        else:
            result[rel] = "<deleted>"
    return result


def compare_native_inputs(lock: dict, current: dict[str, str]) -> list[dict]:
    pinned = lock["native_inputs"]["files"]
    changes = []
    for rel in sorted(set(pinned) | set(current)):
        before, after = pinned.get(rel), current.get(rel)
        if before != after:
            kind = "added" if before is None else "removed" if after is None or after == "<deleted>" else "modified"
            changes.append({"path": rel, "change": kind, "pinned_sha256": before, "current_sha256": after})
    return changes


def _base_files(source: Path, bridge: Path) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise NativeBaseError(f"symlink in native source: {path}")
        rel = path.relative_to(source).as_posix()
        if path.is_file() and rel.startswith(BASE_DIRS):
            files[rel] = path.read_bytes()
    if not any(r.startswith("solo/Build/") for r in files) or not any(r.startswith("raid/Build/") for r in files):
        raise NativeBaseError("native source lacks solo/Build or raid/Build")
    files[BRIDGE_REL] = bridge.read_bytes()
    return files


def deterministic_tar(files: dict[str, bytes]) -> bytes:
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for rel in sorted(files):
            info = tarfile.TarInfo(rel)
            info.size, info.mtime, info.mode, info.uid, info.gid, info.uname, info.gname = len(files[rel]), 0, 0o644, 0, 0, "", ""
            tar.addfile(info, io.BytesIO(files[rel]))
    out = io.BytesIO()
    with gzip.GzipFile(fileobj=out, mode="wb", mtime=0, compresslevel=9) as gz:
        gz.write(raw.getvalue())
    return out.getvalue()


def read_tar(data: bytes) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        for member in tar.getmembers():
            rel = PurePosixPath(member.name)
            if not member.isreg() or rel.is_absolute() or ".." in rel.parts or str(rel) != member.name:
                raise NativeBaseError(f"unsafe or non-regular tar member: {member.name!r}")
            if member.name in files:
                raise NativeBaseError(f"duplicate tar member: {member.name}")
            files[member.name] = tar.extractfile(member).read()
    return files


def verify_tar(data: bytes, lock: dict) -> dict[str, bytes]:
    if sha256_bytes(data) != lock["tar"]["sha256"] or len(data) != lock["tar"]["bytes"]:
        raise NativeBaseError("native base archive sha256/bytes do not match the lock")
    files = read_tar(data)
    pinned = lock["files"]
    if set(files) != set(pinned):
        raise NativeBaseError(f"native base file set differs: extra={sorted(set(files) - set(pinned))} missing={sorted(set(pinned) - set(files))}")
    for rel, blob in files.items():
        if sha256_bytes(blob) != pinned[rel]["sha256"]:
            raise NativeBaseError(f"native base file differs: {rel}")
    return files


def load_lock(path: Path = LOCK) -> dict:
    if not path.is_file():
        raise NativeBaseError(f"native base lock missing: {path}")
    lock = json.loads(path.read_text(encoding="utf-8"))
    for key in ("base_id", "tar", "files", "native_inputs", "framework", "binding"):
        if key not in lock:
            raise NativeBaseError(f"native base lock lacks {key}")
    if lock["binding"]["appName"] != CONFIG["binding"]["appName"]:
        raise NativeBaseError("native base lock is bound to another app")
    return lock


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    sub.add_parser("fingerprint")
    p = sub.add_parser("pack")
    p.add_argument("--source", required=True); p.add_argument("--bridge", required=True)
    p.add_argument("--out", required=True); p.add_argument("--lock", required=True); p.add_argument("--provenance", required=True)
    v = sub.add_parser("verify"); v.add_argument("--tar", required=True); v.add_argument("--lock", default=str(LOCK))
    u = sub.add_parser("unpack"); u.add_argument("--tar", required=True); u.add_argument("--dest", required=True); u.add_argument("--lock", default=str(LOCK))
    args = parser.parse_args(argv)
    try:
        if args.cmd == "fingerprint":
            fp = native_input_fingerprint()
            print(json.dumps({"files": len(fp), "sha256": tree_digest(fp)}))
        elif args.cmd == "pack":
            out, lock_path = Path(args.out), Path(args.lock)
            if out.exists() or lock_path.exists():
                raise NativeBaseError("output archive or lock already exists")
            files = _base_files(Path(args.source), Path(args.bridge))
            data = deterministic_tar(files)
            frameworks = sorted({PurePosixPath(r).name for r in files if r.endswith(".framework.js")})
            if len(frameworks) != 1:
                raise NativeBaseError(f"expected one shared framework.js name, found {frameworks}")
            fp = native_input_fingerprint()
            lock = {
                "schema": 1,
                "base_id": out.name.removesuffix(".tar.gz"),
                "binding": CONFIG["binding"],
                "tar": {"name": out.name, "sha256": sha256_bytes(data), "bytes": len(data)},
                "files": {rel: {"sha256": sha256_bytes(b), "bytes": len(b)} for rel, b in sorted(files.items())},
                "framework": {"name": frameworks[0], "coop_api_residual": 1},
                "native_inputs": {"prefixes": CONFIG["native"]["input_prefixes"], "sha256": tree_digest(fp), "files": fp},
                "provenance": json.loads(Path(args.provenance).read_text(encoding="utf-8")),
            }
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(data)
            lock_path.write_text(json.dumps(lock, indent=1, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")
            print(json.dumps({"ok": True, "tar": lock["tar"], "files": len(files), "native_inputs": len(fp)}))
        elif args.cmd == "verify":
            files = verify_tar(Path(args.tar).read_bytes(), load_lock(Path(args.lock)))
            print(json.dumps({"ok": True, "files": len(files)}))
        elif args.cmd == "unpack":
            dest = Path(args.dest)
            if dest.exists():
                raise NativeBaseError(f"destination exists: {dest}")
            files = verify_tar(Path(args.tar).read_bytes(), load_lock(Path(args.lock)))
            for rel, blob in files.items():
                target = dest / rel
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(blob)
            print(json.dumps({"ok": True, "dest": str(dest), "files": len(files)}))
    except (NativeBaseError, OSError, KeyError, ValueError, tarfile.TarError, subprocess.CalledProcessError) as error:
        print(json.dumps({"ok": False, "error": str(error)}), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
