from __future__ import annotations

import argparse
import csv
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


SOURCE_LANGUAGES = {
    ".cs": "C#",
    ".rs": "Rust",
    ".ps1": "PowerShell",
    ".py": "Python",
    ".js": "JavaScript",
    ".ts": "TypeScript",
    ".html": "HTML",
    ".css": "CSS",
    ".xaml": "XAML",
    ".iss": "Inno Setup",
    ".bat": "Batch",
    ".cmd": "Batch",
    ".sh": "Shell",
    ".csproj": "MSBuild",
    ".props": "MSBuild",
    ".targets": "MSBuild",
    ".sln": "Solution",
    ".slnx": "Solution",
    ".toml": "TOML",
    ".json": "JSON",
    ".xml": "XML",
    ".yml": "YAML",
    ".yaml": "YAML",
    ".runsettings": "Run settings",
    ".manifest": "Manifest",
    ".config": "Configuration",
}


@dataclass
class FunctionCoverage:
    file: str
    language: str
    name: str
    signature: str
    first_line: int | None
    line_hits: dict[int, int] = field(default_factory=dict)
    execution_count: int | None = None

    @property
    def covered(self) -> bool:
        if self.execution_count is not None:
            return self.execution_count > 0
        return any(hits > 0 for hits in self.line_hits.values())


@dataclass
class ManagedFileCoverage:
    path: str
    line_hits: dict[int, int] = field(default_factory=dict)
    branch_hits: dict[str, int] = field(default_factory=dict)
    functions: dict[str, FunctionCoverage] = field(default_factory=dict)


@dataclass
class CoverageRow:
    file: str
    language: str
    status: str
    lines_covered: int | None = None
    lines_total: int | None = None
    branches_covered: int | None = None
    branches_total: int | None = None
    functions_covered: int | None = None
    functions_total: int | None = None
    uncovered_lines: str = ""
    reason: str = ""

    @staticmethod
    def percent(covered: int | None, total: int | None) -> float | None:
        if covered is None or total is None:
            return None
        return 100.0 if total == 0 else covered * 100.0 / total

    def as_dict(self) -> dict[str, Any]:
        return {
            "file": self.file,
            "language": self.language,
            "status": self.status,
            "lines": metric_dict(self.lines_covered, self.lines_total),
            "branches": metric_dict(self.branches_covered, self.branches_total),
            "functions": metric_dict(self.functions_covered, self.functions_total),
            "uncovered_lines": self.uncovered_lines,
            "reason": self.reason,
        }


def metric_dict(covered: int | None, total: int | None) -> dict[str, Any] | None:
    percent = CoverageRow.percent(covered, total)
    if percent is None:
        return None
    return {"covered": covered, "total": total, "percent": round(percent, 4)}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Aggregate first-party Yagu line, branch, and function coverage."
    )
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--managed-json", type=Path, action="append", default=[])
    parser.add_argument("--managed-cobertura", type=Path, action="append", default=[])
    parser.add_argument("--rust-json", type=Path, action="append", default=[])
    parser.add_argument("--status-json", type=Path)
    return parser.parse_args()


def tracked_source_files(repo_root: Path) -> dict[str, str]:
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root), "ls-files", "-z"],
            check=True,
            capture_output=True,
        )
        tracked = result.stdout.decode("utf-8").split("\0")
    except (OSError, subprocess.CalledProcessError) as exc:
        raise RuntimeError(f"Could not inventory tracked files with git: {exc}") from exc

    inventory: dict[str, str] = {}
    for raw in tracked:
        if not raw:
            continue
        path = raw.replace("\\", "/")
        if is_excluded_source(path):
            continue
        if source_language(path) is not None:
            inventory[path.lower()] = path
    return inventory


def is_excluded_source(path: str) -> bool:
    lower = path.lower()
    parts = lower.split("/")
    if lower.startswith(("tests/", "testresults/", "src/vendor/", "plans/", "docs/")):
        return True
    if lower.startswith(("installer/output/", "installer/staging/", "installer/prerequisites/")):
        return True
    if "/tests/" in lower or any(part in {"bin", "obj"} for part in parts):
        return True
    if lower.endswith((".g.cs", ".generated.cs")):
        return True
    return lower == "src/yagu/help.html"


