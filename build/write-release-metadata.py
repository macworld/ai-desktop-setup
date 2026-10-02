#!/usr/bin/env python3
"""Write candidate metadata after native verification; JSON alone is not a trust anchor."""
import argparse
import hashlib
import json
from pathlib import Path
import re

OWNED = {'AI.Desktop.Setup.exe', 'AI.Desktop.Setup.Core.dll'}

def write_metadata(root, verification, version, tag, commit, repository, run_id):
    root = Path(root)
    if (not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version) or tag != 'v'+version or
        not re.fullmatch(r'[0-9a-f]{40}', commit) or not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or
        not re.fullmatch(r'[1-9][0-9]*', str(run_id))):
        raise ValueError('Invalid release identity')
    if verification.get('schema_version') != 1 or verification.get('source_commit') != commit or str(verification.get('workflow_run_id')) != str(run_id):
        raise ValueError('Verification source/run mismatch')
    expected = {f'AI-Desktop-Setup-{version}-{a}.exe': a for a in ('x64','arm64')}
    records = verification.get('artifacts', [])
    if len(records) != 2 or {a['name'] for a in records} != set(expected) or {p.name for p in root.glob('*.exe')} != set(expected):
        raise ValueError('Exact two-architecture candidate required')
    artifacts = []
    for a in records:
        if (a.get('product_name') != 'AI Desktop Setup' or a.get('product_version') != version or a.get('architecture') != expected[a['name']] or
            any(a.get(k) is not True for k in ('outer_signature_valid','inner_signatures_valid','timestamp_valid'))):
            raise ValueError('Candidate identity or native verification failed')
        inner = a.get('inner_files', [])
        owned = [x for x in inner if x.get('ownership') == 'owned']
        if len(owned) != 2 or {x['path'] for x in owned} != OWNED or any(x.get('signature_valid') is not True for x in owned):
            raise ValueError('Owned inner signature missing')
        if len({x['path'].casefold() for x in inner}) != len(inner) or any(x.get('ownership') not in ('owned','third_party','support') or not re.fullmatch('[0-9a-f]{64}',x.get('sha256','')) for x in inner):
            raise ValueError('Invalid inner inventory')
        file = root/a['name']
        if file.is_symlink() or not file.is_file(): raise ValueError('Invalid candidate file')
        data=file.read_bytes();digest=hashlib.sha256(data).hexdigest()
        if digest != a['sha256']: raise ValueError('Candidate changed after native verification')
        artifacts.append(dict(name=a['name'],architecture=a['architecture'],bytes=len(data),sha256=digest,signed=True))
    manifest=dict(schema_version=1,version=version,tag=tag,source_commit=commit,source_repository=repository,workflow_run_id=str(run_id),artifacts=sorted(artifacts,key=lambda a:a['name']))
    targets=[root/'release-manifest.json',root/'SHA256SUMS']
    if any(p.exists() or p.is_symlink() for p in targets): raise ValueError('Metadata already exists')
    targets[0].write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    targets[1].write_text(''.join(a['sha256']+'  '+a['name']+'\n' for a in manifest['artifacts']),encoding='ascii')
    return manifest

def main():
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('release-dir','verification','version','tag','commit','repository','run-id'):p.add_argument('--'+name,required=True)
    a=p.parse_args()
    write_metadata(a.release_dir,json.loads(Path(a.verification).read_text(encoding='utf-8-sig')),a.version,a.tag,a.commit,a.repository,a.run_id)

if __name__=='__main__':main()
