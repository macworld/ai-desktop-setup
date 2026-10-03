import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

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

    # Counter values observed from a three-test run with locked SDK 10.0.401
    # (VSTest 18.7.0), Test.Sdk 17.14.1, and xunit.runner.visualstudio 3.1.5.
    # VSTest leaves notExecuted (and every auxiliary counter) zero even
    # when individual results are NotExecuted. No host/run metadata retained.
    TRX_COUNTS = dict(total='3', executed='1', passed='1', failed='0',
        error='0', timeout='0', aborted='0', inconclusive='0',
        passedButRunAborted='0', notRunnable='0', notExecuted='0',
        disconnected='0', warning='0', completed='0', inProgress='0', pending='0')
    TRX_RESULTS = [
        ('AiDesktopSetup.Tests.PackagePolicyTests.EmptyMirrorsUsesOfficialSource', 'Passed'),
        ('AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageRequiresNativeTrustAndPinsBytes', 'NotExecuted'),
        ('AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust', 'NotExecuted'),
    ]

    def write_trx(self, results=None, counts=None):
        tree = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        result_nodes = ET.SubElement(tree, 'Results')
        definition_nodes = ET.SubElement(tree, 'TestDefinitions')
        entry_nodes = ET.SubElement(tree, 'TestEntries')
        for i, (name, outcome) in enumerate(self.TRX_RESULTS if results is None else results):
            test_id = '00000000-0000-0000-0000-%012d' % i
            execution_id = '00000000-0000-0000-0001-%012d' % i
            ET.SubElement(result_nodes, 'UnitTestResult', testId=test_id,
                executionId=execution_id, testName=name, outcome=outcome)
            definition = ET.SubElement(definition_nodes, 'UnitTest', id=test_id, name=name)
            ET.SubElement(definition, 'Execution', id=execution_id)
            class_name, _, method_name = name.split('(', 1)[0].rpartition('.')
            ET.SubElement(definition, 'TestMethod', className=class_name, name=method_name)
            ET.SubElement(entry_nodes, 'TestEntry', testId=test_id, executionId=execution_id)
        summary = ET.SubElement(tree, 'ResultSummary', outcome='Completed')
        ET.SubElement(summary, 'Counters', self.TRX_COUNTS | (counts or {}))
        path = self.root / 'test.trx'
        ET.ElementTree(tree).write(path, encoding='utf-8', xml_declaration=True)
        return path

    def write_theory_trx(self):
        # The full 429-result report has runtime-enumerated MemberData rows:
        # one canonical method definition/testId, distinct executionId/display
        # per row, and one matching TestEntry per execution. Neutral values only.
        path = self.write_trx([
            ('Fixture.Theories.MemberRows(value: 1)', 'Passed'),
            ('Fixture.Theories.MemberRows(value: 2)', 'Passed'),
        ], {'total':'2', 'executed':'2', 'passed':'2'})
        tree = ET.parse(path)
        results = next(n for n in tree.iter() if n.tag.endswith('Results'))
        definitions = next(n for n in tree.iter() if n.tag.endswith('TestDefinitions'))
        entries = next(n for n in tree.iter() if n.tag.endswith('TestEntries'))
        results[1].set('testId', results[0].get('testId'))
        entries[1].set('testId', results[0].get('testId'))
        definitions.remove(definitions[1])
        definitions[0].set('name', 'Fixture.Theories.MemberRows')
        tree.write(path)
        return path

    def test_trx_accepts_runtime_theory_rows_sharing_one_method_definition(self):
        result = self.module().trx_report(self.write_theory_trx())
        self.assertEqual(result, dict(total=2, executed=2, passed=2,
            skipped=[], unverified_native_gates=[]))

    def test_trx_rejects_failed_skipped_or_unknown_runtime_theory_row(self):
        for outcome in ('Failed', 'NotExecuted', 'Unknown'):
            with self.subTest(outcome=outcome):
                path = self.write_theory_trx()
                tree = ET.parse(path)
                results = next(n for n in tree.iter() if n.tag.endswith('Results'))
                results[1].set('outcome', outcome)
                tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)

    def test_trx_rejects_duplicate_missing_or_mismatched_execution_entries(self):
        for mutation in ('duplicate-execution', 'missing-execution', 'duplicate-entry',
                         'missing-entry', 'wrong-entry-id', 'wrong-entry-execution',
                         'wrong-definition-execution'):
            with self.subTest(mutation=mutation):
                path = self.write_trx([('Fixture.One', 'Passed'), ('Fixture.Two', 'Passed')],
                    {'total':'2', 'executed':'2', 'passed':'2'})
                tree = ET.parse(path)
                results = next(n for n in tree.iter() if n.tag.endswith('Results'))
                entries = next(n for n in tree.iter() if n.tag.endswith('TestEntries'))
                if mutation == 'duplicate-execution':
                    results[1].set('executionId', results[0].get('executionId'))
                elif mutation == 'missing-execution':
                    del results[1].attrib['executionId']
                elif mutation == 'duplicate-entry':
                    entries[1].set('executionId', entries[0].get('executionId'))
                elif mutation == 'missing-entry':
                    entries.remove(entries[1])
                elif mutation == 'wrong-entry-id':
                    entries[1].set('testId', 'unmatched-method-id')
                elif mutation == 'wrong-entry-execution':
                    entries[1].set('executionId', 'unmatched-execution-id')
                else:
                    next(n for n in tree.iter() if n.tag.endswith('Execution')).set('id', 'unmatched-execution-id')
                tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)

    def test_trx_rejects_theory_name_method_or_definition_mismatch(self):
        for mutation in ('wrong-row-name', 'duplicate-row-name', 'wrong-method-class',
                         'missing-method-name', 'duplicate-definition'):
            with self.subTest(mutation=mutation):
                path = self.write_theory_trx()
                tree = ET.parse(path)
                results = next(n for n in tree.iter() if n.tag.endswith('Results'))
                definitions = next(n for n in tree.iter() if n.tag.endswith('TestDefinitions'))
                method = next(n for n in tree.iter() if n.tag.endswith('TestMethod'))
                if mutation == 'wrong-row-name':
                    results[1].set('testName', 'Fixture.Other.MemberRows(value: 2)')
                elif mutation == 'duplicate-row-name':
                    results[1].set('testName', results[0].get('testName'))
                elif mutation == 'wrong-method-class':
                    method.set('className', 'Fixture.Other')
                elif mutation == 'missing-method-name':
                    del method.attrib['name']
                else:
                    definitions.append(ET.fromstring(ET.tostring(definitions[0])))
                tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)

    def test_trx_rejects_duplicate_allowed_skip_with_distinct_executions(self):
        optional = self.TRX_RESULTS[1]
        for shared_id in (False, True):
            with self.subTest(shared_id=shared_id):
                path = self.write_trx([('Fixture.OrdinaryTest', 'Passed'), optional, optional])
                if shared_id:
                    tree = ET.parse(path)
                    results = next(n for n in tree.iter() if n.tag.endswith('Results'))
                    definitions = next(n for n in tree.iter() if n.tag.endswith('TestDefinitions'))
                    entries = next(n for n in tree.iter() if n.tag.endswith('TestEntries'))
                    results[2].set('testId', results[1].get('testId'))
                    entries[2].set('testId', results[1].get('testId'))
                    definitions.remove(definitions[2])
                    tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)

    def test_trx_accepts_authentic_vstest_skip_counters_and_retains_evidence(self):
        result = self.module().trx_report(self.write_trx())
        skipped = [
            'AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageRequiresNativeTrustAndPinsBytes',
            'AiDesktopSetup.Tests.NativePackageTrustTests.ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust',
        ]
        self.assertEqual(result, dict(total=3, executed=1, passed=1,
            skipped=skipped, unverified_native_gates=skipped))

    def test_trx_accepts_nonempty_all_passed_run(self):
        result = self.module().trx_report(self.write_trx([('Fixture.OrdinaryTest', 'Passed')], {'total':'1'}))
        self.assertEqual(result, dict(total=1, executed=1, passed=1, skipped=[], unverified_native_gates=[]))

    def test_trx_accepts_either_named_skip_individually(self):
        for optional in self.TRX_RESULTS[1:]:
            with self.subTest(skip=optional[0]):
                result = self.module().trx_report(self.write_trx(
                    [('Fixture.OrdinaryTest', 'Passed'), optional], {'total':'2'}))
                self.assertEqual(result['skipped'], [optional[0]])
                self.assertEqual(result['executed'], 1)

    def test_trx_rejects_counters_that_disagree_with_individual_outcomes(self):
        for counts in ({'executed':'2', 'passed':'2'}, {'notExecuted':'2'}):
            with self.subTest(counts=counts), self.assertRaises(ValueError):
                self.module().trx_report(self.write_trx(counts=counts))

    def test_trx_rejects_inconsistent_primary_and_auxiliary_counters(self):
        mod = self.module()
        mutations = {'total':'4', 'executed':'3', 'passed':'3', 'failed':'1',
            'notExecuted':'2'}
        mutations.update({key:'1' for key in ('error', 'timeout', 'aborted',
            'inconclusive', 'passedButRunAborted', 'notRunnable', 'disconnected',
            'warning', 'completed', 'inProgress', 'pending')})
        for key, value in mutations.items():
            with self.subTest(counter=key), self.assertRaises(ValueError):
                mod.trx_report(self.write_trx([('Fixture.OrdinaryTest', 'Passed')],
                    {'total':'1'} | {key:value}))

    def test_trx_rejects_missing_negative_or_malformed_counters(self):
        for key in self.TRX_COUNTS:
            for value in (None, '-1', 'invalid'):
                with self.subTest(counter=key, value=value):
                    path = self.write_trx([('Fixture.OrdinaryTest', 'Passed')], {'total':'1'})
                    tree = ET.parse(path)
                    counters = next(n for n in tree.iter() if n.tag.endswith('Counters'))
                    if value is None:
                        del counters.attrib[key]
                    else:
                        counters.set(key, value)
                    tree.write(path)
                    with self.assertRaises(ValueError):
                        self.module().trx_report(path)

    def test_trx_rejects_failed_unknown_or_unexpected_skipped_result(self):
        for outcome in ('Failed', 'NotExecuted', 'Error', 'Pending', 'Unknown'):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                self.module().trx_report(self.write_trx([('Fixture.OrdinaryTest', outcome)], {'total':'1'}))

    def test_trx_rejects_empty_or_all_skipped_execution(self):
        for results, total in ([], '0'), (self.TRX_RESULTS[1:], '2'):
            with self.subTest(total=total), self.assertRaises(ValueError):
                self.module().trx_report(self.write_trx(results,
                    {'total':total, 'executed':'0', 'passed':'0'}))

    def test_trx_rejects_identity_mismatch_duplicate_or_missing_results(self):
        for mutation in ('name', 'id', 'duplicate', 'missing', 'missing-id'):
            with self.subTest(mutation=mutation):
                path = self.write_trx()
                tree = ET.parse(path)
                results = next(n for n in tree.iter() if n.tag.endswith('Results'))
                if mutation == 'name':
                    results[0].set('testName', 'DifferentTest')
                elif mutation == 'id':
                    results[0].set('testId', 'unmatched-id')
                elif mutation == 'duplicate':
                    results[1].set('testId', results[0].get('testId'))
                elif mutation == 'missing':
                    results.remove(results[0])
                else:
                    del results[0].attrib['testId']
                tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)

    def test_trx_rejects_failed_or_missing_run_summary(self):
        for outcome in ('Failed', 'Error', 'Aborted', None):
            with self.subTest(outcome=outcome):
                path = self.write_trx([('Fixture.OrdinaryTest', 'Passed')], {'total':'1'})
                tree = ET.parse(path)
                summary = next(n for n in tree.iter() if n.tag.endswith('ResultSummary'))
                if outcome is None:
                    del summary.attrib['outcome']
                else:
                    summary.set('outcome', outcome)
                tree.write(path)
                with self.assertRaises(ValueError):
                    self.module().trx_report(path)


if __name__ == '__main__':
    unittest.main()
