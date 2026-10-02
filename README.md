# AI Desktop Setup

A neutral Windows desktop setup client with authenticated AGSP discovery and
recoverable installation/configuration. Opening the UI and previewing a setup code
make no network requests. Confirm the displayed setup/API addresses to authenticate.

The Core and Tests projects target .NET 10 and .NET Framework 4.8. The WPF App
uses the Windows-provided .NET Framework 4.8 on x64 and 4.8.1 on ARM64; a .NET 10
runtime is never bundled. Local developer builds use exactly .NET SDK 10.0.401 and NSIS 3.12:

```sh
./scripts/build-windows.sh
```

Set `AI_SETUP_DOTNET` or `AI_SETUP_MAKENSIS` to select installed tools. Local,
unsigned preview artifacts go into `artifacts/`. The packaging script validates
PE architecture and Framework runtime configuration, excludes PDBs, and records
hashes and unsigned status. Artifacts are not automatically uploaded or published.

The four-step UI retains local config/auth
backup and rollback, private-file safeguards, conservative TOML merging, complete-file
download limits, official-package metadata checks, protected helper IPC and Windows
servicing receipt checks. Core provides authenticated discovery, recovery storage,
configuration transactions, and independent official package trust boundaries;
the end-to-end workflow rechecks authorization before installing or writing secrets.
Interrupted committed configuration retries only its completion receipt. See the
[Codex adapter policy](docs/codex-adapter.md) and
[Windows acceptance matrix](docs/windows-acceptance.md). Native Windows
runtime, ACL, UAC, WPF and package-install acceptance is required before release;
cross-compilation alone does not establish those behaviors.

`protocol/` contains the AGSP v1 wire contract. Owned source uses the MIT license;
third-party code, build tools and downloaded official packages retain their licenses
and are identified in `THIRD-PARTY-NOTICES.md`.

## Install and recovery

Download the architecture matching the native Windows system: x64 requires Windows
10 2004+ with .NET Framework 4.8; ARM64 requires Windows 11 22H2+ with 4.8.1.
Open the assistant, paste the setup code supplied by your chosen gateway, inspect
the displayed service/API addresses, and confirm authentication. Choose Install and
configure, approve Windows elevation only for the official package installation,
then open the desktop client. This requires network access for official downloads.
See [privacy](privacy.md), [removal and recovery](uninstall.md), and
[signing policy](docs/code-signing-policy.md).

## Reproducible input boundary

CI stages only the tested exact Git commit into a fresh neutral physical directory,
uses locked package restores and tool versions, runs shared net10.0 and net48 tests,
and compiles/packages both app architectures. It retains all generated source,
intermediates, PDBs, test results, logs, payloads and unsigned candidates in a hashed
cohort. Pre-existing developer output is never reused. Public helpers contain no
private audit rules. CI does not itself assert a private neutrality audit pass.

Hosted shared net48 testing is separate from the Windows 10 x64 and native ARM64
acceptance matrix. The latter remains required before release. No hosted CI or
signing run is claimed by checking in a workflow. There is no public release URL yet.

Ordinary hosted CI permits only the two named existing official-MSIX fixture tests
to be skipped when no real package fixture is supplied. It records those exact
identities as unverified native gates. Every other discovered test must execute and
pass; empty runs, unexpected skips, inconsistent TRX counts and failures stop CI.

The staged [signed-release workflow](docs/signed-release.md) is implemented but awaits
Foundation configuration, hosted execution and exact-candidate native/private acceptance.
Local unsigned outputs are not signed releases.
