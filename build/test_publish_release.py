import importlib.util
from pathlib import Path
import tempfile
import unittest

class PublicationTests(unittest.TestCase):
    def module(self):
        p=Path(__file__).with_name('publish-release.py');self.assertTrue(p.exists(),'immutable draft publication gate missing')
        s=importlib.util.spec_from_file_location('publish_release',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
    def test_disabled_or_unavailable_immutable_setting_rejected(self):
        m=self.module()
        for response in ({},{'enabled':False},{'enabled':'true'},{'enabled':1}):
            with self.subTest(response=response),self.assertRaises(ValueError):m.require_immutable(response)
        m.require_immutable({'enabled':True})
    def test_partial_changed_extra_or_duplicate_draft_assets_rejected(self):
        m=self.module();expected={'a.exe':b'signed-a','b.exe':b'signed-b'}
        import hashlib
        assets=[dict(name=n,size=len(b),digest='sha256:'+hashlib.sha256(b).hexdigest()) for n,b in expected.items()]
        m.validate_assets(assets,expected)
        for altered in (assets[:1],assets+assets[:1],[dict(assets[0],digest='sha256:'+'0'*64),assets[1]],[dict(assets[0],name='extra.exe'),assets[1]]):
            with self.assertRaises(ValueError):m.validate_assets(altered,expected)
    def test_missing_or_changed_provenance_bundle_rejected_before_network(self):
        m=self.module()
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);(p/'bundle').write_bytes(b'changed')
            with self.assertRaises(ValueError):m.publish(p,p/'bundle','0'*64,'example/repo','v1.2.3','a'*40)
