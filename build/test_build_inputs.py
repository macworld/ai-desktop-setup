import os
from pathlib import Path
import shutil
import subprocess
import unittest


@unittest.skipUnless(shutil.which('dotnet'), 'dotnet SDK is required for the build-input contract')
class BuildInputTests(unittest.TestCase):
    def evaluate(self, *properties):
        root = Path(__file__).resolve().parents[1]
        return subprocess.run(['dotnet','msbuild',str(root/'Core/AI.Desktop.Setup.Core.csproj'),
                               '-nologo','-target:ValidateFreshBuildInputs',*properties],
                              cwd=root,capture_output=True,text=True)

    def test_fresh_build_accepts_supported_cache_policy(self):
        self.assertEqual(self.evaluate().returncode, 0)

    def test_explicit_cache_paths_and_enabled_caches_are_rejected(self):
        for setting in ['DisablePackageAssetsCache=false','DisableRarCache=false',
                        'ResolveAssemblyReferencesStateFile=unexpected.cache',
                        'AssemblyInformationCacheOutputPath=unexpected.cache',
                        'AssemblyInformationCachePaths=unexpected.cache']:
            with self.subTest(setting=setting):
                self.assertNotEqual(self.evaluate('-p:'+setting).returncode, 0)