def source_language(path: str) -> str | None:
    lower = path.lower()
    if lower.endswith(".prompt.md"):
        return "Prompt"
    return SOURCE_LANGUAGES.get(Path(lower).suffix)


def canonical_path(raw: str, repo_root: Path, inventory: dict[str, str]) -> str | None:
    normalized = raw.replace("\\", "/").lstrip("./")
    root = repo_root.resolve().as_posix().rstrip("/")
    candidates = [normalized]
    if normalized.lower().startswith(root.lower() + "/"):
        candidates.append(normalized[len(root) + 1 :])
    for prefix in ("src/yagu/", "../yagu/", "yagu/"):
        if normalized.lower().startswith(prefix):
            candidates.append(normalized[len(prefix) :])
    for candidate in candidates:
        tracked = inventory.get(candidate.lower())
        if tracked is not None:
            return tracked
    return None


def add_hits(target: dict[int, int], source: dict[str, Any]) -> None:
    for line_text, hits_value in source.items():
        line = int(line_text)
        target[line] = target.get(line, 0) + int(hits_value)


def load_managed_json(
    path: Path,
    repo_root: Path,
    inventory: dict[str, str],
    files: dict[str, ManagedFileCoverage],
) -> None:
    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict):
        raise ValueError(f"Managed coverage JSON must contain an object: {path}")

    for documents in payload.values():
        if not isinstance(documents, dict):
            continue
        for raw_path, classes in documents.items():
            relative = canonical_path(raw_path, repo_root, inventory)
            if relative is None or source_language(relative) != "C#":
                continue
            file_coverage = files.setdefault(relative, ManagedFileCoverage(relative))
            for class_name, methods in classes.items():
                if not isinstance(methods, dict):
                    continue
                for method_signature, body in methods.items():
                    if not isinstance(body, dict):
                        continue
                    lines = body.get("Lines", {})
                    if not isinstance(lines, dict) or not lines:
                        continue
                    add_hits(file_coverage.line_hits, lines)
                    function_key = f"{class_name}|{method_signature}"
                    function = file_coverage.functions.get(function_key)
                    if function is None:
                        function = FunctionCoverage(
                            file=relative,
                            language="C#",
                            name=str(class_name),
                            signature=str(method_signature),
                            first_line=min(int(number) for number in lines),
                        )
                        file_coverage.functions[function_key] = function
                    add_hits(function.line_hits, lines)

                    for branch in body.get("Branches", []):
                        if not isinstance(branch, dict):
                            continue
                        branch_key = "|".join(
                            str(value)
                            for value in (
                                class_name,
                                method_signature,
                                branch.get("Line"),
                                branch.get("Offset"),
                                branch.get("EndOffset"),
                                branch.get("Path"),
                                branch.get("Ordinal"),
                            )
                        )
                        file_coverage.branch_hits[branch_key] = (
                            file_coverage.branch_hits.get(branch_key, 0) + int(branch.get("Hits", 0))
                        )


