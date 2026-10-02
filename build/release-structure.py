#!/usr/bin/env python3
"""Read-only structural checks plus isolated extraction. Native trust is separate."""
import argparse
import json
from pathlib import Path
from package import inventory, validate_payload, OWNED
from signed_pe import compare_signing
from nsis_payload import decode,verify_payload


def verify_inner(unsigned,signed):
    unsigned,signed=Path(unsigned),Path(signed)
    if {p.name for p in signed.iterdir()}!={'x64','arm64'}:raise ValueError('Unexpected signed inner artifact entries')
    result={}
    for arch in ('x64','arm64'):
        validate_payload(unsigned/arch,arch);entries=validate_payload(signed/arch,arch)
        old,new=inventory(unsigned/arch),inventory(signed/arch)
        if set(old)!=set(new):raise ValueError('Signing changed payload paths')
        for name in old:
            if name in OWNED:compare_signing(old[name],new[name])
            elif old[name]!=new[name]:raise ValueError('Signing altered third-party/support bytes')
        result[arch]=entries
    return result


def extract_final(unsigned,signed,payload,architecture,output):
    old,new=Path(unsigned).read_bytes(),Path(signed).read_bytes()
    compare_signing(old,new,outer=True)
    entries=validate_payload(Path(payload),architecture)
    decoded=decode(new,require_signed=True)
    actual,plugins=verify_payload(decoded,inventory(Path(payload)),architecture)
    output=Path(output)
    if output.exists() or output.is_symlink():raise ValueError('Extraction directory must be unused')
    output.mkdir(parents=True)
    for name,data in actual.items():
        path=output/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data)
    return dict(inner_files=entries,tooling_files=plugins)


def main():
    p=argparse.ArgumentParser(description=__doc__);s=p.add_subparsers(dest='command',required=True)
    inner=s.add_parser('inner');final=s.add_parser('final')
    for parser in (inner,final):
        for name in ('unsigned','signed','output'):parser.add_argument('--'+name,required=True)
    for name in ('payload','architecture','extract'):final.add_argument('--'+name,required=True)
    a=p.parse_args();out=Path(a.output)
    if out.exists() or out.is_symlink():raise ValueError('Refusing structure report overwrite')
    result=verify_inner(a.unsigned,a.signed) if a.command=='inner' else extract_final(a.unsigned,a.signed,a.payload,a.architecture,a.extract)
    out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')

if __name__=='__main__':main()
