#!/usr/bin/env python3
"""Fail when source trees/packages contain generated, runtime, secret, or archive artifacts."""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN_DIRECTORY_NAMES = {
    ".git",
    ".idea",
    ".vs",
    ".vscode",
    "__pycache__",
    ".pytest_cache",
    "bin",
    "obj",
    "node_modules",
    "dist",
    "TestResults",
    "test-results",
    "playwright-report",
    "pgdata",
    "tmp",
    "output",
}
FORBIDDEN_FILE_NAMES = {".DS_Store", "Thumbs.db", "debug.log"}
FORBIDDEN_SUFFIXES = {".rar", ".zip", ".7z", ".bak", ".tmp", ".pyc", ".pyo"}
SECRET_FILE_NAMES = {".env"}
SECRET_PATH_PARTS = {"secrets"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--package",
        action="store_true",
        help="also reject a root .git directory; use this on an extracted delivery package",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    findings: list[str] = []
    for current, directory_names, file_names in os.walk(ROOT):
        current_path = Path(current)
        relative_current = current_path.relative_to(ROOT)

        blocked_dirs: list[str] = []
        for name in directory_names:
            is_root_git = relative_current == Path(".") and name == ".git"
            if name in FORBIDDEN_DIRECTORY_NAMES and not (is_root_git and not args.package):
                blocked_dirs.append(name)

        for name in blocked_dirs:
            findings.append(str(relative_current / name))
        directory_names[:] = [name for name in directory_names if name not in blocked_dirs and name != ".git"]

        for name in file_names:
            relative = relative_current / name
            path = current_path / name
            if name in FORBIDDEN_FILE_NAMES or name in SECRET_FILE_NAMES or path.suffix.lower() in FORBIDDEN_SUFFIXES:
                findings.append(str(relative))
                continue
            if SECRET_PATH_PARTS.intersection(relative.parts):
                findings.append(str(relative))

    if findings:
        print("Repository cleanliness check failed. Remove these artifacts:", file=sys.stderr)
        for finding in sorted(set(findings), key=str.casefold):
            print(f" - {finding}", file=sys.stderr)
        return 1

    mode = "delivery package" if args.package else "working tree"
    print(f"Repository cleanliness check passed ({mode}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
