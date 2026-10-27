#!/usr/bin/env python3
"""Derive a ONE store H5 Web candidate from an already verified Few Moves web root.

The source root is never modified. The output is a copy in which the only changes are:
  * platform/fewmoves-onestore.js (the bridge from web/platforms/onestore), and
  * one classic <script src> inserted right after <meta charset> in index.html, solo/, coop/ and raid/ pages,
    so it is the first script of each top-level game document (ONE SDK must load before game code).
Unity builds, templates and game files are byte-identical. This is not an upload or a hosting claim:
the pages must be served over HTTPS from the co-op server origin (/api/coop/v1 is same-origin) with
ONE store frame-ancestors allowed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from prepare_web_release import PackageError, _check_paths, _scan, _sha256  # noqa: E402  (verified helpers)

ROOT = Path(__file__).resolve().parents[1]
BRIDGE_SOURCE = ROOT / "web" / "platforms" / "onestore" / "fewmoves-onestore.js"
BRIDGE_REL = "platform/fewmoves-onestore.js"
MARKER = "onestore-package.json"
PAGES = {"index.html": "", "solo/index.html": "../", "coop/index.html": "../", "raid/index.html": "../"}
HEAD_META_RE = re.compile(rb"<head>\r?\n(?P<indent>[ \t]*)<meta charset=\"utf-8\">\r?\n")
SDK_URL_RE = re.compile(r'var SDK_URL = "(?P<url>https://h5sdk\.onestore\.net/lib/v[0-9.]+/onestore-h5-sdk(?:\.min)?\.js)";')
FORBIDDEN_IN_PAGE = (b"onestore-h5-sdk", b"fewmoves-onestore", b"http-equiv=\"Content-Security-Policy\"")


def _script_tag(prefix: str) -> bytes:
    return f'<script src="{prefix}{BRIDGE_REL}"></script>'.encode()


def _inject(html: bytes, rel: str, prefix: str) -> tuple[bytes, dict[str, Any]]:
    for needle in FORBIDDEN_IN_PAGE:
        if needle.lower() in html.lower():
            raise PackageError(f"{rel} already references {needle.decode()}; refusing to inject twice or under a CSP meta")
    matches = list(HEAD_META_RE.finditer(html))
    if len(matches) != 1 or html.count(b"<head>") != 1:
        raise PackageError(f"{rel} must have exactly one <head> followed by <meta charset=\"utf-8\">")
    match = matches[0]
    if b"<script" in html[: match.end()].lower():
        raise PackageError(f"{rel} has a script before the injection point")
    insertion = match.group("indent") + _script_tag(prefix) + b"\n"
    offset = match.end()
    return html[:offset] + insertion + html[offset:], {"offset": offset, "inserted": insertion.decode()}


def prepare(source: Path, out: Path, receipt_path: Path | None = None) -> dict[str, Any]:
    source = Path(os.path.abspath(source))
    out = Path(os.path.abspath(out))
    receipt_path = Path(os.path.abspath(receipt_path)) if receipt_path is not None else None
    _check_paths(source, out, receipt_path)
    source_files = _scan(source, "source")
    for rel in PAGES:
        if rel not in source_files:
            raise PackageError(f"missing required page: {rel}")
    if any(rel == "platform" or rel.startswith("platform/") or rel == MARKER for rel in source_files):
        raise PackageError("source already contains platform/ or a ONE store marker")
    bridge_text = BRIDGE_SOURCE.read_text(encoding="utf-8")
    sdk = SDK_URL_RE.search(bridge_text)
    if not sdk:
        raise PackageError("bridge SDK_URL is not a pinned h5sdk.onestore.net version")
    bridge_sha = _sha256(BRIDGE_SOURCE)

    edits: dict[str, dict[str, Any]] = {}
    staging = Path(tempfile.mkdtemp(prefix=f".{out.name}.staging-", dir=out.parent))
    try:
        for rel in source_files:
            target = staging / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            if rel in PAGES:
                original = (source / rel).read_bytes()
                patched, edit = _inject(original, rel, PAGES[rel])
                target.write_bytes(patched)
                edit.update({"source_sha256": source_files[rel]["sha256"], "output_sha256": hashlib.sha256(patched).hexdigest()})
                edits[rel] = edit
            else:
                shutil.copy2(source / rel, target)
        (staging / "platform").mkdir()
        shutil.copy2(BRIDGE_SOURCE, staging / BRIDGE_REL)
        manifest = {
            "state": "provisional",
            "kind": "few-moves-onestore-h5-candidate",
            "not_an_upload": True,
            "sdk_url": sdk.group("url"),
            "bridge": {"path": BRIDGE_REL, "sha256": bridge_sha},
            "source_root": str(source),
            "edits": edits,
            "hosting_requirements": [
                "HTTPS public URL registered as the ONE Web product test/game URL",
                "served by the co-op server origin so /api/coop/v1 stays same-origin",
                "final response allows frame-ancestors https://*.onestore.co.kr https://*.onestore.net (or sends neither XFO nor CSP)",
            ],
        }
        (staging / MARKER).write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        result = _verify(source, staging, source_files, edits, bridge_sha)
        os.rename(staging, out)
    except BaseException:
        shutil.rmtree(staging, ignore_errors=True)
        raise
    if _scan(source, "source") != source_files:
        raise PackageError("source changed while packaging")
    receipt = {"ok": True, "source": str(source), "out": str(out), "sdk_url": sdk.group("url"), "bridge_sha256": bridge_sha,
               "pages": sorted(edits), "files": result["files"], "bytes": result["bytes"],
               "marker_sha256": _sha256(out / MARKER)}
    if receipt_path is not None:
        with receipt_path.open("x", encoding="utf-8") as handle:
            json.dump(receipt, handle, indent=2, sort_keys=True)
            handle.write("\n")
    return receipt


def _verify(source: Path, staged: Path, source_files: dict[str, dict[str, Any]], edits: dict[str, dict[str, Any]], bridge_sha: str) -> dict[str, Any]:
    out_files = _scan(staged, "output")
    expected = set(source_files) | {BRIDGE_REL, MARKER}
    if set(out_files) != expected:
        raise PackageError(f"output file set differs: {sorted(set(out_files) ^ expected)}")
    for rel, info in source_files.items():
        if rel in edits:
            patched = (staged / rel).read_bytes()
            original = (source / rel).read_bytes()
            inserted = edits[rel]["inserted"].encode()
            offset = edits[rel]["offset"]
            if patched != original[:offset] + inserted + original[offset:] or patched.count(b"fewmoves-onestore.js") != 1:
                raise PackageError(f"{rel} differs beyond the single bridge script")
        elif out_files[rel]["sha256"] != info["sha256"]:
            raise PackageError(f"{rel} changed during copy")
    if out_files[BRIDGE_REL]["sha256"] != bridge_sha:
        raise PackageError("bridge copy mismatch")
    return {"files": len(out_files), "bytes": sum(item["bytes"] for item in out_files.values())}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="verified web root with index.html, solo/, coop/, raid/")
    parser.add_argument("--out", required=True, help="new output directory; must not exist")
    parser.add_argument("--receipt", help="optional receipt JSON path; must not exist")
    args = parser.parse_args(argv)
    try:
        receipt = prepare(Path(args.source), Path(args.out), Path(args.receipt) if args.receipt else None)
    except PackageError as error:
        print(json.dumps({"ok": False, "error": str(error)}), file=sys.stderr)
        return 2
    print(json.dumps(receipt, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
