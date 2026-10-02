# AI Desktop Setup

A neutral Windows desktop setup client baseline. This preview deliberately gates
installation and configuration while authenticated AGSP setup is implemented.
Opening the UI makes no network requests and asks for no API credential.

The Core and Tests projects target .NET 10 and .NET Framework 4.8. The WPF App
uses the Windows-provided .NET Framework 4.8 on x64 and 4.8.1 on ARM64; a .NET 10
runtime is never bundled. Build with the .NET 10 SDK and NSIS 3.12 or later:

```sh
./scripts/build-windows.sh
```

Set `AI_SETUP_DOTNET` or `AI_SETUP_MAKENSIS` to select installed tools. Local,
unsigned preview artifacts go into `artifacts/`. The packaging script validates
PE architecture and Framework runtime configuration, excludes PDBs, and records
hashes and unsigned status. Artifacts are not automatically uploaded or published.

The UI is an intentionally incomplete shell. The baseline retains local config/auth
backup and rollback, private-file safeguards, conservative TOML merging, complete-file
download limits, official-package metadata checks, protected helper IPC and Windows
servicing receipt checks. Core provides authenticated discovery, recovery storage,
configuration transactions, and independent official package trust boundaries;
the end-to-end user workflow remains to be implemented. See the
[Codex adapter policy and native gates](docs/codex-adapter.md). Native Windows
runtime, ACL, UAC, WPF and package-install acceptance is required before release;
cross-compilation alone does not establish those behaviors.

`protocol/` contains the AGSP v1 wire contract. Owned source uses the MIT license;
third-party code, build tools and downloaded official packages retain their licenses
and are identified in `THIRD-PARTY-NOTICES.md`.
