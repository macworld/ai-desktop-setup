# Codex Windows adapter

This adapter owns installation authority. AGSP supplies bounded configuration and
optional complete-package mirror metadata; it cannot supply a publisher, trusted
root, command, executable path, or installation policy. Mirrors' declared SHA256
values establish transport integrity, not official trust.

Reviewed 2026-10-02 against the official [Windows deployment guide](https://learn.chatgpt.com/docs/enterprise/windows-deployment):

| Artifact | Fixed official source |
| --- | --- |
| x64 package | `https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix` |
| ARM64 package | `https://persistent.oaistatic.com/codex-app-prod/ChatGPT-arm64.msix` |
| Offline license | `https://persistent.oaistatic.com/codex-app-prod/ChatGPT-License.xml` |

Store product: `9PLM9XGG6VKS`. The guide calls these latest Store-signed packages
and requires offline license pairing for provisioning. Sources move; filenames,
display names, and mirror hashes cannot establish package identity.

Local adapter pins, corroborated by cached official x64 package metadata:

- Identity: `OpenAI.Codex`.
- Manifest publisher: `CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B`.
- Family: `OpenAI.Codex_2p2nqsd0c76g0`; application: `App`.
- Native OS architecture: x64 or ARM64; an emulated process must select the OS package.
- Minimum accepted application version: **26.928.3736.0**. This is a conservative
  reviewed adapter baseline taken from the available official metadata fixture,
  **not a vendor minimum-support promise or native acceptance result**.
- Maximum complete MSIX: 2 GiB; license: 1 MiB. The reviewed x64 fixture is
  905,762,116 bytes. All declarations, headers, and streamed body bytes are bounded.
- x64 Windows baseline: 10.0.19041.0 / Framework 4.8. ARM64 helper baseline:
  10.0.22621.0 / Framework 4.8.1. The package's own target-family minimum is also checked.

Numeric version comparison determines Skip/Install/Register; a newer matching
installed package is never downgraded. Official moving-source selection skips a
valid installed version at or above the adapter baseline. A selected newer mirror
may request an upgrade; its downloaded version must match its declared version.
The helper independently checks machine-wide registered/provisioned versions
before deployment, including packages belonging to another account.

## Signature policy and byte binding

Before elevation and again on the protected helper copy, invoke
[WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
with `WINTRUST_ACTION_GENERIC_VERIFY_V2`, the documented
[app-package signature provider](https://learn.microsoft.com/en-us/windows/win32/appxpkg/how-to-programmatically-sign-a-package#remarks).
Only native status zero succeeds. Non-Windows execution fails closed. No certificate
installation, leaf-thumbprint shortcut, hash-only flag, or trust bypass is supported.

[WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data)
uses noninteractive FILE verification, VERIFY followed by CLOSE even after failure,
whole-chain revocation excluding the root (`fdwRevocationChecks=1`, flags `0x80`),
and MD2/MD4 disabled (`0x2000`). Online retrieval is permitted: cache-only (`0x1000`)
is absent. Lifetime-signing (`0x800`) is absent, preserving the provider's timestamp
semantics. Unavailable revocation (`0x80092013`/`0x800B010E`) is retryable failure;
no fallback disables revocation. Other nonzero native statuses also block deployment.
Native network/time-out/cache/expired/revoked/timestamp behavior remains an acceptance gate.

The signed manifest must independently match identity, publisher, native
architecture, local version floor, target OS, and mirror version. Keep locally
calculated SHA256, file handles that disallow Windows write/delete sharing, and
parent-directory handles that disallow rename until consumption finishes. The
helper accepts only local path/digest pairs and the fixed Provision operation,
copies into an admin-owned directory, rechecks its built-in policy and both
handoff hashes, and keeps handles open through DISM. DISM's own signature/license
validation is an additional enforcement layer, never pre-install trust evidence.
The offline license's authorization is enforced by Windows servicing; the local
license hash binds handoff bytes, not a gateway's authority to issue a license.

The original user's package-only staging directory grants that user full control
and Administrators/SYSTEM read/execute for alternate-account UAC copying. It grants
neither group writes nor Everyone/Users access. It contains only public installation
artifacts. Resume secrets, API keys, configuration, and journals remain in their
separate original-user storage. The helper destination permits only Administrators
and SYSTEM. These ACLs and locked-handle behavior require native cross-account tests.

Package and license transport uses ProtocolHttpClients' anonymous pool with no
cookies, default credentials, bearer/proof headers, decompression, or redirects.
No automatic fallback occurs. After mirror failure the workflow may offer an
explicit official-source retry using `CodexOfficialPolicy.Official(plan.Policy)`.
Current policy permits no redirects even for official sources; update the adapter
explicitly if the official distribution changes.

## Reasoning capability

`SessionCapabilities` permits only exact `none`, `minimal`, `low`, `medium`, `high`,
`xhigh`, and requires license pairing. Omission stays omitted; it does not insert
`none` or `medium`. This is a reviewed v1 adapter subset corroborated by the pinned
[traditional release parser](https://github.com/openai/codex/blob/4a3466efbf84cfb7469eca94bbf6307166c9f48e/codex-rs/protocol/src/openai_models.rs#L41).
The [current official parser](https://github.com/openai/codex/blob/cb6da58876afed3ede0ab11084f67dd5394ecb48/codex-rs/protocol/src/openai_models.rs#L60)
accepts open strings, including newer levels. Our narrower policy is not a
protocol-wide vendor enum. The Windows binary's source revision is unknown;
native client compatibility and model-specific service preflight remain distinct gates.

## Workflow consumers and native gate

Use `SessionBindingValidator(clock, CodexOfficialPolicy.SessionCapabilities)` for
production validation. Original-user `InspectMachineAsync` provides machine state;
`Resolve(session,machine)` selects an InstallationPlan. `InstallAsync(plan,progress,ct)`
owns private staging/download/trust/elevation/cleanup. Alternatively,
`PackageTrustVerifier.PrepareAsync` returns a disposable PreparedInstallation;
keep it alive through `WindowsInstaller.InstallPreparedAsync`. `OriginalUserSid`
is checked before handoff. Only `CreateHelperRequest()` crosses IPC, with no session
or credential information. The helper reconstructs its own policy/machine state.
Original-user `RegisterAsync` uses the fixed family registration command and then
re-inspects. Failed immediate registration reports a sign-out requirement. Launch
uses `CodexLauncher.Open(machine)` after verified current-user registration; it
checks the original SID. Provisioning success alone is insufficient to launch.

Native acceptance is **pending**, and release remains gated. Set
`AI_SETUP_OFFICIAL_MSIX` to an external real official package matching native host
architecture, then run tests filtered to `NativePackageTrustTests` in a native x64
Windows 10 process and native ARM64 Windows 11 process. The binary is never committed.

- `ActualOfficialPackageRequiresNativeTrustAndPinsBytes`: genuine trust, identity,
  native architecture, Windows write-sharing protection, wrong-architecture rejection.
- `ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust`: independent copies
  with modified manifest and executable payload must fail the native provider.

If the provider fails to detect payload corruption, acceptance is blocked pending
additional signed blockmap content verification; servicing success cannot waive it.
C5 must additionally exercise signature corruption; revoked/unavailable/expired
chains and timestamp behavior; newer installed machine/user versions; source and
protected-copy replacement; real staging/helper ACLs; same-user and alternate-admin
UAC; cancellation; user registration, sign-out/restart, and launch. Unit tests with
synthetic ZIPs or substituted native calls establish local policy only.
