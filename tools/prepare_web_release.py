#!/usr/bin/env python3
"""Derive a web package that shares one byte-identical Unity .wasm across modes.

The source web root is never modified. The output is a post-processed derived
package: it is not a Unity build, not an .ait, and not an upload candidate. The
copied provenance files describe the original per-mode builds only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import sys
import tempfile
from pathlib import Path, PurePosixPath
from typing import Any

MODES = ("solo", "coop", "raid")
SHARED_DIR = PurePosixPath("shared/Build")
DERIVED_MARKER = "derived-package.json"
WASM_NAME_RE = re.compile(r"^[0-9A-Za-z_-]+\.wasm$")
# Unity templates write codeUrl either as a literal or as buildUrl + "/name".
# Only the value span (the part after "codeUrl:") is ever replaced.
CODE_URL_RE = re.compile(
    rb'codeUrl\s*:\s*(?P<value>"(?P<literal>[^"]*)"|buildUrl\s*\+\s*"(?P<suffix>/[^"]*)")'
)
# Any write to buildUrl: plain or compound assignment, but not a == / === comparison.
BUILD_URL_ASSIGN_RE = re.compile(rb'\bbuildUrl\s*(?P<op>\*\*|>>>|<<|>>|&&|\|\||\?\?|[-+*/%&|^])?=(?!=)')
# The only supported right-hand side: exactly the literal "Build" then the statement's ";".
# Concatenation, logical/conditional operators, comments, or a missing ";" are rejected, not parsed.
BUILD_URL_RHS_RE = re.compile(rb'[ \t]*"Build"[ \t]*;')
BUILD_REF_RE = re.compile(rb'[0-9a-f]{32}\.(?:data|framework\.js|loader\.js|symbols\.json|wasm)')


class PackageError(Exception):
    """A rejected input, path, or verification failure."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _scan(root: Path, label: str) -> dict[str, dict[str, Any]]:
    """Return {relative posix path: {bytes, sha256}} for a tree of regular files only."""
    files: dict[str, dict[str, Any]] = {}
    for current, dirs, names in os.walk(root, followlinks=False):
        base = Path(current)
        for name in list(dirs) + names:
            info = os.lstat(base / name)
            rel = (base / name).relative_to(root).as_posix()
            if stat.S_ISLNK(info.st_mode):
                raise PackageError(f"{label} contains a symlink: {rel}")
            if name in dirs:
                continue
            if not stat.S_ISREG(info.st_mode):
                raise PackageError(f"{label} contains a non-regular file: {rel}")
            if info.st_nlink != 1:
                raise PackageError(f"{label} contains a hard-linked file: {rel}")
            files[rel] = {"bytes": info.st_size, "sha256": _sha256(base / name)}
    return dict(sorted(files.items()))


def _code_url(html: bytes, label: str) -> tuple[re.Match[bytes], str]:
    matches = list(CODE_URL_RE.finditer(html))
    if len(matches) != 1:
        raise PackageError(f"{label} must contain exactly one codeUrl, found {len(matches)}")
    match = matches[0]
    if match.group("literal") is not None:
        return match, match.group("literal").decode("utf-8")
    # A buildUrl reference is accepted only when the page assigns it exactly once, as `buildUrl = "Build";`.
    assignments = list(BUILD_URL_ASSIGN_RE.finditer(html))
    if len(assignments) != 1 or assignments[0].group("op") is not None or not BUILD_URL_RHS_RE.match(html, assignments[0].end()):
        found = [html[a.start():a.end() + 40].decode("utf-8", "replace") for a in assignments]
        raise PackageError(f'{label} codeUrl uses buildUrl, which must be assigned exactly once as "Build": {found}')
    return match, "Build" + match.group("suffix").decode("utf-8")


def _mode_wasm(root: Path, mode: str) -> dict[str, Any]:
    html_path = root / mode / "index.html"
    if not html_path.is_file():
        raise PackageError(f"missing mode page: {mode}/index.html")
    _, value = _code_url(html_path.read_bytes(), f"{mode}/index.html")
    rel = PurePosixPath(value)
    if rel.is_absolute() or len(rel.parts) != 2 or rel.parts[0] != "Build" or not WASM_NAME_RE.fullmatch(rel.name):
        raise PackageError(f"{mode}/index.html codeUrl is not Build/<name>.wasm: {value!r}")
    wasm = root / mode / "Build" / rel.name
    if not wasm.is_file():
        raise PackageError(f"{mode}/index.html codeUrl target is missing: {mode}/{value}")
    return {"mode": mode, "name": rel.name, "source_path": f"{mode}/Build/{rel.name}", "sha256": _sha256(wasm), "bytes": wasm.stat().st_size}


def _identity(path: Path) -> tuple[int, int]:
    info = os.stat(path)
    return info.st_dev, info.st_ino


def _reject_symlink_components(path: Path, label: str) -> None:
    """Reject a symlink at any existing component of an absolute path, whatever it points to."""
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        if not os.path.lexists(current):
            break
        if stat.S_ISLNK(os.lstat(current).st_mode):
            raise PackageError(f"{label} path has a symlink component: {current}")


