from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools" / "prepare_onestore_web.py"
BRIDGE = ROOT / "web" / "platforms" / "onestore" / "fewmoves-onestore.js"
PAGE = '<!doctype html>\n<html lang="ko">\n<head>\n  <meta charset="utf-8">\n  <title>{title}</title>\n</head>\n<body><canvas id="unity-canvas"></canvas><script>var x=1;</script></body>\n</html>\n'


class PrepareOnestoreWebTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(os.path.realpath(self.temp.name))
        self.source = self.base / "source"
        self.out = self.base / "out"
        self.write("index.html", PAGE.format(title="Few Moves").replace('<canvas id="unity-canvas"></canvas>', ""))
        self.write("style.css", "body{}\n")
        for mode in ("solo", "coop", "raid"):
            self.write(f"{mode}/index.html", PAGE.format(title=mode))
            self.write(f"{mode}/Build/{mode * 3}.data", f"data-{mode}")
        self.write("shared/Build/abc.wasm", "wasm")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write(self, rel: str, text: str) -> None:
        path = self.source / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def run_tool(self, *extra: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-B", str(SCRIPT), "--source", str(self.source), "--out", str(self.out), *extra],
                              capture_output=True, text=True)

    def snapshot(self, root: Path) -> dict[str, bytes]:
        return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}

    def test_injects_one_first_script_per_page_and_changes_nothing_else(self) -> None:
        before = self.snapshot(self.source)
        receipt = self.base / "receipt.json"
        result = self.run_tool("--receipt", str(receipt))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.snapshot(self.source), before, "source is untouched")
        after = self.snapshot(self.out)
        self.assertEqual(set(after), set(before) | {"platform/fewmoves-onestore.js", "onestore-package.json"})
        self.assertEqual(after["platform/fewmoves-onestore.js"], BRIDGE.read_bytes())
        for rel, prefix in {"index.html": "", "solo/index.html": "../", "coop/index.html": "../", "raid/index.html": "../"}.items():
            tag = f'  <script src="{prefix}platform/fewmoves-onestore.js"></script>\n'.encode()
            original = before[rel]
            anchor = original.index(b'<meta charset="utf-8">\n') + len(b'<meta charset="utf-8">\n')
            self.assertEqual(after[rel], original[:anchor] + tag + original[anchor:], rel)
            self.assertLess(after[rel].index(b"fewmoves-onestore.js"), after[rel].index(b"<script>"), "bridge is the first script")
        for rel in before:
            if not rel.endswith("index.html"):
                self.assertEqual(after[rel], before[rel], rel)
        manifest = json.loads(after["onestore-package.json"])
        self.assertEqual(manifest["sdk_url"], "https://h5sdk.onestore.net/lib/v1.1.0/onestore-h5-sdk.min.js")
        self.assertTrue(manifest["not_an_upload"])
        self.assertTrue(json.loads(receipt.read_text())["ok"])

    def assert_rejected(self, fragment: str, *extra: str) -> None:
        result = self.run_tool(*extra)
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn(fragment, result.stderr)
        self.assertFalse(self.out.exists(), "no partial output on failure")
        self.assertEqual([p.name for p in self.base.iterdir() if p.name.startswith(".out.staging-")], [], "staging cleaned up")

    def test_missing_mode_page_is_rejected(self) -> None:
        (self.source / "raid" / "index.html").unlink()
        self.assert_rejected("missing required page: raid/index.html")

    def test_already_packaged_source_is_rejected(self) -> None:
        self.write("platform/fewmoves-onestore.js", "x")
        self.assert_rejected("already contains platform/")

    def test_double_injection_is_rejected(self) -> None:
        self.write("coop/index.html", PAGE.format(title="c").replace("<title>", '<script src="../platform/fewmoves-onestore.js"></script><title>'))
        self.assert_rejected("coop/index.html already references fewmoves-onestore")

    def test_csp_meta_is_rejected(self) -> None:
        self.write("solo/index.html", PAGE.format(title="s").replace("<title>", '<meta http-equiv="Content-Security-Policy" content="default-src \'self\'"><title>'))
        self.assert_rejected("solo/index.html already references")

    def test_script_before_injection_point_is_rejected(self) -> None:
        self.write("raid/index.html", PAGE.format(title="r").replace("<head>\n", "<script>early()</script><head>\n"))
        self.assert_rejected("raid/index.html has a script before the injection point")

    def test_unexpected_head_shape_is_rejected(self) -> None:
        self.write("index.html", PAGE.format(title="h").replace('  <meta charset="utf-8">\n', ""))
        self.assert_rejected("index.html must have exactly one <head>")

    def test_existing_output_or_receipt_is_rejected(self) -> None:
        self.out.mkdir()
        result = self.run_tool()
        self.assertEqual(result.returncode, 2)
        self.assertIn("output already exists", result.stderr)
        self.out.rmdir()
        receipt = self.base / "r.json"
        receipt.write_text("{}")
        self.assert_rejected("receipt already exists", "--receipt", str(receipt))


if __name__ == "__main__":
    unittest.main()
