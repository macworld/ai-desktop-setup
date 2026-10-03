"""Exercise the committed release upload selection and lossless local transport.

This models upload-artifact's documented hidden-file switch; it does not execute
GitHub's hosted service or claim artifact-origin/attestation acceptance.
"""
import importlib.util
import re
from pathlib import Path
import tempfile
import unittest
import zipfile


class CohortTransferTests(unittest.TestCase):
    def test_release_cohort_upload_retains_hidden_source_and_inventory(self):
        source = Path(__file__).resolve().parents[1]
        workflow = (source / '.github/workflows/release.yml').read_text()
        uploads = re.findall(r'      - uses: actions/upload-artifact@[^\n]+\n(.*?)(?=\n      - |\n  [a-z]|\Z)', workflow, re.S)
        cohort = [step for step in uploads if re.search(r'^        id: cohort$', step, re.M)]
        self.assertEqual(len(cohort), 1)
        options = dict(re.findall(r'^          ([\w-]+): ([^\n]+)$', cohort[0], re.M))
        self.assertEqual(options['path'], 'C:/build/ai-desktop-setup/cohort')
        include_hidden = options.get('include-hidden-files', 'false') == 'true'
        spec = importlib.util.spec_from_file_location('candidate_transfer', source / 'build/candidate.py')
        candidate = importlib.util.module_from_spec(spec); spec.loader.exec_module(candidate)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); staged = root / 'cohort'; staged.mkdir()
            # Actual tracked-source shape, including the actual release workflow.
            for name in ('.github/workflows/ci.yml', '.github/workflows/release.yml', '.gitignore', 'README.md'):
                path = staged / 'source' / name; path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((source / name).read_bytes())
            (staged / 'source/obj').mkdir(); (staged / 'source/obj/evidence.pdb').write_bytes(b'synthetic-evidence')
            before = candidate.inventory(staged)
            selected = [p for p in staged.rglob('*') if p.is_file() and (include_hidden or not any(x.startswith('.') for x in p.relative_to(staged).parts))]
            with zipfile.ZipFile(root / 'upload.zip', 'w') as archive:
                for path in selected: archive.write(path, path.relative_to(staged))
            with zipfile.ZipFile(root / 'upload.zip') as archive: archive.extractall(root / 'download')
            self.assertEqual(candidate.inventory(root / 'download'), before)
