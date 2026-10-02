#!/usr/bin/env python3
"""Package a complete verified payload. This stage never writes final release hashes."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
OWNED={'AI.Desktop.Setup.exe','AI.Desktop.Setup.Core.dll'}


def safe_path(path):
    if not path or any(ord(c)<32 for c in path) or any(c in path for c in '\\:$\x00{}"') or path.startswith('/'):
        raise ValueError('Unsafe payload path')
    for part in path.split('/'):
        if part in ('','.','..') or part[-1] in '. ' or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?',part):raise ValueError('Windows path alias')
    return path


def inventory(root):
    root=Path(root)
    if root.is_symlink() or (hasattr(root,'is_junction') and root.is_junction()) or not root.is_dir():raise ValueError('Payload root must be an ordinary directory')
    result={};folded=set()
    for p in sorted(root.rglob('*')):
        if p.is_symlink() or (hasattr(p,'is_junction') and p.is_junction()):raise ValueError('Payload link/reparse point')
        name=safe_path(p.relative_to(root).as_posix())
        if name.casefold() in folded:raise ValueError('Case-insensitive payload collision')
        folded.add(name.casefold())
        if p.is_dir():continue
        if not p.is_file():raise ValueError('Special payload file')
        result[name]=p.read_bytes()
    return result


def validate_payload(root,architecture):
    if architecture not in ('x64','arm64'):raise ValueError('Unknown architecture')
    data=inventory(root)
    runtime=json.loads((ROOT/'build/runtime-inventory.json').read_text())
    support={'LICENSE.txt':(ROOT/'LICENSE').read_bytes(),'THIRD-PARTY-NOTICES.md':(ROOT/'THIRD-PARTY-NOTICES.md').read_bytes()}
    support.update({'licenses/'+n:b for n,b in inventory(ROOT/'licenses').items()})
    notices={'licenses/'+v['package']+'/THIRD-PARTY-NOTICES.TXT' for v in runtime.values() if v['notice_required']}
    expected=OWNED|set(runtime)|set(support)|notices|{'AI.Desktop.Setup.exe.config'}
    if set(data)!=expected:raise ValueError('Incomplete or extra payload files')
    for name,entry in runtime.items():
        if hashlib.sha256(data[name]).hexdigest()!=entry['sha256']:raise ValueError('Third-party DLL changed')
    if any(data[n]!=b for n,b in support.items()):raise ValueError('License/support content changed')
    if any(hashlib.sha256(data[n]).hexdigest()!='6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21' for n in notices):raise ValueError('Required notice missing/changed')
    spec=importlib.util.spec_from_file_location('signed_pe',ROOT/'build/signed_pe.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
    if m.framing(data['AI.Desktop.Setup.exe'],outer=False)['machine']!={'x64':0x8664,'arm64':0xaa64}[architecture]:raise ValueError('Wrong application architecture')
    if m.framing(data['AI.Desktop.Setup.Core.dll'],outer=False)['machine']!={'x64':0x8664,'arm64':0xaa64}[architecture]:raise ValueError('Wrong shared library architecture')
    xml=ET.fromstring(data['AI.Desktop.Setup.exe.config']);r=xml.findall('./startup/supportedRuntime')
    if len(r)!=1 or r[0].get('sku')!={'x64':'.NETFramework,Version=v4.8','arm64':'.NETFramework,Version=v4.8.1'}[architecture]:raise ValueError('Wrong Framework target')
    return [dict(path=n,bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),ownership='owned' if n in OWNED else 'third_party' if n in runtime else 'support') for n,b in sorted(data.items())]


def package(payload,architecture,version,output,makensis,verification=None):
    payload=Path(payload).absolute();output=Path(output).absolute()
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+',version):raise ValueError('Invalid version')
    if output.exists() or output.is_symlink():raise ValueError('Refusing artifact overwrite')
    entries=validate_payload(payload,architecture)
    from signed_pe import framing
    for name in OWNED:framing((payload/name).read_bytes(),require_signed=True,outer=False)
    if verification is None:raise ValueError('Native inner verification receipt required')
    receipt=json.loads(Path(verification).read_text(encoding='utf-8-sig'))
    got=receipt.get('payloads',{}).get(architecture)
    if got!=entries:raise ValueError('Payload differs from native verified inventory')
    prefix='/' if os.name=='nt' else '-'
    if subprocess.check_output([makensis,prefix+'VERSION'],text=True).strip() not in ('v3.12','3.12'):raise ValueError('Exactly NSIS 3.12 required')
    output.parent.mkdir(parents=True,exist_ok=True)
    # Explicit sorted paths avoid platform-dependent File /r expansion. All strings
    # are canonical and cannot inject NSIS syntax. No timestamps are copied.
    with tempfile.TemporaryDirectory() as tmp:
        include=Path(tmp)/'payload.nsh'
        lines=['SetDateSave off']
        for item in entries:
            name=item['path'];parent=Path(name).parent.as_posix();dest='$PayloadPath'+('\\'+parent.replace('/','\\') if parent!='.' else '')
            lines.extend([f'SetOutPath "{dest}"',f'File "{payload.as_posix()}/{name}"'])
        include.write_text('\n'.join(lines)+'\n',encoding='utf-8')
        subprocess.run([makensis,prefix+'NOCONFIG',prefix+'V2',*[prefix+'D'+x for x in (f'PAYLOAD_DIR={payload}',f'PAYLOAD_INCLUDE={include}',f'OUTPUT_FILE={output}',f'VERSION={version}',f'ARCH={architecture}')],str(ROOT/'packaging/launcher.nsi')],check=True)
    return entries


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for n in ('payload','architecture','version','output'):p.add_argument('--'+n,required=True)
    p.add_argument('--inner-verification',required=True);p.add_argument('--makensis',default=os.environ.get('AI_SETUP_MAKENSIS','makensis'))
    a=p.parse_args();package(a.payload,a.architecture,a.version,a.output,a.makensis,a.inner_verification)

if __name__=='__main__':main()
