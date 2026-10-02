import hashlib
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

class NsisTests(unittest.TestCase):
    def setUp(self):
        path=Path(__file__).with_name('nsis_payload.py');self.assertTrue(path.exists(),'signed NSIS decoder missing')
        s=importlib.util.spec_from_file_location('nsis_payload',path);self.m=importlib.util.module_from_spec(s);s.loader.exec_module(self.m)
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
        self.compiler=os.environ.get('AI_SETUP_MAKENSIS','makensis')
        if not shutil.which(self.compiler):self.skipTest('NSIS 3.12 required')
    def compile(self):
        (self.root/'payload.txt').write_bytes(b'fixture payload')
        (self.root/'fixture.nsi').write_text('''Unicode true
SilentInstall silent
RequestExecutionLevel user
CRCCheck force
SetCompressor /SOLID lzma
SetCompressorDictSize 8
Name "Fixture"
OutFile "fixture.exe"
Section
SetOutPath "$TEMP"
File "payload.txt"
SectionEnd
''')
        prefix='/' if os.name=='nt' else '-'
        subprocess.run([self.compiler,prefix+'NOCONFIG',prefix+'V1',str(self.root/'fixture.nsi')],cwd=self.root,check=True,capture_output=True)
        return (self.root/'fixture.exe').read_bytes()
    def test_real_nsis_structural_decode(self):
        d=self.m.decode(self.compile());self.assertEqual([r['data'] for r in d['records']],[b'fixture payload'])
        self.assertNotIn('signature_valid',d)
    def test_corrupt_truncated_overlay_and_limit_rejected(self):
        d=self.compile();changed=bytearray(d);changed[-8]^=1
        for data in (d[:-1],bytes(changed),d+b'overlay'):
            with self.assertRaises(ValueError):self.m.decode(data)
        with self.assertRaises(ValueError):self.m.decode(d,maximum=16)
    def test_unsigned_and_unreviewed_wrapper_cannot_be_release(self):
        d=self.compile()
        with self.assertRaises(ValueError):self.m.decode(d,require_signed=True)
        with self.assertRaises(ValueError):self.m.verify_payload(self.m.decode(d),{'payload.txt':b'fixture payload'},'x64')

class WrapperContractTests(NsisTests):
    def wrapper(self,architecture='x64',alter=''):
        import json
        root=Path(__file__).resolve().parents[1]
        names=['AI.Desktop.Setup.exe','AI.Desktop.Setup.Core.dll','AI.Desktop.Setup.exe.config','LICENSE.txt','THIRD-PARTY-NOTICES.md','licenses/dotnet/LICENSE.txt','licenses/dotnet/SOURCE.md','licenses/NSIS/COPYING','licenses/NSIS/SOURCE.md']
        names+=list(json.loads((root/'build/runtime-inventory.json').read_text()))
        names+=['licenses/'+n+'/THIRD-PARTY-NOTICES.TXT' for n in ('Microsoft.Bcl.AsyncInterfaces','System.IO.Pipelines','System.Text.Encodings.Web','System.Text.Json')]
        expected={n:('fixture '+n).encode() for n in names};include=['SetDateSave off']
        for name in sorted(expected):
            p=self.root/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(expected[name])
            parent=Path(name).parent.as_posix();dest='$PayloadPath'+('\\'+parent.replace('/','\\') if parent!='.' else '')
            include.extend([f'SetOutPath "{dest}"',f'File "{p.as_posix()}"'])
        inc=self.root/'payload.nsh';inc.write_text('\n'.join(include)+'\n')
        wrapper=root/'packaging/launcher.nsi'
        if alter:
            source=wrapper.read_text().replace('Icon "../App/Assets/app.ico"',f'Icon "{root.as_posix()}/App/Assets/app.ico"')
            source=source.replace('StrCpy $PayloadPath "$PLUGINSDIR\\app"',alter)
            wrapper=self.root/'changed.nsi';wrapper.write_text(source)
        target=self.root/(architecture+'.exe');prefix='/' if os.name=='nt' else '-'
        subprocess.run([self.compiler,prefix+'NOCONFIG',prefix+'V1',*[prefix+'D'+x for x in ('PAYLOAD_DIR='+str(self.root),'PAYLOAD_INCLUDE='+str(inc),'OUTPUT_FILE='+str(target),'VERSION=1.2.3','ARCH='+architecture)],str(wrapper)],check=True,capture_output=True)
        return self.m.decode(target.read_bytes()),expected

    def test_both_architectures_extract_complete_exact_bytes(self):
        for arch in ('x64','arm64'):
            d,expected=self.wrapper(arch)
            actual,plugins=self.m.verify_payload(d,expected,arch)
            self.assertEqual(actual,expected);self.assertEqual(len(actual),22)
            self.assertEqual(plugins[0]['ownership'],'third_party')

    def test_changed_payload_missing_extra_or_unclassified_records_rejected(self):
        import copy
        d,expected=self.wrapper()
        for mutation in ('changed','missing','extra','unclassified','plugin'):
            parsed=copy.deepcopy(d);wanted=dict(expected)
            if mutation=='changed':wanted['AI.Desktop.Setup.exe']=b'changed'
            elif mutation=='missing':wanted.pop('LICENSE.txt')
            elif mutation=='extra':wanted['extra.txt']=b'extra'
            elif mutation=='unclassified':parsed['records'].append(dict(offset=99999999,data=b'extra',bytes=5,sha256=hashlib.sha256(b'extra').hexdigest()))
            else:parsed['records'][0]['sha256']='0'*64
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):self.m.verify_payload(parsed,wanted,'x64')

    def test_changed_assignment_and_wrong_architecture_rejected(self):
        d,expected=self.wrapper(alter='StrCpy $PayloadPath "$TEMP\\app"')
        with self.assertRaises(ValueError):self.m.verify_payload(d,expected,'x64')
        d,expected=self.wrapper()
        with self.assertRaises(ValueError):self.m.verify_payload(d,expected,'arm64')

    def test_forged_section_disk_estimate_rejected(self):
        d,expected=self.wrapper();d['section_sizes']=[0]
        with self.assertRaises(ValueError):self.m.verify_payload(d,expected,'x64')
