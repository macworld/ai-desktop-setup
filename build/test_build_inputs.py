import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(shutil.which('dotnet'), 'dotnet SDK is required for the build-input contract')
class BuildInputTests(unittest.TestCase):
    def test_framework_app_locked_restore_ignores_sdk_default_rid(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as directory:
            # Inject the Windows SDK inference result before the app's late
            # properties so this restore regression also runs on other hosts.
            inferred = Path(directory) / 'inferred.targets'
            inferred.write_text('''<Project>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'AI.Desktop.Setup'">
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <_UsingDefaultRuntimeIdentifier>true</_UsingDefaultRuntimeIdentifier>
  </PropertyGroup>
</Project>''')
            result = subprocess.run(
                ['dotnet', 'restore', str(root / 'App/AI.Desktop.Setup.csproj'),
                 '--locked-mode', '--force', '-p:Platform=x64',
                 '-p:CustomBeforeDirectoryBuildTargets=' + str(inferred)],
                cwd=root, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

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
