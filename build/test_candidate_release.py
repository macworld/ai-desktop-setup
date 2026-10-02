import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

class CandidateReleaseTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
    def module(self):
        p=Path(__file__).with_name('release-candidate.py');self.assertTrue(p.exists(),'candidate acceptance binding missing')
        s=importlib.util.spec_from_file_location('release_candidate',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
    def test_acceptance_rejects_changed_added_and_missing_bytes(self):
        m=self.module();(self.root/'a.exe').write_bytes(b'final')
        digest=m.seal(self.root,'a'*40,'12','1')
        m.check(self.root,digest,'a'*40,'12','1')
        for change in ('changed','added','missing'):
            with self.subTest(change=change):
                (self.root/'a.exe').write_bytes(b'final')
                if change=='changed':(self.root/'a.exe').write_bytes(b'changed')
                elif change=='added':(self.root/'extra').write_bytes(b'extra')
                else:(self.root/'a.exe').unlink()
                with self.assertRaises(ValueError):m.check(self.root,digest,'a'*40,'12','1')
                (self.root/'extra').unlink(missing_ok=True)
    def test_acceptance_rejects_other_commit_run_attempt_and_index(self):
        m=self.module();(self.root/'a.exe').write_bytes(b'final');digest=m.seal(self.root,'a'*40,'12','1')
        for sha,run,attempt,expected in [('b'*40,'12','1',digest),('a'*40,'13','1',digest),('a'*40,'12','2',digest),('a'*40,'12','1','0'*64)]:
            with self.assertRaises(ValueError):m.check(self.root,expected,sha,run,attempt)
