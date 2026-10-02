#!/usr/bin/env python3
"""Bind delayed acceptance to the original run/attempt and every candidate byte."""
import argparse
import hashlib
import json
from pathlib import Path
from package import inventory


def files(root):
    return [dict(name=n,bytes=len(b),sha256=hashlib.sha256(b).hexdigest()) for n,b in inventory(root).items() if n!='candidate-index.json']


def seal(root,commit,run,attempt):
    path=Path(root)/'candidate-index.json'
    if path.exists():raise ValueError('Candidate is already sealed')
    result=dict(schema_version=1,source_commit=commit,workflow_run_id=str(run),workflow_run_attempt=str(attempt),files=files(root))
    path.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check(root,expected_digest,commit,run,attempt):
    path=Path(root)/'candidate-index.json'
    if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest()!=expected_digest:raise ValueError('Candidate index digest changed')
    result=json.loads(path.read_text())
    if (result['schema_version']!=1 or result['source_commit']!=commit or result['workflow_run_id']!=str(run) or result['workflow_run_attempt']!=str(attempt) or result['files']!=files(root)):raise ValueError('Candidate bytes or original source/run/attempt changed')
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('command',choices=['seal','check'])
    for n in ('root','commit','run','attempt'):p.add_argument('--'+n,required=True)
    p.add_argument('--digest');a=p.parse_args()
    if a.command=='seal':print(seal(a.root,a.commit,a.run,a.attempt))
    else:check(a.root,a.digest,a.commit,a.run,a.attempt)

if __name__=='__main__':main()
