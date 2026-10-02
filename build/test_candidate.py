import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('candidate', Path(__file__).with_name('candidate.py'))


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def module(self):
        self.assertTrue(Path(SPEC.origin).exists(), 'candidate boundary is not implemented')
        module = importlib.util.module_from_spec(SPEC)
        SPEC.loader.exec_module(module)
        return module

    def test_stage_uses_exact_commit_not_ignored_or_dirty_files(self):
        mod = self.module()
        source = self.root / 'repo'
        source.mkdir()
        def git(*args):
            return subprocess.check_output(['git', '-C', str(source), *args], stderr=subprocess.DEVNULL)
        git('init', '-q')
        git('config', 'user.name', 'Fixture')
        git('config', 'user.email', 'fixture@example.invalid')
        (source / 'README').write_text('committed')
        (source / '.gitignore').write_text('obj/\n')
        git('add', '.')
        git('commit', '-qm', 'fixture')
        commit = git('rev-parse', 'HEAD').decode().strip()
        (source / 'README').write_text('dirty')
        (source / 'obj').mkdir()
        (source / 'obj/cache').write_text('excluded')
        mod.stage(source, self.root / 'stage', commit)
        self.assertEqual((self.root / 'stage/README').read_text(), 'committed')
        self.assertFalse((self.root / 'stage/obj').exists())
        with self.assertRaises(ValueError):
            mod.stage(source, self.root / 'stage', commit)

    def test_inventory_includes_ignored_intermediates_and_detects_mutation(self):
        mod = self.module()
        (self.root / 'obj').mkdir()
        (self.root / 'obj/test.pdb').write_bytes(b'abc')
        inventory = mod.inventory(self.root)
        self.assertEqual(inventory, [{'path': 'obj/test.pdb', 'bytes': 3,
            'sha256': 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'}])
        (self.root / 'obj/test.pdb').write_bytes(b'changed')
        self.assertNotEqual(mod.inventory(self.root), inventory)

    def test_inventory_rejects_symlink(self):
        mod = self.module()
        try:
            (self.root / 'link').symlink_to(self.root / 'target')
        except OSError:
            self.skipTest('symlink permission unavailable')
        with self.assertRaises(ValueError):
            mod.inventory(self.root)

    def test_trx_allows_only_named_package_gates_and_retains_evidence(self):
        mod = self.module()
        self.assertTrue(hasattr(mod, 'trx_report'), 'structured TRX gate missing')
        def write(names, counts):
            path = self.root / 'test.trx'
            results = ''.join('<UnitTestResult testId="%s" testName="%s" outcome="%s"/>' % (i,n,o) for i,(n,o) in enumerate(names))
            definitions = ''.join('<UnitTest id="%s" name="%s"/>' % (i,n) for i,(n,o) in enumerate(names))
            path.write_text('<TestRun><Results>'+results+'</Results><TestDefinitions>'+definitions+'</TestDefinitions><ResultSummary><Counters '+counts+'/></ResultSummary></TestRun>')
            return path
        optional = 'AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageRequiresNativeTrustAndPinsBytes'
        p = write([('OrdinaryTest','Passed'),(optional,'NotExecuted')], 'total="2" executed="1" passed="1" failed="0" notExecuted="1"')
        result = mod.trx_report(p)
        self.assertEqual(result['skipped'], [optional])
        self.assertEqual(result['executed'], 1)
        for names,counts in [([('OrdinaryTest','Failed')], 'total="1" executed="1" passed="0" failed="1" notExecuted="0"'),
                             ([('Unexpected','NotExecuted'),('OrdinaryTest','Passed')], 'total="2" executed="1" passed="1" failed="0" notExecuted="1"'),
                             ([(optional,'NotExecuted')], 'total="1" executed="0" passed="0" failed="0" notExecuted="1"'),
                             ([('OrdinaryTest','Passed')], 'total="2" executed="1" passed="1" failed="0" notExecuted="1"')]:
            with self.assertRaises(ValueError): mod.trx_report(write(names, counts))


if __name__ == '__main__':
    unittest.main()