def load_managed_cobertura(
    path: Path,
    repo_root: Path,
    inventory: dict[str, str],
    files: dict[str, ManagedFileCoverage],
) -> None:
    root = ET.parse(path).getroot()
    for class_node in root.findall(".//class"):
        relative = canonical_path(class_node.get("filename", ""), repo_root, inventory)
        if relative is None or source_language(relative) != "C#":
            continue
        class_name = class_node.get("name", "")
        file_coverage = files.setdefault(relative, ManagedFileCoverage(relative))

        class_lines = class_node.find("lines")
        if class_lines is not None:
            for line_node in class_lines.findall("line"):
                line = int(line_node.get("number", "0"))
                file_coverage.line_hits[line] = file_coverage.line_hits.get(line, 0) + int(
                    line_node.get("hits", "0")
                )
                counts = parse_condition_counts(line_node.get("condition-coverage", ""))
                if counts is not None:
                    covered, total = counts
                    for index in range(total):
                        key = f"{class_name}|{line}|{index}"
                        file_coverage.branch_hits[key] = max(
                            file_coverage.branch_hits.get(key, 0), 1 if index < covered else 0
                        )

        methods_node = class_node.find("methods")
        if methods_node is None:
            continue
        for method_node in methods_node.findall("method"):
            method_name = method_node.get("name", "")
            signature = method_node.get("signature", "")
            line_nodes = method_node.findall("./lines/line")
            if not line_nodes:
                continue
            function_key = f"{class_name}|{method_name}|{signature}"
            function = file_coverage.functions.get(function_key)
            if function is None:
                function = FunctionCoverage(
                    file=relative,
                    language="C#",
                    name=f"{class_name}.{method_name}",
                    signature=signature,
                    first_line=min(int(node.get("number", "0")) for node in line_nodes),
                )
                file_coverage.functions[function_key] = function
            for line_node in line_nodes:
                line = int(line_node.get("number", "0"))
                function.line_hits[line] = function.line_hits.get(line, 0) + int(
                    line_node.get("hits", "0")
                )


def parse_condition_counts(value: str) -> tuple[int, int] | None:
    start = value.find("(")
    slash = value.find("/", start + 1)
    end = value.find(")", slash + 1)
    if start < 0 or slash < 0 or end < 0:
        return None
    return int(value[start + 1 : slash]), int(value[slash + 1 : end])


def managed_rows(
    files: dict[str, ManagedFileCoverage],
) -> tuple[list[CoverageRow], list[FunctionCoverage]]:
    rows: list[CoverageRow] = []
    functions: list[FunctionCoverage] = []
    for path in sorted(files, key=str.lower):
        coverage = files[path]
        uncovered = [line for line, hits in coverage.line_hits.items() if hits == 0]
        rows.append(
            CoverageRow(
                file=path,
                language="C#",
                status="measured",
                lines_covered=sum(hits > 0 for hits in coverage.line_hits.values()),
                lines_total=len(coverage.line_hits),
                branches_covered=sum(hits > 0 for hits in coverage.branch_hits.values()),
                branches_total=len(coverage.branch_hits),
                functions_covered=sum(function.covered for function in coverage.functions.values()),
                functions_total=len(coverage.functions),
                uncovered_lines=compact_ranges(uncovered),
            )
        )
        functions.extend(coverage.functions.values())
    return rows, functions


def load_rust_json(
    path: Path,
    repo_root: Path,
    inventory: dict[str, str],
) -> tuple[list[CoverageRow], list[FunctionCoverage]]:
    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    datasets = payload.get("data", [])
    if not isinstance(datasets, list):
        raise ValueError(f"LLVM coverage JSON has no data array: {path}")

    rows_by_file: dict[str, CoverageRow] = {}
    functions: dict[str, FunctionCoverage] = {}
    for dataset in datasets:
        for file_entry in dataset.get("files", []):
            relative = canonical_path(file_entry.get("filename", ""), repo_root, inventory)
            if relative is None or source_language(relative) != "Rust":
                continue
            summary = file_entry.get("summary", {})
            lines = summary.get("lines", {})
            branches = summary.get("branches", {})
            function_summary = summary.get("functions", {})
            rows_by_file[relative] = CoverageRow(
                file=relative,
                language="Rust",
                status="measured",
                lines_covered=int(lines.get("covered", 0)),
                lines_total=int(lines.get("count", 0)),
                branches_covered=int(branches.get("covered", 0)),
                branches_total=int(branches.get("count", 0)),
                functions_covered=int(function_summary.get("covered", 0)),
                functions_total=int(function_summary.get("count", 0)),
            )

        for function_entry in dataset.get("functions", []):
            execution_count = int(function_entry.get("count", 0))
            name = str(function_entry.get("name", ""))
            for raw_path in set(function_entry.get("filenames", [])):
                relative = canonical_path(raw_path, repo_root, inventory)
                if relative is None or source_language(relative) != "Rust":
                    continue
                first_line = rust_function_first_line(function_entry, raw_path)
                key = f"{relative}|{name}|{first_line}"
                functions[key] = FunctionCoverage(
                    file=relative,
                    language="Rust",
                    name=name,
                    signature=name,
                    first_line=first_line,
                    execution_count=execution_count,
                )
    return sorted(rows_by_file.values(), key=lambda row: row.file.lower()), list(functions.values())


