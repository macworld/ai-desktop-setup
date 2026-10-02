# Windows acceptance

The shared regression harness must run on Windows 10 x64 with Framework 4.8,
and Windows 11 ARM64 with Framework 4.8.1. Compilation, x64 emulation on ARM,
and non-Windows substitutes do not satisfy either native gate. Use isolated
scratch and synthetic credentials; never point fixtures at an actual Codex home.

```powershell
dotnet test Tests/Windows/AI.Desktop.Setup.WindowsTests.csproj -c Release -p:Platform=x64 --settings Tests/Windows/x64.runsettings --logger trx --diag TestResults/x64-vstest.log
dotnet test Tests/Windows/AI.Desktop.Setup.WindowsTests.csproj -c Release -p:Platform=ARM64 --settings Tests/Windows/arm64.runsettings --logger trx --diag TestResults/arm64-vstest.log
```

Use a current matching-architecture SDK/testhost. The native project pins the
Framework/platform and includes shared Core/protocol/recovery/workflow/trust
regressions. `NativeHostTests` fails unless process and OS are the expected native
architecture (`IsWow64Process2` process machine UNKNOWN), and checks the native
OS build and installed Framework release. Retain its output, TRX, SDK version,
selected host path and CLR module. Record execution identity separately from the
interactive user: a SYSTEM run does not prove original-user isolation or UI use.

Set `AI_SETUP_OFFICIAL_MSIX` to a real official package matching the native host
for full package trust, identity/architecture and byte-pinning fixtures. Separately,
`AI_SETUP_SIGNATURE_MSIX` accepts a real official package for architecture-independent
signature-provider testing. Its unmodified baseline must succeed before testing
manifest/payload corruption. This does not establish installation compatibility.
Never substitute synthetic ZIPs or weakened revocation settings for native trust.

Current evidence and gates (2026-10-02):

| Gate | Windows 10 native x64 | Windows 11 native ARM64 |
| --- | --- | --- |
| Framework project compile | Passed (compile only) | Passed (compile only) |
| Native host, OS, CLR assertions | No native host available | Passed; Framework 4.8.1, native ARM64 |
| WPF PNG/JPEG real pixel decode, bounds, truncated payload, frozen transfer | Not run | Passed |
| CurrentUser DPAPI / strict owner and private ACL | Not run | Passed under SYSTEM; interactive-user evidence pending |
| Shared regression suite | Not run | See final test receipt; not a cross-account acceptance claim |
| Real matching-architecture package trust and corruption | Not run | Fixture unavailable; not accepted |
| Different-user DPAPI rejection / private ACL isolation | Not run | Not run |
| Original-user and alternate-admin UAC, package-only source read/copy and protected revalidation | Not run | Not run |
| Existing/newer client, registration/launch ownership, sign-out/restart | Not run | Not run |
| No-follow directory/file replacement races; power-loss persistence | Not run | Not run |
| MOTW / SmartScreen on final signed artifact | Not run | Not run; signed-artifact release gate |

The cached official signature-provider fixture is `OpenAI.Codex` x64
`26.928.3736.0`, SHA256
`36a36149f7004c5d4619a4df7882931cc0fccb4d86b065fae2c49d7b179bfa8d`.
Testing its provider on ARM64 is neither ARM64 package acceptance nor a native
x64 run. Private evidence records exact source revisions, receipts and limitations.
Do not publish a completed native matrix while any required row remains open.

Recovery behavior:

- Preview is local. A private current-user claim is committed before bootstrap.
  Lost bootstrap responses retry the original claim and secret, even beyond the
  initial ticket window; the server remains the authorization authority.
- Configuration journals live beneath a fixed private child of the local Codex
  home, preserving same-volume atomic replacements. Resume rechecks authorization
  and actual file digests; conflicts fail closed. Committed journals never reinstall
  or rewrite configuration just to retry a receipt.
- Resume, cleanup and cancellation serialize locally. Explicit cancel/exit,
  acknowledged completion and expired-record cleanup reclaim owned secret stages;
  they preserve target files and intentional private backups, and never revoke keys.
- Restart and sign-out flags are independent local fields. They remain conservative
  through receipt retry; a restarted setup process does not prove a Windows restart
  or sign-out. A recovered window advises opening from Start if the requirement
  was already satisfied. It does not launch automatically or demand another install.
  Acknowledged completion clears recovery; a fresh attempt reinspects registration.
- Protected artwork is screened before actual WPF decoding and bounded pixel
  allocation. Cosmetic decode failures use the neutral fallback; authorization
  rejection still blocks continuation. Help opens only on a click and excludes
  query/fragment parameters. Clipboard cleanup occurs only if it still contains
  the code pasted by this operation.
