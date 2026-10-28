#!/usr/bin/env python3
"""Derive a release web root from a verified Few Moves web root using a release profile.

The solo-first profile keeps solo and raid, drops the coop mode, its manifests, and the 멀티 menu entry
on the home page. Every kept file is byte-identical to the source except index.html, which loses exactly
one <a class="menu-bar" href="coop/index.html">…</a> block. With --onestore, the reviewed ONE store
bridge (tools/prepare_onestore_web.py `_inject`, web/platforms/onestore/fewmoves-onestore.js) is added
to the kept pages. Optionally writes a deterministic zip. The source root is never modified.

This is a packaging step, not a Unity build, an upload, or a hosting claim.
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
import zipfile
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from prepare_web_release import PackageError, _check_paths, _scan, _sha256  # noqa: E402
from prepare_onestore_web import BRIDGE_REL, BRIDGE_SOURCE, SDK_URL_RE, _inject  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "release-manifest.json"
HREF_RE = re.compile(rb'(?:href|src)="([^"#?]+)"')
ZIP_DATE = (1980, 1, 1, 0, 0, 0)


def _load_profile(path: Path) -> dict[str, Any]:
    profile = json.loads(path.read_text(encoding="utf-8"))
    for key in ("id", "include_modes", "exclude_modes", "copy_root_files", "copy_dirs", "portal", "public_text_forbidden"):
        if key not in profile:
            raise PackageError(f"profile missing {key}")
    return profile


def _remove_menu_block(html: bytes, href: str) -> bytes:
    pattern = re.compile(rb'\n\n[ \t]*<a class="menu-bar" href="' + re.escape(href.encode()) + rb'">.*?</a>', re.S)
    matches = list(pattern.finditer(html))
    if len(matches) != 1:
        raise PackageError(f"portal must contain exactly one menu block for {href}, found {len(matches)}")
    block = matches[0].group(0)
    if block.count(b"<a ") != 1:
        raise PackageError("menu block for removal spans more than one link")
    return html[: matches[0].start()] + html[matches[0].end():]


def _selected(source_files: dict[str, Any], profile: dict[str, Any]) -> list[str]:
    keep = []
    for rel in source_files:
        top = rel.split("/", 1)[0]
        if "/" not in rel:
            if rel in profile["copy_root_files"]:
                keep.append(rel)
            elif rel not in profile.get("drop_root_files", []):
                raise PackageError(f"unclassified root file: {rel}")
        elif top in profile["copy_dirs"]:
            keep.append(rel)
        elif top not in profile["exclude_modes"]:
            raise PackageError(f"unclassified directory: {top}/")
    for name in profile["copy_root_files"]:
        if name not in source_files:
            raise PackageError(f"missing root file: {name}")
    for mode in profile["include_modes"]:
        if f"{mode}/index.html" not in source_files:
            raise PackageError(f"missing mode page: {mode}/index.html")
    return sorted(keep)


def _check_public(root: Path, profile: dict[str, Any]) -> dict[str, Any]:
    """Text files carry no excluded-mode wording; compiled residuals match the profile exactly."""
    suffixes = tuple(profile.get("public_text_suffixes", [".html", ".css", ".json"]))
    allowed = profile.get("compiled_residuals", {}).get("allowed", {})
    findings: list[str] = []
    residuals: dict[str, dict[str, int]] = {}
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(root).as_posix()
        data = path.read_bytes()
        if rel.endswith(suffixes):
            for needle in profile["public_text_forbidden"]:
                if needle.encode() in data:
                    findings.append(f"{rel}: {needle}")
        count = data.count(b"/api/coop")
        if count:
            name = "Build/" + path.name if "/Build/" in "/" + rel else rel
            if allowed.get(name, {}).get("/api/coop/v1") != count:
                findings.append(f"{rel}: unexpected /api/coop x{count}")
            residuals[rel] = count  # occurrences of the compiled co-op API base path
    if findings:
        raise PackageError("public content check failed: " + "; ".join(findings))
    return residuals


def _check_links(root: Path, pages: list[str]) -> None:
    for rel in pages:
        html = (root / rel).read_bytes()
        base = (root / rel).parent
        for match in HREF_RE.finditer(html):
            target = match.group(1).decode()
            if target.startswith(("http:", "https:", "data:", "mailto:", "//")):
                continue
            if not (base / target).resolve().is_file():
                raise PackageError(f"{rel} links to missing {target}")
        for ref in re.findall(rb'(?:"|/)((?:\.\./shared/)?Build/[0-9a-f]{32}\.[a-z.]+)"', html):
            if not (base / ref.decode()).resolve().is_file():
                raise PackageError(f"{rel} references missing {ref.decode()}")


def _write_zip(root: Path, zip_path: Path) -> dict[str, Any]:
    with zipfile.ZipFile(zip_path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(p for p in root.rglob("*") if p.is_file()):
            info = zipfile.ZipInfo(path.relative_to(root).as_posix(), date_time=ZIP_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, path.read_bytes())
    return {"path": str(zip_path), "sha256": _sha256(zip_path), "bytes": zip_path.stat().st_size}


TOSS_INIT_REL = "platform/toss-init.js"
TOSS_TAG = b'<script type="module" src="platform/toss-init.js"></script>'


def _inject_toss(html: bytes) -> bytes:
    """One module script right after <meta charset> on the home page (the .ait entry)."""
    if b"toss-init.js" in html:
        raise PackageError("index.html already references toss-init.js")
    match = re.search(rb"<head>\r?\n([ \t]*)<meta charset=\"utf-8\">\r?\n", html)
    if not match or html.count(b"<head>") != 1:
        raise PackageError("index.html must have one <head> followed by <meta charset=\"utf-8\">")
    return html[: match.end()] + match.group(1) + TOSS_TAG + b"\n" + html[match.end():]


AIT_BRIDGE_REL = "platform/ait-unity-bridge.js"


def _inject_ait_bridge(html: bytes, rel: str) -> bytes:
    """The SDK unity-bridge module (window.AppsInToss) first in a game page's <head>, as AITTemplate loads it."""
    if b"ait-unity-bridge.js" in html:
        raise PackageError(f"{rel} already references ait-unity-bridge.js")
    match = re.search(rb"<head>\r?\n([ \t]*)<meta charset=\"utf-8\">\r?\n", html)
    if not match or html.count(b"<head>") != 1:
        raise PackageError(f"{rel} must have one <head> followed by <meta charset=\"utf-8\">")
    if b"<script" in html[: match.end()].lower():
        raise PackageError(f"{rel} has a script before the injection point")
    prefix = b"" if "/" not in rel else b"../"
    tag = b'<script type="module" src="' + prefix + AIT_BRIDGE_REL.encode() + b'"></script>'
    return html[: match.end()] + match.group(1) + tag + b"\n" + html[match.end():]


