#!/usr/bin/env python3
"""Verify the documentation status registry and required project documents.

This check is intentionally dependency-light. It validates the current source tree
and, when a Git base is supplied, requires documentation updates for code changes.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = [
    ROOT / "docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md",
    ROOT / "docs/implementation/PROJECT-STATUS.yaml",
    ROOT / "docs/implementation/CURRENT-PHASE.md",
    ROOT / "docs/platform-rules/capability-matrix.md",
    ROOT / "docs/CHANGELOG.md",
    ROOT / "docs/DOCUMENTATION-MAP.md",
    ROOT / "README.md",
    ROOT / "AGENTS.md",
]


def git_changed(base: str) -> set[str]:
    command = ["git", "diff", "--name-only", f"{base}...HEAD"]
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "git diff failed")
    return {line.strip().replace("\\", "/") for line in result.stdout.splitlines() if line.strip()}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", help="Git base ref for documentation transaction checks")
    args = parser.parse_args()
    errors: list[str] = []

    for path in REQUIRED:
        if not path.is_file() or path.stat().st_size == 0:
            errors.append(f"missing or empty required document: {path.relative_to(ROOT)}")

    status_text = (ROOT / "docs/implementation/PROJECT-STATUS.yaml").read_text(encoding="utf-8")
    current_text = (ROOT / "docs/implementation/CURRENT-PHASE.md").read_text(encoding="utf-8")
    spec_text = (ROOT / "docs/specification/RAVENCIA-NIHAI-PROJE-BELGESI.md").read_text(encoding="utf-8")
    readme_text = (ROOT / "README.md").read_text(encoding="utf-8")
    agents_text = (ROOT / "AGENTS.md").read_text(encoding="utf-8")

    expected_scope = {"TRENDYOL", "TRENDYOL_EFATURAM"}
    for code in expected_scope:
        if code not in status_text or code not in agents_text:
            errors.append(f"active scope {code} missing from status or AGENTS")
    valid_f3_states = ("F3_CLOSURE_ACTIVE", "F3_CORE_CODE_COMPLETE_VALIDATION_PENDING")
    valid_f4_states = ("F4_IN_PROGRESS", "F4_CODE_COMPLETE_VALIDATION_PENDING")
    if not any(state in current_text for state in valid_f3_states) or not any(state in current_text for state in valid_f4_states):
        errors.append("CURRENT-PHASE does not declare the expected active or validation-pending F3/F4 state")
    status_version_match = re.search(r'^document_version:\s*["\']?([^"\'\s]+)', status_text, flags=re.M)
    spec_version_match = re.search(r'\*\*Belge sürümü:\*\*\s*([^\s]+)', spec_text)
    current_version_match = re.search(r'\*\*Ana plan sürümü:\*\*\s*([^\s]+)', current_text)
    if not status_version_match or not spec_version_match or not current_version_match:
        errors.append("master plan, status or current phase document version could not be read")
    else:
        versions = {status_version_match.group(1), spec_version_match.group(1), current_version_match.group(1)}
        if len(versions) != 1:
            errors.append(
                "master plan, PROJECT-STATUS and CURRENT-PHASE document versions do not match: "
                f"{spec_version_match.group(1)} / {status_version_match.group(1)} / {current_version_match.group(1)}"
            )
    if "PROJECT-STATUS.yaml" not in readme_text:
        errors.append("README does not point to PROJECT-STATUS.yaml")

    if args.base:
        changed = git_changed(args.base)
        code_changed = any(p.startswith(("src/", "tests/", "deploy/")) or p.endswith((".sln", ".props", ".csproj")) for p in changed)
        if code_changed:
            required_changes = {
                "docs/implementation/PROJECT-STATUS.yaml",
                "docs/implementation/CURRENT-PHASE.md",
                "docs/CHANGELOG.md",
            }
            missing = sorted(required_changes - changed)
            if missing:
                errors.append("code changed without documentation transaction updates: " + ", ".join(missing))
            if not any(re.match(r"docs/implementation/F.*-evidence-log\.md$", p) for p in changed):
                errors.append("code changed without a phase evidence-log update")

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print("Documentation status and transaction checks passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
