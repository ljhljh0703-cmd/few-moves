from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tools" / "prepare_web_release.py"
WASM = "37819947a037eb02a18ce51e501aea5a.wasm"
PAGES = {
    "solo": 'var buildUrl = "Build"; var config = { dataUrl: buildUrl + "/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.data", codeUrl: buildUrl + "/%s" };' % WASM,
    "coop": 'createUnityInstance(canvas,{dataUrl:"Build/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.data",codeUrl:"Build/%s"});' % WASM,
    "raid": 'createUnityInstance(canvas,{dataUrl:"Build/cccccccccccccccccccccccccccccccc.data",codeUrl:"Build/%s"});' % WASM,
}


class PrepareWebReleaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        # realpath: macOS temp dirs sit under the /var symlink, which the tool now rejects.
        self.base = Path(os.path.realpath(self.temp.name))
        self.source = self.base / "source"
        self.out = self.base / "out"
        self.write("index.html", "<html>home</html>\n")
        for mode, script in PAGES.items():
            self.write(f"{mode}/index.html", f"<html><script>{script}</script></html>\n")
            self.write(f"{mode}/Build/{WASM}", "same-wasm-bytes")
            data = {"solo": "a", "coop": "b", "raid": "c"}[mode] * 32
            self.write(f"{mode}/Build/{data}.data", f"data-{mode}")
            self.write(f"{mode}/unity-build.provenance.json", '{"mode":"%s"}\n' % mode)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write(self, rel: str, text: str) -> None:
        path = self.source / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def snapshot(self) -> dict[str, bytes]:
        return {p.relative_to(self.source).as_posix(): p.read_bytes() for p in sorted(self.source.rglob("*")) if p.is_file()}

    def run_tool(self, *extra: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--source", str(self.source), "--out", str(self.out), *extra],
            check=False, capture_output=True, text=True,
        )

    def assert_rejected(self, fragment: str) -> None:
        before = self.snapshot()
        result = self.run_tool()
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn(fragment, result.stderr)
        self.assertFalse(self.out.exists())
        self.assertEqual(self.snapshot(), before)
        self.assertEqual([p.name for p in self.base.iterdir() if "staging" in p.name], [])

    def test_shares_wasm_resolves_urls_and_preserves_source(self) -> None:
        before = self.snapshot()
        receipt_path = self.base / "receipt.json"
        result = self.run_tool("--receipt", str(receipt_path))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.snapshot(), before)
        shared = self.out / "shared" / "Build" / WASM
        self.assertEqual(shared.read_text(), "same-wasm-bytes")
        for mode in PAGES:
            self.assertFalse((self.out / mode / "Build" / WASM).exists())
            html = (self.out / mode / "index.html").read_bytes()
            original = before[f"{mode}/index.html"]
            old = f'buildUrl + "/{WASM}"' if mode == "solo" else f'"Build/{WASM}"'
            self.assertEqual(html, original.replace(old.encode(), f'"../shared/Build/{WASM}"'.encode()))
            self.assertEqual((self.out / mode / "Build" / WASM).parent.joinpath("..", "..", "shared", "Build", WASM).resolve(), shared.resolve())
            self.assertEqual((self.out / mode / "unity-build.provenance.json").read_bytes(), before[f"{mode}/unity-build.provenance.json"])
        receipt = json.loads(receipt_path.read_text())
        self.assertTrue(receipt["derived"])
        self.assertFalse(receipt["upload_candidate"])
        self.assertIsNone(receipt["size_cap_claim"])
        on_disk = sum(p.stat().st_size for p in self.out.rglob("*") if p.is_file())
        self.assertEqual(receipt["totals"]["output_bytes"], on_disk)
        self.assertEqual(receipt["totals"]["source_bytes"], sum(len(v) for v in before.values()))
        self.assertEqual({entry["resolved"] for entry in receipt["code_url_resolution"]}, {f"shared/Build/{WASM}"})
        self.assertEqual(os.stat(shared).st_nlink, 1)

    def test_rejects_existing_output_without_touching_it(self) -> None:
        self.out.mkdir()
        (self.out / "keep.txt").write_text("keep")
        result = self.run_tool()
        self.assertEqual(result.returncode, 2)
        self.assertIn("output already exists", result.stderr)
        self.assertEqual([p.name for p in self.out.iterdir()], ["keep.txt"])

    def test_rejects_existing_receipt(self) -> None:
        receipt_path = self.base / "receipt.json"
        receipt_path.write_text("old")
        result = self.run_tool("--receipt", str(receipt_path))
        self.assertEqual(result.returncode, 2)
        self.assertFalse(self.out.exists())
        self.assertEqual(receipt_path.read_text(), "old")

    def test_rejects_one_different_wasm(self) -> None:
        self.write(f"raid/Build/{WASM}", "different-bytes")
        self.assert_rejected("not byte-identical")

    def test_rejects_symlinked_wasm(self) -> None:
        target = self.source / "raid" / "Build" / WASM
        target.unlink()
        outside = self.base / "outside.wasm"
        outside.write_text("same-wasm-bytes")
        target.symlink_to(outside)
        self.assert_rejected("symlink")

    def test_rejects_symlinked_directory(self) -> None:
        outside = self.base / "outside-dir"
        outside.mkdir()
        (self.source / "extra").symlink_to(outside, target_is_directory=True)
        self.assert_rejected("symlink")

    def test_rejects_hard_link_bypass(self) -> None:
        target = self.source / "coop" / "Build" / WASM
        target.unlink()
        os.link(self.source / "solo" / "Build" / WASM, target)
        self.assert_rejected("hard-linked")

    def test_rejects_missing_wasm_target(self) -> None:
        (self.source / "coop" / "Build" / WASM).unlink()
        self.assert_rejected("codeUrl target is missing")

    def test_rejects_code_url_count_mismatch(self) -> None:
        self.write("raid/index.html", f'<script>a={{codeUrl:"Build/{WASM}"}};b={{codeUrl:"Build/{WASM}"}}</script>\n')
        self.assert_rejected("exactly one codeUrl, found 2")

    def test_rejects_code_url_path_escape(self) -> None:
        self.write("solo/index.html", f'<script>x={{codeUrl:"../coop/Build/{WASM}"}}</script>\n')
        self.assert_rejected("not Build/<name>.wasm")

    def assert_receipt_rejected(self, receipt: Path, fragment: str, out: Path | None = None) -> None:
        before = self.snapshot()
        out = out or self.out
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--source", str(self.source), "--out", str(out), "--receipt", str(receipt)],
            check=False, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn(fragment, result.stderr)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(os.path.lexists(out))
        self.assertFalse(os.path.lexists(receipt))
        self.assertEqual([p.name for p in self.base.iterdir() if "staging" in p.name], [])

    def test_rejects_receipt_inside_source(self) -> None:
        self.assert_receipt_rejected(self.source / "receipt.json", "inside the source")
        self.assert_receipt_rejected(self.source / "solo" / "Build" / "receipt.json", "inside the source")

    def test_rejects_receipt_inside_source_via_symlinked_parent(self) -> None:
        alias = self.base / "alias"
        alias.symlink_to(self.source, target_is_directory=True)
        self.assert_receipt_rejected(alias / "receipt.json", "symlink component")
        hop = self.base / "hop"
        hop.symlink_to(self.base, target_is_directory=True)
        self.assert_receipt_rejected(hop / "source" / "coop" / "receipt.json", "symlink component")

    def test_rejects_receipt_inside_source_via_case_alias(self) -> None:
        upper = self.base / "SOURCE"
        if not upper.exists():
            self.skipTest("case-sensitive filesystem")
        self.assert_receipt_rejected(upper / "receipt.json", "inside the source")

    def test_rejects_receipt_inside_or_equal_to_output(self) -> None:
        self.assert_receipt_rejected(self.out / "receipt.json", "parent must be an existing directory")
        self.assert_receipt_rejected(self.out, "must not be the output path")

    def test_rejects_output_inside_source_via_symlinked_ancestor(self) -> None:
        hop = self.base / "hop"
        hop.symlink_to(self.base, target_is_directory=True)
        before = self.snapshot()
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--source", str(self.source), "--out", str(hop / "source" / "solo" / "pkg")],
            check=False, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn("symlink component", result.stderr)
        self.assertEqual(self.snapshot(), before)

    # Independent review fixture output_ancestor_symlink: alias -> unrelated real dir, --out alias/nested/out.
    def test_rejects_output_ancestor_symlink_outside_source(self) -> None:
        real = self.base / "real"
        (real / "nested").mkdir(parents=True)
        alias = self.base / "alias"
        alias.symlink_to(real, target_is_directory=True)
        self.out = alias / "nested" / "out"
        self.assert_rejected("symlink component")
        self.assertEqual(list((real / "nested").iterdir()), [])

    # Independent review fixture receipt_ancestor_symlink: --receipt alias/receipt.json.
    def test_rejects_receipt_ancestor_symlink_outside_source(self) -> None:
        real = self.base / "real"
        real.mkdir()
        alias = self.base / "alias"
        alias.symlink_to(real, target_is_directory=True)
        self.assert_receipt_rejected(alias / "receipt.json", "symlink component")
        self.assertEqual(list(real.iterdir()), [])

    # Independent review fixture buildurl_path_escape: buildUrl="../outside" while mode/Build/<wasm> exists.
    def test_rejects_build_url_path_escape(self) -> None:
        self.write("solo/index.html", f'<script>var buildUrl = "../outside"; x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
        self.write(f"outside/{WASM}", "same-wasm-bytes")
        self.assert_rejected('assigned exactly once as \\"Build\\"')

    def test_rejects_ambiguous_build_url(self) -> None:
        self.write("solo/index.html", f'<script>var buildUrl = "Build"; buildUrl = "../x"; x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
        self.assert_rejected("assigned exactly once")
        self.write("solo/index.html", f'<script>x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
        self.assert_rejected("assigned exactly once")

    # Independent r3 delta fixture: the first quoted token is "Build" but the full RHS escapes the mode.
    def test_rejects_build_url_trailing_concatenation(self) -> None:
        self.write(f"outside/{WASM}", "same-wasm-bytes")
        self.write("solo/index.html", f'<script>var buildUrl = "Build" + "/../../outside"; x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
        self.assert_rejected("assigned exactly once")

    def test_rejects_build_url_non_literal_rhs(self) -> None:
        for rhs in (
            '"Build" || "../x";',
            '"Build" ? "Build" : "../x";',
            '"Build" /* c */;',
            '"Build"',
            '"Build"\n+ "/../x";',
            "'Build';",
            '`Build`;',
        ):
            with self.subTest(rhs=rhs):
                self.write("solo/index.html", f'<script>var buildUrl = {rhs} x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
                self.assert_rejected("assigned exactly once")
        self.write("solo/index.html", f'<script>var buildUrl = "Build"; buildUrl += "/../x"; x={{codeUrl: buildUrl + "/{WASM}"}}</script>\n')
        self.assert_rejected("assigned exactly once")

    # The standard Unity template statement stays accepted; a comparison is not an assignment.
    def test_accepts_standard_unity_build_url_statement(self) -> None:
        page = (f'<script>\n    var buildUrl = "Build";\n    if (buildUrl === "Build") {{}}\n'
                f'    var config = {{ codeUrl: buildUrl + "/{WASM}" }};\n</script>\n')
        self.write("solo/index.html", page)
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.out / "solo" / "index.html").read_text(),
                         page.replace(f'buildUrl + "/{WASM}"', f'"../shared/Build/{WASM}"'))

    # Independent review fixture crlf_outside_codeurl: CRLF and all other bytes survive; only the value span changes.
    def test_preserves_crlf_and_bytes_outside_code_url(self) -> None:
        page = ('<html>\r\n<script>\r\nvar buildUrl = "Build";\r\nvar config = {\r\n  codeUrl :  buildUrl + "/%s",\r\n'
                '  dataUrl: buildUrl + "/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.data"\r\n};\r\n</script>\r\n\u00e9\r\n</html>\r\n') % WASM
        (self.source / "solo" / "index.html").write_bytes(page.encode("utf-8"))
        coop = (self.source / "coop" / "index.html").read_bytes().replace(b"\n", b"\r\n")
        (self.source / "coop" / "index.html").write_bytes(coop)
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        solo_out = (self.out / "solo" / "index.html").read_bytes()
        expected = page.replace(f'buildUrl + "/{WASM}"', f'"../shared/Build/{WASM}"').encode("utf-8")
        self.assertEqual(solo_out, expected)
        self.assertEqual(solo_out.count(b"\r\n"), page.count("\r\n"))
        coop_out = (self.out / "coop" / "index.html").read_bytes()
        self.assertEqual(coop_out, coop.replace(f'"Build/{WASM}"'.encode(), f'"../shared/Build/{WASM}"'.encode()))
        self.assertEqual(coop_out.count(b"\r\n"), coop.count(b"\r\n"))

    def test_rejects_output_inside_source(self) -> None:
        before = self.snapshot()
        result = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--source", str(self.source), "--out", str(self.source / "pkg")],
            check=False, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn("must not contain each other", result.stderr)
        self.assertEqual(self.snapshot(), before)


if __name__ == "__main__":
    unittest.main()
