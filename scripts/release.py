"""Validate release metadata and prepare the public release files."""

import argparse
import hashlib
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def release_metadata():
    project = ET.parse(ROOT / "src/CodexCleaner/CodexCleaner.csproj")
    version = project.findtext("PropertyGroup/Version", "").strip()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError("Project Version must have three numeric components.")
    changelog = (ROOT / "docs/CHANGELOG.md").read_text(encoding="utf-8-sig")
    lines = changelog.splitlines()
    heading = f"## {version}"
    if lines.count(heading) != 1:
        raise ValueError(f"CHANGELOG.md must contain exactly one {heading} section.")
    start = lines.index(heading) + 1
    end = next((i for i in range(start, len(lines)) if lines[i].startswith("## ")), len(lines))
    notes = "\n".join(lines[start:end]).strip()
    if not notes:
        raise ValueError(f"CHANGELOG.md has no release notes for {version}.")
    return version, notes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("verify", "package", "notes"))
    parser.add_argument("--tag", default=os.environ.get("TAG"))
    parser.add_argument("--output", type=Path, default=ROOT / "notes.md")
    args = parser.parse_args()
    version, notes = release_metadata()
    if args.command in ("verify", "notes") or args.tag:
        if args.tag != f"v{version}":
            raise ValueError(f"Tag {args.tag!r} does not match project version v{version}.")
    if args.command == "package":
        executable = ROOT / "dist/CodexCleaner.exe"
        if not executable.is_file() or executable.stat().st_size == 0:
            raise ValueError("dist/CodexCleaner.exe is missing or empty.")
        with executable.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        (ROOT / "dist/SHA256SUMS.txt").write_text(
            f"{digest}  CodexCleaner.exe\n", encoding="ascii", newline="\n"
        )
    elif args.command == "notes":
        args.output.write_text(notes + "\n", encoding="utf-8", newline="\n")
    print(f"Release {version}: {args.command} complete.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, ET.ParseError) as error:
        print(f"Release preparation failed: {error}", file=sys.stderr)
        sys.exit(1)
