import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
class SignedPackageTests(unittest.TestCase):
    def module(self):
        path=ROOT/'build/package.py'
        self.assertTrue(path.exists(),'staged packaging boundary missing')
        spec=importlib.util.spec_from_file_location('staged_package',path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m

    def test_incomplete_payload_fails_packaging(self):
        m=self.module()
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(ValueError):m.validate_payload(Path(tmp),'x64')

    def test_third_party_never_enters_signing_allowlist(self):
        path=ROOT/'signing/inner.xml'
        self.assertTrue(path.exists(),'inner signing allowlist missing')
        tree=ET.parse(path);ns={'s':'http://signpath.io/artifact-configuration/v1'}
        paths={p.attrib['path'] for p in tree.findall('.//s:pe-file',ns)}
        self.assertEqual(paths,{a+'/'+p for a in ('x64','arm64') for p in ('AI.Desktop.Setup.exe','AI.Desktop.Setup.Core.dll')})
        self.assertEqual(len(tree.findall('.//s:authenticode-sign',ns)),4)

    def test_windows_path_aliases_rejected(self):
        m=self.module()
        for path in ('../x','/x','x\\y','C:x','x:stream','x.','x ','NUL','a/COM1.txt','a//b','a/./b','a/{var:0}','a\nb','a\tb'):
            with self.subTest(path=path),self.assertRaises(ValueError):m.safe_path(path)

if __name__=='__main__':unittest.main()
