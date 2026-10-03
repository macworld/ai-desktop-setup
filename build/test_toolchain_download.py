import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest


ARCHIVE = b'PK\x03\x04pinned tool archive fixture'
HTML = b'<!doctype html><html>Download requires a command-line client</html>'


class DownloadServer(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/redirect':
            self.send_response(302)
            self.send_header('Location', '/html?transient-secret=do-not-log')
            self.end_headers()
            return
        is_download_client = self.headers.get('User-Agent', '').startswith('AI-Desktop-Setup-Toolchain/')
        body = ARCHIVE if self.path == '/archive' and is_download_client else HTML
        self.send_response(200)
        self.send_header('Content-Type', 'application/octet-stream' if body == ARCHIVE else 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is required for toolchain download tests')
class ToolchainDownloadTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), DownloadServer)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def download(self, directory, endpoint, digest):
        destination = Path(directory) / 'tool.zip'
        environment = os.environ.copy()
        environment.update({
            'DOWNLOAD_HELPER': str(Path(__file__).with_name('download-toolchain.ps1')),
            'DOWNLOAD_URL': f'http://127.0.0.1:{self.server.server_port}{endpoint}',
            'DOWNLOAD_OUTPUT': str(destination),
            'DOWNLOAD_SHA256': digest,
        })
        result = subprocess.run([
            'pwsh', '-NoProfile', '-Command',
            '$ErrorActionPreference = "Stop"; '
            '. $env:DOWNLOAD_HELPER; '
            'try { Get-PinnedToolchainDownload -Name "NSIS archive" -Uri $env:DOWNLOAD_URL '
            '-OutFile $env:DOWNLOAD_OUTPUT -Sha256 $env:DOWNLOAD_SHA256; '
            '@{ok=$true} | ConvertTo-Json -Compress } '
            'catch { @{ok=$false; error=$_.Exception.Message} | ConvertTo-Json -Compress }',
        ], env=environment, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout), destination

    def test_download_client_receives_and_keeps_pinned_archive(self):
        # Browser-style or omitted UA makes this endpoint return HTML instead.
        with tempfile.TemporaryDirectory() as directory:
            result, destination = self.download(directory, '/archive', hashlib.sha256(ARCHIVE).hexdigest())
            self.assertTrue(result['ok'], result)
            self.assertEqual(destination.read_bytes(), ARCHIVE)

    def test_changed_archive_is_rejected_and_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            result, destination = self.download(directory, '/archive', '0' * 64)
            self.assertFalse(result['ok'])
            self.assertIn(hashlib.sha256(ARCHIVE).hexdigest(), result['error'])
            self.assertIn('0' * 64, result['error'])
            self.assertFalse(destination.exists())

    def test_html_response_has_useful_diagnostic_without_redirect_query(self):
        with tempfile.TemporaryDirectory() as directory:
            result, destination = self.download(directory, '/redirect', hashlib.sha256(ARCHIVE).hexdigest())
            self.assertFalse(result['ok'])
            for detail in ('NSIS archive', 'status=200', 'content-type=text/html', f'bytes={len(HTML)}',
                           hashlib.sha256(HTML).hexdigest(), '/html'):
                self.assertIn(detail, result['error'])
            self.assertNotIn('do-not-log', result['error'])
            self.assertNotIn('transient-secret', result['error'])
            self.assertFalse(destination.exists())

    def test_html_is_rejected_even_when_its_digest_matches(self):
        with tempfile.TemporaryDirectory() as directory:
            result, destination = self.download(directory, '/html', hashlib.sha256(HTML).hexdigest())
            self.assertFalse(result['ok'])
            self.assertIn('content-type=text/html', result['error'])
            self.assertFalse(destination.exists())


if __name__ == '__main__':
    unittest.main()
