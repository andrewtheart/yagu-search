import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.analyze_coverage import CoverageRow, canonical_path, tracked_source_files, write_reports


class SourceInventoryTests(unittest.TestCase):
    def test_inventory_includes_pending_sources_without_staging_them(self):
        with tempfile.TemporaryDirectory(prefix="yagu-coverage-") as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet", str(root)], check=True, capture_output=True)
            (root / ".gitignore").write_text("ignored/\n", encoding="utf-8")
            paths = [
                "src/Tracked.cs",
                "src/Deleted.cs",
                "src/New.cs",
                "src/space name.cs",
                "scripts/new.ps1",
                "ignored/Generated.cs",
                "tests/Test.cs",
                "src/obj/Generated.cs",
                "src/bin/Generated.cs",
                "src/vendor/ThirdParty.cs",
                "src/Version.g.cs",
                "src/HELP.html",
                "README.md",
            ]
            for relative in paths:
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("", encoding="utf-8")
            subprocess.run(
                ["git", "-C", str(root), "add", "src/Tracked.cs", "src/Deleted.cs"],
                check=True,
                capture_output=True,
            )
            (root / "src/Deleted.cs").unlink()

            inventory = tracked_source_files(root)

            self.assertEqual(
                {
                    "src/tracked.cs": "src/Tracked.cs",
                    "src/new.cs": "src/New.cs",
                    "src/space name.cs": "src/space name.cs",
                    "scripts/new.ps1": "scripts/new.ps1",
                    "src/help.html": "src/HELP.html",
                },
                inventory,
            )
            self.assertEqual("src/New.cs", canonical_path(str(root / "src/New.cs"), root, inventory))
            staged = subprocess.run(
                ["git", "-C", str(root), "diff", "--cached", "--name-only"],
                check=True,
                capture_output=True,
                text=True,
            ).stdout.splitlines()
            self.assertEqual(["src/Deleted.cs", "src/Tracked.cs"], staged)

    def test_empty_repository_has_no_source_files(self):
        with tempfile.TemporaryDirectory(prefix="yagu-coverage-") as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet", str(root)], check=True, capture_output=True)
            self.assertEqual({}, tracked_source_files(root))

    def test_git_failures_are_reported_instead_of_an_empty_inventory(self):
        for failure in (OSError("git unavailable"), subprocess.CalledProcessError(128, "git")):
            with self.subTest(failure=type(failure).__name__):
                with patch("scripts.analyze_coverage.subprocess.run", side_effect=failure):
                    with self.assertRaisesRegex(RuntimeError, "Could not inventory tracked files"):
                        tracked_source_files(Path.cwd())


class LanguageTotalsTests(unittest.TestCase):
    def test_reports_separate_languages_and_preserve_combined_totals(self):
        rows = [
            CoverageRow("src/App.cs", "C#", "measured", 9, 10, 3, 4, 2, 2),
            CoverageRow("src/Other.cs", "C#", "measured", 1, 2, 0, 2, 0, 1),
            CoverageRow("src/core.rs", "Rust", "measured", 20, 20, 8, 8, 5, 5),
        ]
        with tempfile.TemporaryDirectory(prefix="yagu-coverage-") as directory:
            output = Path(directory)
            write_reports(output, rows, [], [], {}, {"authoritative": False})
            summary = json.loads((output / "coverage-summary.json").read_text(encoding="utf-8"))
            expected = {
                "C#": ((10, 12), (3, 6), (2, 3)),
                "Rust": ((20, 20), (8, 8), (5, 5)),
                "Combined measured": ((30, 32), (11, 14), (7, 8)),
            }
            for language, metrics in expected.items():
                total = summary["combined_measured"] if language == "Combined measured" else summary["totals_by_language"][language]
                for metric, (covered, count) in zip(("lines", "branches", "functions"), metrics):
                    with self.subTest(language=language, metric=metric):
                        self.assertEqual(covered, total[metric]["covered"])
                        self.assertEqual(count, total[metric]["total"])
            self.assertEqual(3, summary["measured_file_count"])
            self.assertEqual(1, summary["fully_covered_file_count"])
            self.assertFalse(summary["runner_status"]["authoritative"])
            markdown = (output / "coverage-summary.md").read_text(encoding="utf-8")
            self.assertIn("| C# | 2 | 83.33% (10/12)", markdown)
            self.assertIn("| Rust | 1 | 100.00% (20/20)", markdown)


if __name__ == "__main__":
    unittest.main()