def rust_function_first_line(function_entry: dict[str, Any], raw_path: str) -> int | None:
    filenames = function_entry.get("filenames", [])
    try:
        file_index = filenames.index(raw_path)
    except ValueError:
        return None
    lines = [
        int(region[2])
        for region in function_entry.get("regions", [])
        if isinstance(region, list) and len(region) >= 3 and int(region[0]) == file_index
    ]
    return min(lines) if lines else None


def compact_ranges(lines: Iterable[int]) -> str:
    ordered = sorted(set(lines))
    if not ordered:
        return ""
    ranges: list[str] = []
    start = previous = ordered[0]
    for line in ordered[1:]:
        if line == previous + 1:
            previous = line
            continue
        ranges.append(str(start) if start == previous else f"{start}-{previous}")
        start = previous = line
    ranges.append(str(start) if start == previous else f"{start}-{previous}")
    return ",".join(ranges)


def uninstrumented_rows(
    inventory: dict[str, str], measured_paths: set[str]
) -> list[CoverageRow]:
    rows: list[CoverageRow] = []
    for path in sorted(inventory.values(), key=str.lower):
        if path.lower() in measured_paths:
            continue
        language = source_language(path)
        if language == "C#":
            reason = "Not compiled into a runtime-instrumented test assembly"
        elif language == "Rust":
            reason = "Not present in the LLVM coverage report"
        else:
            reason = f"No runtime {language} coverage collector is configured"
        rows.append(CoverageRow(path, language or "Unknown", "N/A", reason=reason))
    return rows


def total_row(language: str, rows: Iterable[CoverageRow]) -> CoverageRow:
    measured = [row for row in rows if row.status == "measured"]
    return CoverageRow(
        file="TOTAL",
        language=language,
        status="measured",
        lines_covered=sum(row.lines_covered or 0 for row in measured),
        lines_total=sum(row.lines_total or 0 for row in measured),
        branches_covered=sum(row.branches_covered or 0 for row in measured),
        branches_total=sum(row.branches_total or 0 for row in measured),
        functions_covered=sum(row.functions_covered or 0 for row in measured),
        functions_total=sum(row.functions_total or 0 for row in measured),
    )


def write_reports(
    output_dir: Path,
    measured_rows: list[CoverageRow],
    functions: list[FunctionCoverage],
    na_rows: list[CoverageRow],
    inputs: dict[str, list[str]],
    runner_status: dict[str, Any] | None,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    all_rows = sorted(measured_rows + na_rows, key=lambda row: row.file.lower())
    write_file_csv(output_dir / "coverage-files.csv", all_rows)
    write_function_csv(output_dir / "coverage-functions.csv", functions)
    write_file_csv(output_dir / "coverage-uninstrumented.csv", na_rows)

    languages = sorted({row.language for row in measured_rows})
    totals = {language: total_row(language, measured_rows) for language in languages}
    combined = total_row("Combined measured", measured_rows)
    fully_covered = [
        row
        for row in measured_rows
        if row.lines_covered == row.lines_total
        and row.branches_covered == row.branches_total
        and row.functions_covered == row.functions_total
    ]
    summary = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "inputs": inputs,
        "runner_status": runner_status,
        "totals_by_language": {key: value.as_dict() for key, value in totals.items()},
        "combined_measured": combined.as_dict(),
        "measured_file_count": len(measured_rows),
        "fully_covered_file_count": len(fully_covered),
        "uninstrumented_file_count": len(na_rows),
        "files": [row.as_dict() for row in all_rows],
    }
    (output_dir / "coverage-summary.json").write_text(
        json.dumps(summary, indent=2) + "\n", encoding="utf-8"
    )
    write_markdown(
        output_dir / "coverage-summary.md",
        measured_rows,
        na_rows,
        totals,
        combined,
        fully_covered,
        runner_status,
    )
    print(
        f"Measured {len(measured_rows)} files; fully covered {len(fully_covered)}; "
        f"uninstrumented/N/A {len(na_rows)}."
    )
    print_metric_line(combined)


