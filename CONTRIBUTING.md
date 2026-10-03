# Contributing

Bug reports, documentation improvements, protocol feedback, and pull requests are
welcome. For security issues, follow [SECURITY.md](SECURITY.md).

## Report an issue

Include the assistant version or commit, Windows version and native architecture,
what you expected, and what happened. For an interrupted setup, describe the stage
and message shown by the assistant. Remove setup codes, credentials, auth files,
and private gateway information from screenshots and logs.

## Development

Use .NET SDK **10.0.401**, selected by `global.json`. The WPF app targets .NET
Framework 4.8 on x64 and 4.8.1 on ARM64; the shared Core and Tests projects target
.NET 10 and .NET Framework 4.8. Packaging also requires NSIS **3.12** and Python 3.
The hosted build's exact tool versions are recorded in
[build/toolchain.json](build/toolchain.json).

From the repository root, run the shared tests:

```sh
dotnet test Tests/AI.Desktop.Setup.Tests.csproj -c Release -f net10.0 --nologo
```

Run the build-tool tests with Python, NSIS, the .NET SDK, and PowerShell available:

```sh
python3 -m unittest discover -s build -p 'test_*.py'
```

For local Windows packages, run `./scripts/build-windows.sh` from a Bash shell.
It runs the shared .NET 10 tests, compiles the .NET Framework tests, and packages
x64 and ARM64 outputs. On Windows, the staged hosted build is implemented in
[build/build.ps1](build/build.ps1); it requires a fresh build directory and the
pinned toolchain.

Builds and portable tests do not verify Windows installation behavior. Changes to
WPF, package trust, elevation, access controls, or recovery may also need the native
checks in [docs/windows-acceptance.md](docs/windows-acceptance.md). Record the
architecture and tests actually executed, and identify any skipped native checks.

## Continuous integration

Hosted CI stages the exact source commit in a fresh directory, restores locked
dependencies, runs shared .NET 10 and .NET Framework 4.8 tests, and packages both
app architectures. It retains source, intermediates, test results, logs, payloads,
and unsigned candidates in a hashed cohort.

Without a real official MSIX fixture, CI permits only
`ActualOfficialPackageRequiresNativeTrustAndPinsBytes` and
`ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust` to be skipped.
Their absence is recorded as an unverified native gate. Every other discovered
test must execute and pass; empty runs, unexpected skips, inconsistent test counts,
and failures stop CI. Native Windows acceptance and independent audit remain
separate release gates under the [signed release procedure](docs/signed-release.md).

## Pull requests

Keep changes focused and describe the user-visible result, reasoning, and relevant
verification. Preserve the separation between gateway-supplied configuration and
the local adapter's installation authority. Protocol changes must update the
[contract, schema, and vectors](protocol/README.md) together with affected tests.
Never commit real credentials, proprietary client packages, or private test evidence.

All contributors must use multi-factor authentication (MFA). Contributors retain
their copyright and provide contributions under the project's [MIT license](LICENSE).
Preserve applicable third-party notices.

## Repository and release roles

Repository maintainers review changes and manage repository access. Signing
submitters request signatures for a frozen candidate; signing approvers authorize
each request. Independent release approvers review native acceptance and audit
evidence for the exact candidate before publication. Repository ownership alone
does not satisfy an independent approval gate.

Role assignments, protected environments, and signing credentials are configured
separately from source contributions. Follow the [code signing policy](docs/code-signing-policy.md)
and [signed release procedure](docs/signed-release.md) before enabling or promoting
a release.
