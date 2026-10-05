#!/usr/bin/env python3
"""Check publishable paths without printing credential values or source snippets.

Source mode uses Git's tracked/unignored inventory, when available. Release mode
checks an unpacked build or ZIP, including ASCII and UTF-16 strings in managed
binaries. This is a conservative local check, not a substitute for secret review.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile


OPAQUE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".icns", ".wav", ".mp3", ".nnue", ".onnx"}
LOCAL_DIRS = {".git", ".build", "artifacts", "dist", "bin", "obj", "TestResults", "node_modules", ".paddi", "__pycache__"}
PRIVATE_SUFFIXES = {".p12", ".pfx", ".key", ".pem", ".mobileprovision"}
MAX_SOURCE_BYTES = 100 * 1024 * 1024
MAX_SCAN_BYTES = 80 * 1024 * 1024
PATTERNS = {
    "credential_pattern": re.compile(rb"\b(?:sk-|ghp_|github_pat_|AKIA)[A-Za-z0-9_-]{16,}"),
    "private_key_material": re.compile(rb"-----BEGIN (?:[A-Z0-9]+ )?PRIVATE KEY-----"),
    "url_embedded_credentials": re.compile(rb"https?://[^\s/'\"<>:@]+:[^\s/'\"<>@]+@[^\s/'\"<>]+"),
    "absolute_user_path": re.compile(rb"(?:/Users/[A-Za-z0-9._-]+/|[A-Za-z]:\\Users\\[A-Za-z0-9._-]+\\)"),
    "personal_signing_default": re.compile(rb"PADDI_SIGN_IDENTITY:-[A-Za-z0-9][^}\r\n]+"),
    "embedded_api_endpoint_default": re.compile(rb"LlmBaseUrl\s*\{[^\r\n}]*}\s*=\s*[\"']https?://[^\"']+"),
}


def private_filename(name: str) -> bool:
    path = Path(name)
    return (path.suffix.lower() in PRIVATE_SUFFIXES or
            path.name == ".env" or path.name.startswith(".env.") and path.name != ".env.example" or
            path.name.endswith(".local.json"))


def source_paths(root: Path):
    # Include tracked files even when .gitignore now excludes them: adding an
    # ignore entry does not remove an accidentally staged secret from Git.
    try:
        result = subprocess.run(["git", "-C", str(root), "ls-files", "--cached", "--others",
                                 "--exclude-standard", "-z"], capture_output=True, check=False)
    except FileNotFoundError:
        result = None
    if result is not None and result.returncode == 0:
        for name in sorted(set(os.fsdecode(p) for p in result.stdout.split(b"\0") if p)):
            yield name, root / name
        return
    for directory, dirs, names in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in LOCAL_DIRS and not d.endswith(".app"))
        for name in sorted(names):
            path = Path(directory) / name
            # No repository yet: emulate ignored private developer files. With
            # Git present a staged private file is still audited above.
            if private_filename(name):
                continue
            yield path.relative_to(root).as_posix(), path


def scan(name: str, size: int, read, release: bool, findings: list[dict[str, str]]) -> bool:
    def report(category: str, severity: str = "error"):
        findings.append({"path": name, "category": category, "severity": severity})

    if private_filename(name):
        report("private_configuration_or_signing_file")
    if not release and size > MAX_SOURCE_BYTES:
        report("exceeds_github_source_file_limit")
    if Path(name).suffix.lower() in OPAQUE_SUFFIXES:
        return False
    if size > MAX_SCAN_BYTES:
        report("requires_manual_large_file_secret_review")
        return False
    data = read()
    # Managed executable strings are commonly UTF-16; removing NULs also keeps
    # ASCII content searchable without emitting any decoded text.
    searchable = data.replace(b"\0", b"")
    for category, pattern in PATTERNS.items():
        if pattern.search(searchable):
            binary = Path(name).suffix.lower() in {".dll", ".dylib", ".so", ".pdb", ".exe"}
            own_binary = Path(name).name.startswith(("Paddi", "PikaDesk"))
            contains_local_home = os.fsencode(str(Path.home()) + os.sep) in searchable
            if category == "absolute_user_path" and release and binary and not own_binary and not contains_local_home:
                # NuGet/native runtimes may retain their upstream compiler paths.
                # Keep these visible for review without labelling them as this
                # developer's personal path or blocking an otherwise clean build.
                report("dependency_embedded_build_path", "review")
            else:
                report(category)
    return True


def audit(root: Path, release: Path | None) -> dict:
    findings: list[dict[str, str]] = []
    scanned = 0
    skipped_assets = 0
    if release and release.is_file():
        if not zipfile.is_zipfile(release):
            raise ValueError("Release file must be a ZIP; use an unpacked directory for other archive formats.")
        with zipfile.ZipFile(release) as archive:
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                if scan(entry.filename, entry.file_size, lambda e=entry: archive.read(e), True, findings):
                    scanned += 1
                else:
                    skipped_assets += 1
    else:
        directory = release or root
        paths = ((p.relative_to(directory).as_posix(), p) for p in directory.rglob("*") if p.is_file()) if release else source_paths(root)
        for name, path in paths:
            if path.is_symlink():
                findings.append({"path": name, "category": "symlink_requires_target_review", "severity": "error"})
                continue
            if not path.is_file():
                continue
            if scan(name, path.stat().st_size, path.read_bytes, release is not None, findings):
                scanned += 1
            else:
                skipped_assets += 1
    return {"mode": "release" if release else "source", "files_scanned": scanned,
            "opaque_assets_skipped": skipped_assets, "findings": sorted(findings, key=lambda f: (f["path"], f["category"]))}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--release", type=Path, help="Unpacked release directory or ZIP file")
    args = parser.parse_args()
    if not args.root.is_dir() or args.release is not None and not args.release.exists():
        parser.error("The requested source/release path does not exist.")
    try:
        report = audit(args.root.resolve(), args.release.resolve() if args.release else None)
    except (OSError, ValueError, zipfile.BadZipFile):
        print(json.dumps({"error": "Audit could not read the selected files."}))
        return 2
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if any(f["severity"] == "error" for f in report["findings"]) else 0


if __name__ == "__main__":
    sys.exit(main())
