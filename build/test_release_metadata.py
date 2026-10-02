"""Policy/byte-binding tests; constructed reports are not native trust evidence."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class MetadataTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.report = dict(schema_version=1, source_commit='a'*40, workflow_run_id='12', artifacts=[])
        for arch in ('x64', 'arm64'):
            name = f'AI-Desktop-Setup-1.2.3-{arch}.exe'
            data = b'final bytes after signing ' + arch.encode()
            (self.root/name).write_bytes(data)
            self.report['artifacts'].append(dict(name=name, sha256=hashlib.sha256(data).hexdigest(), product_name='AI Desktop Setup', product_version='1.2.3', architecture=arch, outer_signature_valid=True, inner_signatures_valid=True, timestamp_valid=True, inner_files=[dict(path=p, sha256='1'*64, ownership='owned', signature_valid=True) for p in ('AI.Desktop.Setup.exe','AI.Desktop.Setup.Core.dll')]))

    def write(self):
        file = ROOT/'build/write-release-metadata.py'
        self.assertTrue(file.exists(), 'final signed-byte metadata producer missing')
        spec=importlib.util.spec_from_file_location('metadata',file); mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
        return mod.write_metadata(self.root,self.report,'1.2.3','v1.2.3','a'*40,'example/ai-desktop-setup','12')

    def test_final_hash_uses_signed_bytes(self):
        result=self.write()
        for artifact in result['artifacts']:
            self.assertEqual(artifact['sha256'],hashlib.sha256((self.root/artifact['name']).read_bytes()).hexdigest())
            self.assertNotEqual(artifact['sha256'],hashlib.sha256(b'unsigned bytes').hexdigest())
            self.assertTrue(artifact['signed'])

    def test_missing_inner_signature_blocks_release(self):
        self.report['artifacts'][0]['inner_files'][0]['signature_valid']=False
        with self.assertRaises(ValueError): self.write()
        self.assertFalse((self.root/'release-manifest.json').exists())

    def test_wrong_product_or_architecture_rejected(self):
        original=copy.deepcopy(self.report)
        for field,value in [('product_name','Other'),('architecture','arm64'),('product_version','1.2.4'),('timestamp_valid',False),('outer_signature_valid',False)]:
            self.report=copy.deepcopy(original);self.report['artifacts'][0][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):self.write()

    def test_stale_report_rejected(self):
        (self.root/self.report['artifacts'][0]['name']).write_bytes(b'changed after verification')
        with self.assertRaises(ValueError):self.write()

    def test_missing_architecture_and_wrong_run_rejected(self):
        self.report['workflow_run_id']='11'
        with self.assertRaises(ValueError):self.write()
        self.report['workflow_run_id']='12';self.report['artifacts'].pop()
        with self.assertRaises(ValueError):self.write()

    def test_overwrite_rejected(self):
        self.write()
        with self.assertRaises(ValueError):self.write()

if __name__=='__main__':unittest.main()
