"""Strict PE/certificate framing and allowed signing mutations. Never verifies trust."""
import struct


def framing(data, require_signed=False, outer=True):
    def get(fmt, offset):
        if offset < 0 or offset+struct.calcsize(fmt)>len(data):raise ValueError('Truncated PE')
        return struct.unpack_from(fmt,data,offset)
    if data[:2]!=b'MZ':raise ValueError('Not PE')
    pe=get('<I',60)[0]
    if pe<64 or data[pe:pe+4]!=b'PE\0\0':raise ValueError('Invalid PE header')
    machine,sections=get('<HH',pe+4);optional=pe+24;size=get('<H',pe+20)[0]
    magic=get('<H',optional)[0]
    if magic not in (0x10b,0x20b) or not 1<=sections<=96:raise ValueError('Unsupported PE profile')
    if outer and (machine!=0x14c or magic!=0x10b):raise ValueError('Wrong NSIS stub architecture')
    dirs=96 if magic==0x10b else 112
    count=get('<I',optional+dirs-4)[0]
    if count<5 or count>16 or size<dirs+count*8:raise ValueError('Optional header directory bounds')
    get(f'<{size}s',optional)
    checksum=optional+64;security=optional+dirs+32
    if outer and max(checksum+4,security+8)>512:raise ValueError('Signing fields overlap NSIS CRC coverage')
    offset,length=get('<II',security);headers=get('<I',optional+60)[0]
    section_start=optional+size
    if headers<section_start+sections*40 or headers>len(data):raise ValueError('Invalid header size')
    ranges=[]
    for i in range(sections):
        at=section_start+i*40;get('<40s',at);n,p=get('<II',at+16)
        if n:
            if p<headers or p+n>len(data):raise ValueError('Invalid PE section bounds')
            if any(p<b and p+n>a for a,b in ranges):raise ValueError('Overlapping PE sections')
            ranges.append((p,p+n))
    if bool(offset)!=bool(length):raise ValueError('Half-empty certificate directory')
    count=0
    if length:
        if offset%8 or offset<headers or offset+length!=len(data) or any(b>offset for a,b in ranges):raise ValueError('Certificate bounds')
        pos=offset
        while pos<offset+length:
            n,revision,kind=get('<IHH',pos)
            end=(pos+n+7)&~7
            if n<8 or pos+n>offset+length or end>offset+length or revision!=0x200 or kind!=2 or any(data[pos+n:end]):raise ValueError('Invalid WIN_CERTIFICATE')
            count+=1;pos=end
        if pos!=offset+length or count!=1:raise ValueError('Expected exactly one PKCS SignedData envelope')
    elif require_signed:raise ValueError('Embedded signature required')
    return dict(machine=machine,checksum=checksum,security=security,certificate_offset=offset,certificate_size=length,certificate_count=count,section_end=max([headers]+[b for a,b in ranges]))


def compare_signing(unsigned,signed,outer=False):
    before=framing(unsigned,outer=outer);after=framing(signed,require_signed=True,outer=outer)
    if before['certificate_size'] or after['certificate_offset']!=(len(unsigned)+7)&~7:raise ValueError('Not an unsigned-to-signed transition')
    if before['checksum']!=after['checksum'] or before['security']!=after['security']:raise ValueError('PE layout changed')
    normalized=bytearray(signed[:len(unsigned)])
    for start,size in ((before['checksum'],4),(before['security'],8)):
        normalized[start:start+size]=unsigned[start:start+size]
    if normalized!=unsigned or any(signed[len(unsigned):after['certificate_offset']]):raise ValueError('Signing modified non-signature bytes')
    return after
