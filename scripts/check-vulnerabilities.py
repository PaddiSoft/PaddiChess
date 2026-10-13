#!/usr/bin/env python3
"""Fail the gate on reported vulnerabilities or unavailable audit sources."""
import argparse
import json
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True)
parser.add_argument('--project', default='PaddiChess.slnx')
args = parser.parse_args()
result = subprocess.run(['dotnet', 'list', args.project, 'package', '--vulnerable',
                         '--include-transitive', '--format', 'json', '--no-restore'],
                        text=True, capture_output=True)
if result.returncode: raise SystemExit('NuGet audit command failed; inspect the dependency source connection.')
try: report = json.loads(result.stdout)
except ValueError: raise SystemExit('NuGet audit returned no valid JSON; this is not a clean result.')
Path(args.output).parent.mkdir(parents=True, exist_ok=True)
Path(args.output).write_text(json.dumps(report, indent=2)+'\n')
findings=[]
def walk(value):
    if isinstance(value,dict):
        if value.get('vulnerabilities'): findings.append('vulnerable dependency: '+value.get('id','unknown'))
        if value.get('level','').lower() in ['error','warning']: findings.append('audit source warning/error')
        for v in value.values(): walk(v)
    elif isinstance(value,list):
        for v in value: walk(v)
walk(report)
if not report.get('projects'): findings.append('audit returned no projects')
if 'NU1900' in result.stderr + result.stdout: findings.append('vulnerability data unavailable')
if findings: raise SystemExit('\n'.join(sorted(set(findings))))
print('NuGet audit completed; no known vulnerable dependencies reported for this restored RID.')