def write_file_csv(path: Path, rows: Iterable[CoverageRow]) -> None:
    fieldnames = [
        "File",
        "Language",
        "Status",
        "LinesCovered",
        "LinesTotal",
        "LinePercent",
        "BranchesCovered",
        "BranchesTotal",
        "BranchPercent",
        "FunctionsCovered",
        "FunctionsTotal",
        "FunctionPercent",
        "UncoveredLines",
        "Reason",
    ]
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(
                {
                    "File": row.file,
                    "Language": row.language,
                    "Status": row.status,
                    "LinesCovered": value_or_blank(row.lines_covered),
                    "LinesTotal": value_or_blank(row.lines_total),
                    "LinePercent": percent_or_blank(row.lines_covered, row.lines_total),
                    "BranchesCovered": value_or_blank(row.branches_covered),
                    "BranchesTotal": value_or_blank(row.branches_total),
                    "BranchPercent": percent_or_blank(row.branches_covered, row.branches_total),
                    "FunctionsCovered": value_or_blank(row.functions_covered),
                    "FunctionsTotal": value_or_blank(row.functions_total),
                    "FunctionPercent": percent_or_blank(row.functions_covered, row.functions_total),
                    "UncoveredLines": row.uncovered_lines,
                    "Reason": row.reason,
                }
            )


def write_function_csv(path: Path, functions: Iterable[FunctionCoverage]) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(
            stream,
            fieldnames=["File", "Language", "Covered", "FirstLine", "Name", "Signature"],
        )
        writer.writeheader()
        for function in sorted(
            functions, key=lambda item: (item.file.lower(), item.first_line or 0, item.signature)
        ):
            writer.writerow(
                {
                    "File": function.file,
                    "Language": function.language,
                    "Covered": function.covered,
                    "FirstLine": value_or_blank(function.first_line),
                    "Name": function.name,
                    "Signature": function.signature,
                }
            )


def write_markdown(
    path: Path,
    measured_rows: list[CoverageRow],
    na_rows: list[CoverageRow],
    totals: dict[str, CoverageRow],
    combined: CoverageRow,
    fully_covered: list[CoverageRow],
    runner_status: dict[str, Any] | None,
) -> None:
    lines = [
        "# Workspace Coverage",
        "",
        f"Generated: {datetime.now(timezone.utc).isoformat()}",
        "",
        "## Measured totals",
        "",
        "| Language | Files | Lines | Branches | Functions |",
        "|---|---:|---:|---:|---:|",
    ]
    for language, total in totals.items():
        file_count = sum(row.language == language for row in measured_rows)
        lines.append(markdown_total_line(language, file_count, total))
    lines.append(markdown_total_line("Combined measured", len(measured_rows), combined))
    lines.extend(
        [
            "",
            f"Fully covered files: **{len(fully_covered)}/{len(measured_rows)}**.",
            f"Uninstrumented/N/A first-party source files: **{len(na_rows)}**.",
        ]
    )
    if runner_status is not None:
        lines.extend(["", "## Runner status", "", "```json", json.dumps(runner_status, indent=2), "```"])

    ranked = sorted(
        measured_rows,
        key=lambda row: (
            CoverageRow.percent(row.lines_covered, row.lines_total) or 0,
            CoverageRow.percent(row.branches_covered, row.branches_total) or 0,
            CoverageRow.percent(row.functions_covered, row.functions_total) or 0,
            row.file.lower(),
        ),
    )[:30]
    lines.extend(
        [
            "",
            "## Lowest coverage files",
            "",
            "| File | Language | Lines | Branches | Functions |",
            "|---|---|---:|---:|---:|",
        ]
    )
    for row in ranked:
        lines.append(
            f"| `{row.file}` | {row.language} | "
            f"{metric_text(row.lines_covered, row.lines_total)} | "
            f"{metric_text(row.branches_covered, row.branches_total)} | "
            f"{metric_text(row.functions_covered, row.functions_total)} |"
        )

    reason_counts: dict[str, int] = {}
    for row in na_rows:
        reason_counts[row.reason] = reason_counts.get(row.reason, 0) + 1
    lines.extend(["", "## Uninstrumented inventory", "", "| Reason | Files |", "|---|---:|"])
    for reason, count in sorted(reason_counts.items(), key=lambda item: (-item[1], item[0])):
        lines.append(f"| {reason} | {count} |")
    lines.extend(
        [
            "",
            "See `coverage-files.csv`, `coverage-functions.csv`, and "
            "`coverage-uninstrumented.csv` for the complete inventories.",
            "",
        ]
    )
    path.write_text("\n".join(lines), encoding="utf-8")


