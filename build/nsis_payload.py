"""Restricted NSIS 3.12 Unicode /SOLID LZMA reader; never executes an installer.
Format: nsis-dev/nsis e3f60402bcdf7be822d159b531c6e38ddf32de12,
Source/exehead/fileform.{h,c}, exec.c. Unsupported profiles fail closed.
"""
import hashlib
import lzma
import struct
import zlib
import json
from pathlib import Path
from signed_pe import framing


def decode(data, maximum=128*1024*1024, require_signed=False):
    pe_info = framing(data, require_signed=require_signed)
    def u32(blob, p):
        if p < 0 or p + 4 > len(blob):
            raise ValueError('truncated NSIS')
        return struct.unpack_from('<I', blob, p)[0]
    def signed(blob, p):
        return struct.unpack_from('<i', blob, p)[0]
    if data[:2] != b'MZ':
        raise ValueError('not PE')
    pe = u32(data, 0x3c)
    if data[pe:pe+4] != b'PE\0\0' or struct.unpack_from('<H', data, pe+4)[0] != 0x14c:
        raise ValueError('NSIS stub profile')
    optional = pe + 24
    if struct.unpack_from('<H', data, optional)[0] != 0x10b:
        raise ValueError('NSIS stub profile')
    cert_offset, cert_size = struct.unpack_from('<II', data, optional+128)
    end = len(data)
    if cert_size:
        if cert_offset+cert_size != len(data) or cert_offset % 8:
            raise ValueError('certificate bounds')
        end = cert_offset
    candidates = []
    for offset in range(512, end-28, 512):
        if data[offset+4:offset+20] == struct.pack('<4I',0xdeadbeef,0x6c6c754e,0x74666f73,0x74736e49):
            candidates.append(offset)
    if len(candidates) != 1:
        raise ValueError('NSIS header absent or ambiguous')
    offset = candidates[0]
    flags, header_size, total = u32(data, offset), u32(data,offset+20), u32(data,offset+24)
    if flags != 10 or header_size > maximum or total < 37 or offset+total > end:
        raise ValueError('NSIS header profile')
    if pe_info['section_end'] > offset:
        raise ValueError('NSIS stream overlaps PE sections')
    if cert_size and cert_offset != (offset+total+7)&~7:
        raise ValueError('Certificate alignment mismatch')
    # Signing may add zero alignment before WIN_CERTIFICATE, never arbitrary data.
    trailing = data[offset+total:end]
    if (not cert_size and trailing) or len(trailing)>7 or any(trailing):
        raise ValueError('unaccounted overlay')
    if zlib.crc32(data[512:offset+total-4]) != u32(data,offset+total-4):
        raise ValueError('NSIS CRC')
    compressed = data[offset+28:offset+total-4]
    if compressed[:5] != b'\x5d\x00\x00\x80\x00':
        raise ValueError('unsupported LZMA properties')
    decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[dict(id=lzma.FILTER_LZMA1,dict_size=8388608,lc=3,lp=0,pb=2)])
    try:
        unpacked = decoder.decompress(compressed[5:], max_length=maximum+1)
    except lzma.LZMAError as exc:
        raise ValueError('Malformed NSIS LZMA') from exc
    if len(unpacked)>maximum or not decoder.eof or decoder.unused_data:
        raise ValueError('LZMA bounds/termination')
    if u32(unpacked,0) != header_size or len(unpacked)<4+header_size:
        raise ValueError('NSIS header length')
    header = unpacked[4:4+header_size]
    if len(header)<300:raise ValueError('Truncated fixed header')
    blocks = [struct.unpack_from('<II',header,4+i*8) for i in range(8)]
    # Stock 32-bit silent Unicode profile: no UI pages, ctlcolors or bgfont.
    pages, sections, entries, strings, languages, colors, font, datablock = blocks
    if pages!=(300,0) or sections[0]!=300 or colors!=(header_size,0) or font!=(0,0) or datablock!=(0,0):
        raise ValueError('unsupported NSIS block profile')
    if sections[1]<1 or entries[1]<1 or strings[1]!=0 or languages[1]<1:
        raise ValueError('NSIS block counts')
    if sections[0]+sections[1]*2072!=entries[0] or entries[0]+entries[1]*28!=strings[0] or not(strings[0] <= languages[0] <= header_size):
        raise ValueError('NSIS block bounds')
    language_size=u32(header,100)
    if language_size<10 or (language_size-10)%4 or languages[0]+language_size*languages[1]!=header_size:
        raise ValueError('NSIS language table')
    rawstrings=header[strings[0]:languages[0]]
    if len(rawstrings)%2 or rawstrings[:2]!=b'\0\0' or rawstrings[-2:]!=b'\0\0':
        raise ValueError('NSIS Unicode strings')
    units=list(struct.unpack('<'+'H'*(len(rawstrings)//2),rawstrings))
    language_values=[]
    for i in range(languages[1]):
        at=languages[0]+i*language_size
        language_values.append([signed(header,j) for j in range(at+10,at+language_size,4)])
    cache={}
    def string(index, stack=()):
        if index < 0:
            key=-index-1
            if key>=len(language_values[0]) or index in stack:
                raise ValueError('NSIS language reference')
            return '{lang:'+'|'.join(string(row[key],stack+(index,)) for row in language_values)+'}'
        if index in cache:
            return cache[index]
        if index>=len(units):
            raise ValueError('NSIS string reference')
        out=[]; literal=[]; pos=index
        def flush():
            if literal:
                out.append(struct.pack('<'+'H'*len(literal),*literal).decode('utf-16-le',errors='strict'));literal.clear()
        while pos<len(units) and units[pos]:
            code=units[pos];pos+=1
            if code<=4:
                flush()
                if pos>=len(units) or units[pos]==0:
                    raise ValueError('NSIS string control')
                value=units[pos];pos+=1
                if code==4:
                    literal.append(value)
                elif code==1:
                    ref=((value>>8)&127)*128+(value&127)
                    out.append(string(-ref-1,stack+(index,)))
                elif code==2:
                    out.append('{shell:%04x}'%value)
                else:
                    ref=((value>>8)&127)*128+(value&127)
                    out.append('{var:%d}'%ref)
            else:literal.append(code)
        if pos>=len(units):raise ValueError('unterminated NSIS string')
        flush();cache[index]=''.join(out);return cache[index]
    # Decode every string, including unreferenced entries and every language slot.
    start=0
    for pos,value in enumerate(units):
        if value==0:
            string(start);start=pos+1
    for row in language_values:
        for index in row:string(index)
    # Validate and preserve every length-prefixed data record, referenced or not.
    rawdata=unpacked[4+header_size:];records=[];by_offset={};pos=0
    while pos<len(rawdata):
        length=u32(rawdata,pos)
        if length>maximum or pos+4+length>len(rawdata):raise ValueError('NSIS data bounds')
        payload=rawdata[pos+4:pos+4+length]
        record=dict(offset=pos,bytes=length,sha256=hashlib.sha256(payload).hexdigest(),data=payload)
        records.append(record);by_offset[pos]=record;pos+=4+length
    # Operand positions containing strings for the stock profile actually used by
    # this wrapper. All other opcodes fail rather than guessing their semantics.
    string_operands={1:(),2:(),4:(),5:(),11:(0,),13:(1,),14:(),19:(1,),20:(1,5),21:(0,),22:(1,),25:(1,2,3),26:(0,1),28:(0,1),29:(1,2),31:(),41:(0,),44:(0,1,2),52:(2,3),65:()}
    normalized=bytearray(header)
    files=[]; instructions=[];output='{outdir}'
    for i in range(entries[1]):
        row=struct.unpack_from('<7i',header,entries[0]+i*28);opcode=row[0];a=row[1:]
        if opcode not in string_operands:raise ValueError('unsupported NSIS opcode')
        for j in string_operands[opcode]:string(a[j])
        # Stack push uses a string; pop/exchange operands are integer variables.
        if opcode==31 and a[1]==0 and a[2]==0:string(a[0])
        if opcode==11 and a[1]:output=string(a[0])
        if opcode==20:
            # Only data-record offsets and FILETIME words vary with signed inputs.
            struct.pack_into('<3i',normalized,entries[0]+i*28+12,0,0,0)
            if a[2] not in by_offset:raise ValueError('NSIS file record reference')
            name=string(a[1]);r=by_offset[a[2]]
            files.append(dict(instruction=i,name=name,destination=output+'\\'+name,offset=a[2],sha256=r['sha256']))
        instructions.append(dict(opcode=opcode,operands=a))
    for i in range(sections[1]):
        at=sections[0]+i*2072
        struct.pack_into('<I',normalized,at+20,0) # display-only unpacked KiB
        string(signed(header,at))
        code,size=u32(header,at+12),u32(header,at+16)
        if code+size>entries[1]:raise ValueError('NSIS section code range')
        name=header[at+24:at+2072].decode('utf-16-le',errors='strict')
        if '\0' not in name:raise ValueError('NSIS section name')
        cache[-100000-i]=name
    # Preserve tables for independent audit; the profile checks all code/strings.
    spans=[('fixed',0,300),('sections',sections[0],entries[0]),('entries',entries[0],strings[0]),('strings',strings[0],languages[0]),('languages',languages[0],header_size)]
    return dict(strings=list(cache.values()),records=records,files=files,instructions=instructions,
                tables=[dict(name=n,data=header[a:b],sha256=hashlib.sha256(header[a:b]).hexdigest()) for n,a,b in spans],
                offset=offset,end=offset+total,crc_valid=True,pe=pe_info,
                profile_sha256=hashlib.sha256(normalized).hexdigest(),
                section_sizes=[u32(header,sections[0]+i*2072+20) for i in range(sections[1])])


def verify_payload(decoded, expected, architecture):
    """Fixed reviewed wrapper profile, full file path/record/byte classification."""
    profile=json.loads(Path(__file__).with_name('wrapper-profile.json').read_text())
    if hashlib.sha256((Path(__file__).resolve().parents[1]/'packaging/launcher.nsi').read_bytes()).hexdigest()!=profile['wrapper_sha256']:
        raise ValueError('Wrapper source changed without profile review')
    lock=json.loads(Path(__file__).with_name('toolchain.json').read_text())['nsis']
    if any(profile[key]!=lock[field] for key,field in [('nsis_version','version'),('nsis_source_commit','source_commit'),('nsis_archive_sha256','sha256'),('nsis_source_sha256','source_sha256')]):
        raise ValueError('NSIS toolchain changed without profile review')
    selected=profile['architectures'].get(architecture)
    if not selected or decoded['profile_sha256']!=selected['header_sha256']:
        raise ValueError('Unreviewed NSIS code, strings, control flow or file region')
    records={r['offset']:r for r in decoded['records']};used=set();actual={};plugins=[]
    # Exact normalized header pins every instruction operand except data locations
    # and times. File names/destinations, section/function boundaries, jumps,
    # PayloadPath assignment, launch and plugin calls are therefore immutable.
    if len(decoded['files'])!=len(selected['files']):raise ValueError('File region changed')
    for item,rule in zip(decoded['files'],selected['files']):
        if {k:item[k] for k in ('instruction','name','destination')}!={k:rule[k] for k in ('instruction','name','destination')}:
            raise ValueError('File destination changed')
        record=records[item['offset']];used.add(item['offset'])
        if rule['ownership']=='tooling':
            if record['sha256']!=profile['system_plugin_sha256']:raise ValueError('Unreviewed NSIS plugin')
            plugins.append(dict(path='NSIS/System.dll',sha256=record['sha256'],ownership='third_party'))
        else:
            path=rule['path']
            if path in actual or path not in expected or record['data']!=expected[path]:raise ValueError('Embedded payload mismatch')
            actual[path]=record['data']
    if set(actual)!=set(expected) or used!=set(records):raise ValueError('Missing, extra or unclassified NSIS records')
    # NSIS script.cpp add_file uses (len+1023)/1024 for ordinary File.
    # Plugin File emissions use generatecode=2, so do not add section size.
    if decoded['section_sizes'] != [sum((len(b)+1023)//1024 for b in actual.values())]:
        raise ValueError('Section disk-space estimate differs from payload')
    return actual,plugins[:1]
