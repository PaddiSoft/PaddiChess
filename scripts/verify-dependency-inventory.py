#!/usr/bin/env python3
"""Verify all release RID locks, checked-in notices and immutable binary assets."""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
third_party = root / 'PaddiChess/Packaging/ThirdParty'
failures = []
checked = 0

def verify(base, item):
    global checked
    path = base / item['file']
    checked += 1
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
        failures.append('missing or changed: ' + str(path.relative_to(root)))

inventory = json.loads((third_party / 'dependencies.json').read_text())
known = {(item['package'].lower(), item['version']) for item in inventory}
for package in inventory:
    if not package.get('notices'):
        failures.append('missing notices: ' + package['package'])
    for notice in package['notices']:
        verify(third_party, notice)
for item in json.loads((third_party / 'Upstream/sources.json').read_text()):
    verify(third_party / 'Upstream', item)
for item in json.loads((root / 'PaddiChess/Packaging/runtime-assets.json').read_text()):
    verify(root, item)
    for notice in item['notices']:
        if not (root / notice).is_file(): failures.append('missing source/license notice: ' + notice)

for rid in ['osx-arm64', 'osx-x64', 'win-x64', 'linux-x64']:
    lock = root / f'PaddiChess/packages.{rid}.lock.json'
    for dependencies in json.loads(lock.read_text())['dependencies'].values():
        for name, package in dependencies.items():
            if package['type'] == 'Project': continue
            # This package is explicitly Debug-only in the application project.
            if name == 'AvaloniaUI.DiagnosticsSupport': continue
            if (name.lower(), package['resolved']) not in known:
                failures.append(f'{rid}: missing license inventory for {name}/{package["resolved"]}')
if failures: raise SystemExit('\n'.join(failures))
print(f'Verified {checked} asset/notice hashes and all four release RID dependency inventories.')
