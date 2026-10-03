#!/usr/bin/env python3
"""Exact Git-tree staging and complete retained-file inventories (no audit rules)."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import xml.etree.ElementTree as ET


def stage(source, destination, commit):
    source, destination = Path(source), Path(destination)
    if not re.fullmatch(r'[0-9a-f]{40}', commit) or destination.exists():
        raise ValueError('An exact commit and unused destination are required')
    def git(*args):
        return subprocess.check_output(['git', '-C', str(source), *args], stderr=subprocess.DEVNULL)
    if git('rev-parse', commit + '^{commit}').decode().strip() != commit:
        raise ValueError('Commit mismatch')
    entries = git('ls-tree', '-rz', '--full-tree', commit).split(b'\0')
    destination.mkdir(parents=True)
    for entry in filter(None, entries):
        head, raw_path = entry.split(b'\t', 1)
        mode, kind, oid = head.split()
        path = PurePosixPath(raw_path.decode('utf-8'))
        if mode not in (b'100644', b'100755') or kind != b'blob' or path.is_absolute() or '..' in path.parts or '\\' in str(path) or ':' in str(path):
            raise ValueError('Unsupported tree entry')
        data = git('cat-file', 'blob', oid.decode())
        if data.startswith(b'version https://git-lfs.github.com/spec/v1'):
            raise ValueError('LFS pointers are unsupported')
        target = destination.joinpath(*path.parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        if mode == b'100755':
            target.chmod(0o755)
    return inventory(destination)


def inventory(root):
    root = Path(root)
    result = []
    for parent, dirs, files in os.walk(root, followlinks=False):
        for name in sorted(dirs + files):
            path = Path(parent) / name
            if path.is_symlink():
                raise ValueError('Symlink in build cohort')
            if path.is_dir():
                continue
            if not path.is_file():
                raise ValueError('Special file in build cohort')
            data = path.read_bytes()
            result.append(dict(path=path.relative_to(root).as_posix(), bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    return sorted(result, key=lambda entry: entry['path'])


OPTIONAL_PACKAGE_TESTS = {
    'AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageRequiresNativeTrustAndPinsBytes',
    'AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust',
}


def trx_report(path):
    tree = ET.parse(path)
    def nodes(name):
        return [n for n in tree.iter() if n.tag.rsplit('}', 1)[-1] == name]
    counters, summaries = nodes('Counters'), nodes('ResultSummary')
    results, definitions = nodes('UnitTestResult'), nodes('UnitTest')
    if len(counters) != 1:
        raise ValueError('Missing or ambiguous test counters')
    if len(summaries) != 1 or summaries[0].get('outcome') != 'Completed':
        raise ValueError('Missing, ambiguous, or unsuccessful test run summary')
    # The locked VSTest TRX logger sets executed = passed + failed and
    # leaves every auxiliary outcome counter zero, including notExecuted.
    # Skips must be reconciled with the individual results, not that field.
    zero_counts = ('failed', 'error', 'timeout', 'aborted', 'inconclusive',
                   'passedButRunAborted', 'notRunnable', 'notExecuted',
                   'disconnected', 'warning', 'completed', 'inProgress', 'pending')
    counts = {k: int(counters[0].get(k, '-1')) for k in ('total', 'executed', 'passed') + zero_counts}
    result_ids = [n.get('testId') for n in results]
    definition_ids = [n.get('id') for n in definitions]
    if (counts['total'] <= 0 or counts['executed'] <= 0 or any(counts[k] != 0 for k in zero_counts) or
        counts['passed'] != counts['executed'] or
        len(results) != counts['total'] or len(definitions) != counts['total'] or
        any(not test_id for test_id in result_ids + definition_ids) or
        len(set(result_ids)) != len(results) or set(result_ids) != set(definition_ids)):
        raise ValueError('Inconsistent or empty test execution')
    names = {n.get('id'): n.get('name') for n in definitions}
    skipped, passed = [], 0
    for result in results:
        name = result.get('testName')
        if names[result.get('testId')] != name:
            raise ValueError('Test identity mismatch')
        if result.get('outcome') == 'Passed':
            passed += 1
        elif result.get('outcome') == 'NotExecuted' and name in OPTIONAL_PACKAGE_TESTS:
            skipped.append(name)
        else:
            raise ValueError('Failed, unexpected skipped, or unknown test outcome')
    if passed != counts['passed'] or passed + len(skipped) != counts['total']:
        raise ValueError('Result counters disagree')
    return dict(total=counts['total'], executed=passed, passed=passed, skipped=sorted(skipped),
                unverified_native_gates=sorted(skipped))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    staging = sub.add_parser('stage')
    for name in ('source', 'destination', 'commit'):
        staging.add_argument('--' + name, required=True)
    listing = sub.add_parser('inventory')
    listing.add_argument('--root', required=True)
    listing.add_argument('--output', required=True)
    trx = sub.add_parser('trx')
    trx.add_argument('paths', nargs='+')
    args = parser.parse_args()
    if args.command == 'stage':
        stage(args.source, args.destination, args.commit)
    elif args.command == 'inventory':
        root, output = Path(args.root).resolve(), Path(args.output).resolve()
        if root == output or root in output.parents:
            raise ValueError('Write inventory outside the inventoried root')
        output.write_text(json.dumps(inventory(root), indent=2) + '\n', encoding='utf-8')
    else:
        print(json.dumps({Path(p).name: trx_report(p) for p in args.paths}))


if __name__ == '__main__':
    main()
