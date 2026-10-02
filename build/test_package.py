import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC=importlib.util.spec_from_file_location('package_windows',Path(__file__).resolve().parents[1]/'scripts/package-windows.py')


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.mod=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(self.mod)
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)

    def test_unknown_runtime_dll_is_rejected(self):
        self.assertTrue(hasattr(self.mod,'validate_runtime'), 'runtime license inventory missing')
        (self.root/'unexpected.dll').write_bytes(b'not owned')
        with self.assertRaises(ValueError):self.mod.validate_runtime(self.root,{})

    def test_mismatched_dependency_bytes_are_rejected(self):
        self.assertTrue(hasattr(self.mod,'validate_runtime'), 'runtime license inventory missing')
        (self.root/'System.Fixture.dll').write_bytes(b'changed')
        with self.assertRaises(ValueError):self.mod.validate_runtime(self.root,{'System.Fixture.dll':{'sha256':'0'*64}})

    def test_exact_runtime_inventory_preserves_ownership(self):
        self.assertTrue(hasattr(self.mod,'validate_runtime'), 'runtime license inventory missing')
        (self.root/'System.Fixture.dll').write_bytes(b'abc')
        entries=self.mod.validate_runtime(self.root,{'System.Fixture.dll':{'sha256':'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad','package':'System.Fixture','version':'1.0.0'}})
        self.assertEqual(entries[0]['ownership'],'third_party')
        self.assertEqual(entries[0]['package'],'System.Fixture')