def markdown_total_line(label: str, file_count: int, row: CoverageRow) -> str:
    return (
        f"| {label} | {file_count} | {metric_text(row.lines_covered, row.lines_total)} | "
        f"{metric_text(row.branches_covered, row.branches_total)} | "
        f"{metric_text(row.functions_covered, row.functions_total)} |"
    )


def metric_text(covered: int | None, total: int | None) -> str:
    percent = CoverageRow.percent(covered, total)
    if percent is None:
        return "N/A"
    return f"{percent:.2f}% ({covered}/{total})"


def print_metric_line(row: CoverageRow) -> None:
    print(
        f"{row.language}: lines {metric_text(row.lines_covered, row.lines_total)}, "
        f"branches {metric_text(row.branches_covered, row.branches_total)}, "
        f"functions {metric_text(row.functions_covered, row.functions_total)}"
    )


def value_or_blank(value: Any) -> Any:
    return "" if value is None else value


def percent_or_blank(covered: int | None, total: int | None) -> str:
    percent = CoverageRow.percent(covered, total)
    return "" if percent is None else f"{percent:.4f}"


def main() -> int:
    args = parse_args()
    repo_root = args.repo_root.resolve()
    output_dir = args.output_dir.resolve()
    inventory = tracked_source_files(repo_root)

    managed_files: dict[str, ManagedFileCoverage] = {}
    for report in args.managed_json:
        load_managed_json(report.resolve(), repo_root, inventory, managed_files)
    for report in args.managed_cobertura:
        load_managed_cobertura(report.resolve(), repo_root, inventory, managed_files)
    managed, managed_functions = managed_rows(managed_files)

    rust_by_file: dict[str, CoverageRow] = {}
    rust_functions: dict[str, FunctionCoverage] = {}
    for report in args.rust_json:
        rows, functions = load_rust_json(report.resolve(), repo_root, inventory)
        for row in rows:
            rust_by_file[row.file] = row
        for function in functions:
            rust_functions[f"{function.file}|{function.signature}|{function.first_line}"] = function

    measured = managed + list(rust_by_file.values())
    measured_paths = {row.file.lower() for row in measured}
    na_rows = uninstrumented_rows(inventory, measured_paths)
    runner_status = None
    if args.status_json is not None and args.status_json.exists():
        runner_status = json.loads(args.status_json.read_text(encoding="utf-8-sig"))
    inputs = {
        "managed_json": [str(path.resolve()) for path in args.managed_json],
        "managed_cobertura": [str(path.resolve()) for path in args.managed_cobertura],
        "rust_json": [str(path.resolve()) for path in args.rust_json],
    }
    write_reports(
        output_dir,
        measured,
        managed_functions + list(rust_functions.values()),
        na_rows,
        inputs,
        runner_status,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, ET.ParseError, json.JSONDecodeError) as exc:
        print(f"coverage analysis failed: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
