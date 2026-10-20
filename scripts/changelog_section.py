#!/usr/bin/env python3
"""Prints one version's section of a changelog, without its heading, and fails if there is none.

The release workflows use it for the release notes and the manifest's changelog:

  python3 scripts/changelog_section.py CHANGELOG.md 1.0.17
"""
import re
import sys


def main() -> None:
    path, version = sys.argv[1], sys.argv[2]
    with open(path, encoding="utf-8") as f:
        text = f.read()

    # From the "## [1.0.17]" heading up to the next "## [" heading.
    match = re.search(r"^## \[" + re.escape(version) + r"\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.S | re.M)
    section = match.group(1).strip() if match else ""
    if not section:
        sys.exit(f"{path} has no section for {version}")
    print(section)


if __name__ == "__main__":
    main()
