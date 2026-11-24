import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from playwright.sync_api import expect, sync_playwright


class HtmlReportBrowserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.fixture.cleanup)
        directory = Path(cls.fixture.name)
        env = os.environ.copy()
        env["DREDGE_HTML_BROWSER_FIXTURE_DIR"] = str(directory)
        subprocess.run(
            [
                "dotnet", "test", "--project", str(Path("Valleysoft.Dredge.Tests") / "Valleysoft.Dredge.Tests.csproj"),
                "--no-restore", "-c", "Release", "--filter-method",
                "Valleysoft.Dredge.Tests.HtmlImageReportTests.BrowserFixtureMatchesExplorerWholeFilesystem",
            ],
            cwd=Path(__file__).resolve().parent.parent,
            env=env,
            check=True,
        )
        cls.report = (directory / "report.html").as_uri()
        cls.expected = json.loads((directory / "expected.json").read_text(encoding="utf-8"))
        cls.playwright = sync_playwright().start()
        cls.addClassCleanup(cls.playwright.stop)
        executable = os.environ.get("DREDGE_BROWSER_EXECUTABLE")
        cls.browser = cls.playwright.chromium.launch(headless=True, executable_path=executable)
        cls.addClassCleanup(cls.browser.close)

    def setUp(self):
        self.page = self.browser.new_page(viewport={"width": 1280, "height": 900})
        self.errors = []
        self.page.on("pageerror", lambda error: self.errors.append(str(error)))
        self.page.goto(self.report)
        self.page.wait_for_load_state("networkidle")

    def tearDown(self):
        self.page.close()
        self.assertEqual([], self.errors)

    def test_comparison_unicode_search_count_and_swap(self):
        self.page.get_by_role("button", name="Differences", exact=True).click()
        self.page.locator("#filter").fill("\u03c3-unicodeprobe")
        expect(self.page.locator(".search-count")).to_have_text("1 match")
        expect(self.page.locator("#right .controls.silt")).to_contain_text("1 match")
        row = self.page.locator(".comparison-search.has-sizes")
        expect(row).to_have_count(1)
        expect(row.locator(".name")).to_contain_text("\u03c2-unicodeprobe")
        before = row.locator(":scope > span").all_text_contents()
        row.click()
        expect(self.page.locator("#right .info")).to_contain_text("/app/\u03c2-unicodeprobe")
        self.page.get_by_role("button", name="Swap sides").click()
        after = self.page.locator(".comparison-search.has-sizes > span").all_text_contents()
        self.assertEqual((before[1], before[3]), (after[3], after[1]))
        self.page.locator("#filter").fill("\u03c3-unicodepkg")
        expect(self.page.locator(".search-count")).to_have_text("1 match")
        expect(self.page.locator(".package-diff-row")).to_have_count(1)
        expect(self.page.locator(".package-diff-row .name")).to_contain_text("\u03c2-unicodepkg")

    def test_whole_filesystem_matches_tui_tree(self):
        self.page.get_by_role("button", name="base", exact=True).click()
        self.page.locator("#layer-1").click()
        self.page.get_by_role("button", name="Whole filesystem").click()
        expect(self.page.locator("#right .controls .push")).to_have_text(self.expected["Summary"])
        folder = self.page.locator('.trow[data-path="app"]')
        expect(folder.locator(".file-size")).to_have_text(self.expected["FolderSize"])
        expect(folder.locator(".name")).to_contain_text(f'{self.expected["FileCount"]} files')
        expect(folder.locator(".name")).to_contain_text(self.expected["Sharing"])
        expect(folder.locator(".tree-note")).to_contain_text(self.expected["Note"])
        folder.locator(".tree-toggle").click()
        expect(self.page.locator('.trow[data-path^="app/"]')).to_have_count(len(self.expected["Entries"]))
        for item in self.expected["Entries"]:
            row = self.page.locator(f'.trow[data-path="{item["Path"]}"]')
            expect(row).to_have_count(1)
            changes = set(row.get_attribute("class").split()) & {"Added", "Modified", "Identical", "Deleted"}
            self.assertEqual(changes, set() if item["Change"] == "None" else {item["Change"]})
            expect(row.locator(".file-size")).to_have_text(item["Size"])
            if item["Note"]:
                expect(row.locator(".tree-note")).to_contain_text(item["Note"])
        self.page.locator('.trow[data-path="app/link"]').click()
        expect(self.page.locator("#right .info")).to_contain_text("/app/link")

    def test_comparison_layout_has_gutter_at_desktop_and_mobile_widths(self):
        self.page.get_by_role("button", name="Differences", exact=True).click()
        for width in (320, 761, 1280):
            with self.subTest(width=width):
                self.page.set_viewport_size({"width": width, "height": 900})
                metrics = self.page.evaluate("""async () => {
                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    const pane = document.querySelector('#right-pane').getBoundingClientRect();
                    const summary = document.querySelector('.comparison-summary .push').getBoundingClientRect();
                    return { gap: pane.right - summary.right,
                      overflow: document.documentElement.scrollWidth > innerWidth };
                }""")
                self.assertGreaterEqual(metrics["gap"], 7)
                self.assertFalse(metrics["overflow"])
                expect(self.page.locator('.comparison-file-body .tree-toggle').first).to_be_visible()


if __name__ == "__main__":
    unittest.main()