TOSS_UI_FILES = ("toss-ui.css", "toss-safearea.js")


def _inject_toss_ui(html: bytes, rel: str) -> bytes:
    """Toss-only inset stylesheet and SafeArea module, last in <head> so they follow the page's own styles."""
    if b"toss-ui.css" in html or b"toss-safearea.js" in html:
        raise PackageError(f"{rel} already references the Toss UI files")
    if html.count(b"\n</head>") != 1:
        raise PackageError(f"{rel} must have exactly one </head> at the start of a line")
    prefix = b"" if "/" not in rel else b"../"
    tags = (b'  <link rel="stylesheet" href="' + prefix + b'platform/toss-ui.css">\n'
            b'  <script type="module" src="' + prefix + b'platform/toss-safearea.js"></script>\n')
    index = html.index(b"\n</head>") + 1
    return html[:index] + tags + html[index:]


def prepare(source: Path, out: Path, profile_path: Path, onestore: bool = False, zip_path: Path | None = None,
            receipt_path: Path | None = None, toss_init: Path | None = None, ait_bridge: Path | None = None,
            toss_ui: Path | None = None) -> dict[str, Any]:
    source = Path(os.path.abspath(source))
    out = Path(os.path.abspath(out))
    _check_paths(source, out, Path(os.path.abspath(receipt_path)) if receipt_path else None)
    if zip_path is not None and os.path.lexists(zip_path):
        raise PackageError(f"zip already exists: {zip_path}")
    if sum(x for x in (onestore, toss_init is not None, ait_bridge is not None)) > 1:
        raise PackageError("--onestore, --toss-init and --ait-bridge are separate targets")
    if ait_bridge is not None and not Path(ait_bridge).is_file():
        raise PackageError(f"AIT bridge module not found: {ait_bridge}")
    if toss_init is not None and not Path(toss_init).is_file():
        raise PackageError(f"toss init module not found: {toss_init}")
    if toss_ui is not None:
        if onestore or toss_init is not None:
            raise PackageError("--toss-ui goes only with --ait-bridge (or alone for a QA fixture)")
        missing = [name for name in TOSS_UI_FILES if not (Path(toss_ui) / name).is_file()]
        if missing:
            raise PackageError(f"toss UI files not found: {missing}")
    profile = _load_profile(profile_path)
    source_files = _scan(source, "source")
    keep = _selected(source_files, profile)
    portal_rel = profile["portal"]["path"]
    pages = [portal_rel] + [f"{mode}/index.html" for mode in profile["include_modes"]]
    bridge = None
    if onestore:
        sdk = SDK_URL_RE.search(BRIDGE_SOURCE.read_text(encoding="utf-8"))
        if not sdk:
            raise PackageError("bridge SDK_URL is not pinned")
        bridge = {"path": BRIDGE_REL, "sha256": _sha256(BRIDGE_SOURCE), "sdk_url": sdk.group("url")}

    edits: dict[str, Any] = {}
    staging = Path(tempfile.mkdtemp(prefix=f".{out.name}.staging-", dir=out.parent))
    try:
        for rel in keep:
            target = staging / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            data = (source / rel).read_bytes()
            changes = []
            if rel == portal_rel:
                data = _remove_menu_block(data, profile["portal"]["remove_menu_href"])
                changes.append("removed excluded-mode menu block")
            if toss_init is not None and rel == portal_rel:
                data = _inject_toss(data)
                changes.append("Toss init module script (home page only)")
            if ait_bridge is not None and rel in pages and (rel != portal_rel or toss_ui is not None):
                data = _inject_ait_bridge(data, rel)
                changes.append("AIT unity-bridge module script")
            if toss_ui is not None and rel in pages:
                data = _inject_toss_ui(data, rel)
                changes.append("Toss UI inset stylesheet and SafeArea module")
            if onestore and rel in pages:
                data, edit = _inject(data, rel, "" if "/" not in rel else "../")
                changes.append("ONE bridge script at offset %d" % edit["offset"])
            if changes:
                target.write_bytes(data)
                edits[rel] = {"source_sha256": source_files[rel]["sha256"], "output_sha256": hashlib.sha256(data).hexdigest(), "changes": changes}
            else:
                shutil.copy2(source / rel, target)
        if onestore:
            (staging / "platform").mkdir()
            shutil.copy2(BRIDGE_SOURCE, staging / BRIDGE_REL)
        if toss_init is not None:
            (staging / "platform").mkdir()
            shutil.copy2(toss_init, staging / TOSS_INIT_REL)
        if ait_bridge is not None:
            (staging / "platform").mkdir()
            shutil.copy2(ait_bridge, staging / AIT_BRIDGE_REL)
        if toss_ui is not None:
            (staging / "platform").mkdir(exist_ok=True)
            for name in TOSS_UI_FILES:
                shutil.copy2(Path(toss_ui) / name, staging / "platform" / name)
        out_files = _scan(staging, "output")
        for rel in keep:
            if rel not in edits and out_files[rel]["sha256"] != source_files[rel]["sha256"]:
                raise PackageError(f"{rel} changed during copy")
        _check_links(staging, pages)
        residuals = _check_public(staging, profile)
        manifest = {
            "state": "provisional",
            "gate": "gate_pending",
            "kind": "few-moves-release-web" + ("-onestore" if onestore else "") + ("-toss" if toss_init is not None else "") + ("-ait-native" if ait_bridge is not None else ""),
            "profile": {"id": profile["id"], "version": profile.get("version"), "sha256": _sha256(profile_path)},
            "modes": profile["include_modes"],
            "source_root_sha256": hashlib.sha256(json.dumps({k: v["sha256"] for k, v in sorted(source_files.items())}).encode()).hexdigest(),
            "edits": edits,
            "onestore_bridge": bridge,
            "toss_init": {"path": TOSS_INIT_REL, "sha256": _sha256(Path(toss_init)), "pages": [portal_rel]} if toss_init is not None else None,
            "ait_bridge": {"path": AIT_BRIDGE_REL, "sha256": _sha256(Path(ait_bridge)),
                           "pages": [p for p in pages if p != portal_rel or toss_ui is not None]} if ait_bridge is not None else None,
            "compiled_coop_api_path_occurrences": residuals,
            "not_an_upload": True,
            "files": {rel: info["sha256"] for rel, info in sorted(out_files.items())},
        }
        if toss_ui is not None:  # key only when used, so earlier manifests reproduce byte for byte
            manifest["kind"] += "-toss-ui"
            manifest["toss_ui"] = {"files": {f"platform/{name}": _sha256(Path(toss_ui) / name) for name in TOSS_UI_FILES}, "pages": pages}
        (staging / MANIFEST).write_text(json.dumps(manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")
        _check_public(staging, profile)
        os.rename(staging, out)
    except BaseException:
        shutil.rmtree(staging, ignore_errors=True)
        raise
    if _scan(source, "source") != source_files:
        raise PackageError("source changed while packaging")
    final = _scan(out, "output")
    receipt: dict[str, Any] = {"ok": True, "source": str(source), "out": str(out), "profile": profile["id"],
                               "onestore": onestore, "files": len(final), "bytes": sum(i["bytes"] for i in final.values()),
                               "manifest_sha256": final[MANIFEST]["sha256"], "edited": sorted(edits)}
    if zip_path is not None:
        receipt["zip"] = _write_zip(out, Path(os.path.abspath(zip_path)))
    if receipt_path is not None:
        with Path(receipt_path).open("x", encoding="utf-8") as handle:
            json.dump(receipt, handle, indent=2, sort_keys=True)
            handle.write("\n")
    return receipt


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--profile", default=str(ROOT / "web" / "release-profiles" / "solo-first" / "profile.json"))
    parser.add_argument("--onestore", action="store_true", help="also inject the reviewed ONE store bridge")
    parser.add_argument("--toss-init", help="built Toss init module to add to the home page (.ait target)")
    parser.add_argument("--ait-bridge", help="built SDK unity-bridge module (window.AppsInToss) for native APPSINTOS_OPTIMIZED game pages")
    parser.add_argument("--toss-ui", help="directory with toss-ui.css and toss-safearea.js (Apps in Toss inset layout; with --ait-bridge, the home page also gets the bridge)")
    parser.add_argument("--zip", help="optional deterministic zip path; must not exist")
    parser.add_argument("--receipt", help="optional receipt JSON path; must not exist")
    args = parser.parse_args(argv)
    try:
        receipt = prepare(Path(args.source), Path(args.out), Path(args.profile), args.onestore,
                          Path(args.zip) if args.zip else None, Path(args.receipt) if args.receipt else None,
                          Path(args.toss_init) if args.toss_init else None,
                          Path(args.ait_bridge) if args.ait_bridge else None,
                          Path(args.toss_ui) if args.toss_ui else None)
    except PackageError as error:
        print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 2
    print(json.dumps(receipt, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