def _ancestor_ids(directory: Path) -> set[tuple[int, int]]:
    """Identities of every ancestor, so a case-aliased parent cannot hide containment."""
    ids: set[tuple[int, int]] = set()
    current = directory
    while True:
        ids.add(_identity(current))
        if current.parent == current:
            break
        current = current.parent
    return ids


def _check_new_path(path: Path, label: str) -> None:
    _reject_symlink_components(path, label)
    if os.path.lexists(path):
        raise PackageError(f"{label} already exists: {path}")
    if not path.parent.is_dir():
        raise PackageError(f"{label} parent must be an existing directory: {path.parent}")


def _same_new_path(a: Path, b: Path) -> bool:
    return _identity(a.parent) == _identity(b.parent) and a.name.casefold() == b.name.casefold()


def _check_paths(source: Path, out: Path, receipt: Path | None) -> None:
    _reject_symlink_components(source, "source")
    if not source.is_dir():
        raise PackageError(f"source must be a real directory: {source}")
    _check_new_path(out, "output")
    source_id = _identity(source)
    if source_id in _ancestor_ids(out.parent):
        raise PackageError("source and output must not contain each other")
    if receipt is None:
        return
    _check_new_path(receipt, "receipt")
    if source_id in _ancestor_ids(receipt.parent):
        raise PackageError("receipt must not be inside the source")
    # The output does not exist yet, so a receipt inside it already failed the parent check;
    # an equal path, including a case alias, is rejected here.
    if _same_new_path(receipt, out):
        raise PackageError("receipt must not be the output path")


def _rewrite(html: bytes, mode: str, shared_url: str) -> tuple[bytes, dict[str, Any]]:
    match, _ = _code_url(html, f"{mode}/index.html")
    start, end = match.span("value")
    replacement = json.dumps(shared_url).encode("utf-8")
    return html[:start] + replacement + html[end:], {"start": start, "end": end, "replacement": replacement}


def _verify(source: Path, out: Path, wasm: dict[str, Any], source_files: dict[str, dict[str, Any]], removed: set[str], edits: dict[str, dict[str, Any]]) -> dict[str, Any]:
    out_files = _scan(out, "output")
    shared_rel = (SHARED_DIR / wasm["name"]).as_posix()
    resolutions = []
    for mode in MODES:
        original = (source / mode / "index.html").read_bytes()
        html = (out / mode / "index.html").read_bytes()
        edit = edits[mode]
        start, end, replacement = edit["start"], edit["end"], edit["replacement"]
        # Every byte outside the codeUrl value span must be unchanged, line endings included.
        if html[:start] != original[:start] or html[start + len(replacement):] != original[end:] or html[start:start + len(replacement)] != replacement:
            raise PackageError(f"output {mode}/index.html changed bytes outside the codeUrl value")
        _, value = _code_url(html, f"output {mode}/index.html")
        target = (out / mode / value).resolve()
        try:
            target_rel = target.relative_to(out.resolve()).as_posix()
        except ValueError as exc:
            raise PackageError(f"output {mode} codeUrl escapes the package: {value}") from exc
        if target_rel != shared_rel or target_rel not in out_files:
            raise PackageError(f"output {mode} codeUrl does not resolve to {shared_rel}: {value}")
        if out_files[target_rel]["sha256"] != wasm["sha256"]:
            raise PackageError(f"output {mode} codeUrl target hash differs")
        for ref in sorted(set(BUILD_REF_RE.findall(html))):
            name = ref.decode("ascii")
            expected = shared_rel if name == wasm["name"] else f"{mode}/Build/{name}"
            if expected not in out_files:
                raise PackageError(f"output {mode}/index.html references a missing file: {name}")
        resolutions.append({"mode": mode, "code_url": value, "resolved": target_rel, "sha256": out_files[target_rel]["sha256"], "value_span": [start, end]})
    for rel, entry in source_files.items():
        if rel in removed:
            if rel in out_files:
                raise PackageError(f"per-mode wasm was not removed: {rel}")
            continue
        if rel not in out_files:
            raise PackageError(f"output is missing source file: {rel}")
        if rel in {f"{mode}/index.html" for mode in MODES}:
            continue
        if out_files[rel] != entry:
            raise PackageError(f"output file differs from source: {rel}")
    expected = set(source_files) - removed | {shared_rel, DERIVED_MARKER}
    extra = sorted(set(out_files) - expected)
    if extra:
        raise PackageError(f"output has unexpected files: {extra}")
    return {"files": out_files, "code_url_resolution": resolutions}


def prepare(source: Path, out: Path, receipt_path: Path | None = None) -> dict[str, Any]:
    source = Path(os.path.abspath(source))
    out = Path(os.path.abspath(out))
    receipt_path = Path(os.path.abspath(receipt_path)) if receipt_path is not None else None
    _check_paths(source, out, receipt_path)
    source_files = _scan(source, "source")
    for required in ("index.html",) + tuple(f"{mode}/index.html" for mode in MODES):
        if required not in source_files:
            raise PackageError(f"missing required page: {required}")
    if any(rel == "shared" or rel.startswith("shared/") or rel == DERIVED_MARKER for rel in source_files):
        raise PackageError("source already contains shared/ or a derived marker")
    wasms = [_mode_wasm(source, mode) for mode in MODES]
    names = {entry["name"] for entry in wasms}
    hashes = {entry["sha256"] for entry in wasms}
    if len(names) != 1 or len(hashes) != 1:
        raise PackageError(f"mode wasm files are not byte-identical under one name: {wasms}")
    wasm = {"name": wasms[0]["name"], "sha256": wasms[0]["sha256"], "bytes": wasms[0]["bytes"]}
    removed = {entry["source_path"] for entry in wasms}
    shared_rel = (SHARED_DIR / wasm["name"]).as_posix()
    shared_url = "../" + shared_rel
    mode_pages = {f"{mode}/index.html": mode for mode in MODES}
    edits: dict[str, dict[str, Any]] = {}

    staging = Path(tempfile.mkdtemp(prefix=f".{out.name}.staging-", dir=out.parent))
    try:
        for rel in source_files:
            if rel in removed:
                continue
            target = staging / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            if rel in mode_pages:
                mode = mode_pages[rel]
                rewritten, edits[mode] = _rewrite((source / rel).read_bytes(), mode, shared_url)
                target.write_bytes(rewritten)
            else:
                shutil.copyfile(source / rel, target)
        (staging / SHARED_DIR).mkdir(parents=True)
        shutil.copyfile(source / wasms[0]["source_path"], staging / shared_rel)
        marker = {
            "kind": "few-moves-derived-web-package",
            "derived": True,
            "not_a_unity_build": True,
            "not_an_ait": True,
            "upload_candidate": False,
            "transform": "shared-identical-wasm",
            "shared_wasm": {"path": shared_rel, "sha256": wasm["sha256"], "bytes": wasm["bytes"]},
            "note": "Per-mode unity-build.provenance.json files are copied unchanged and describe the original builds, not this package.",
        }
        (staging / DERIVED_MARKER).write_text(json.dumps(marker, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        if os.path.lexists(out):
            raise PackageError(f"output appeared during preparation: {out}")
        os.rename(staging, out)
    except BaseException:
        shutil.rmtree(staging, ignore_errors=True)
        raise

    written: Path | None = None
    try:
        verified = _verify(source, out, wasm, source_files, removed, edits)
        if _scan(source, "source") != source_files:
            raise PackageError("source tree changed during preparation")
        before = sum(entry["bytes"] for entry in source_files.values())
        after = sum(entry["bytes"] for entry in verified["files"].values())
        receipt = {
            "schema_version": 1,
            "kind": "few-moves-derived-web-package-receipt",
            "derived": True,
            "not_a_unity_build": True,
            "not_an_ait": True,
            "upload_candidate": False,
            "size_cap_claim": None,
            "source_root": str(source),
            "output_root": str(out),
            "receipt_path": str(receipt_path) if receipt_path is not None else None,
            "shared_wasm": {"path": shared_rel, "sha256": wasm["sha256"], "bytes": wasm["bytes"], "replaced": sorted(removed)},
            "totals": {"source_bytes": before, "output_bytes": after, "saved_bytes": before - after, "source_files": len(source_files), "output_files": len(verified["files"])},
            "code_url_resolution": verified["code_url_resolution"],
            "source_unchanged": True,
            "source_files": source_files,
            "output_files": verified["files"],
        }
        if receipt_path is not None:
            with open(receipt_path, "x", encoding="utf-8") as handle:
                written = receipt_path
                handle.write(json.dumps(receipt, indent=2, sort_keys=True) + "\n")
        # Re-check after the receipt write: neither tree may gain a file outside its hash list.
        if _scan(source, "source") != source_files:
            raise PackageError("source tree changed after writing the receipt")
        if _scan(out, "output") != verified["files"]:
            raise PackageError("output tree changed after writing the receipt")
        return receipt
    except BaseException:
        if written is not None:
            written.unlink(missing_ok=True)
        shutil.rmtree(out, ignore_errors=True)
        raise


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="existing web root with solo/, coop/, raid/")
    parser.add_argument("--out", required=True, help="new output directory; must not exist")
    parser.add_argument("--receipt", help="optional receipt JSON path; must not exist")
    args = parser.parse_args(argv)
    try:
        receipt = prepare(Path(args.source), Path(args.out), Path(args.receipt) if args.receipt else None)
        summary = {key: receipt[key] for key in ("kind", "output_root", "receipt_path", "shared_wasm", "totals", "code_url_resolution", "upload_candidate")}
        print(json.dumps(summary, indent=2, sort_keys=True))
        return 0
    except (PackageError, OSError, UnicodeDecodeError) as exc:
        print(json.dumps({"status": "REJECTED", "error": str(exc)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
