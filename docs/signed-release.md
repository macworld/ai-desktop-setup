# Staged signed release

The source and staged workflow are public at
[macworld/ai-desktop-setup](https://github.com/macworld/ai-desktop-setup). The signed
release workflow has not run against SignPath. See
[Actions](https://github.com/macworld/ai-desktop-setup/actions) for current outcomes.
There is no Foundation submission or approval, signing certificate, full native
installation acceptance, or published signed release. Native ARM64 wrapper UI
startup passed only. Formal signing remains disabled (`RELEASE_ENABLED=false`).

[Wenhao Liu (macworld)](https://github.com/macworld) is the sole known maintainer,
author and reviewer, and the intended signing authority as repository owner.
SignPath roles remain pending approval/setup, and an independent release approver
is not configured. Self-review cannot satisfy the independent environment gates.
All contributors must use MFA under project policy; account MFA and approval
configuration have not been verified. See the [Code signing policy](code-signing-policy.md).

`release.yml` accepts protected version tags only, in the configured original
repository. Ordinary PR/main CI remains unsigned and has no signing secrets.
Before enabling it, review the exact committed workflow and configure:

- `RELEASE_REPOSITORY` (exact owner/repository), then `RELEASE_ENABLED=true` only
  when all prerequisites below are ready. Tags must match the project version.
- Pre-created `signing-inner`, `signing-outer`, `windows-acceptance`, `private-audit`
  and `release-publication` environments, with protected-tag restrictions, required
  reviewers, prevention of self-review and disabled administrative bypass.
- Foundation acceptance; trusted GitHub origin/hosted-runner policy; actual
  submitter/approver assignments; `SIGNPATH_ORGANIZATION_ID`,
  `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`; reviewed artifact configurations
  named `inner` and `outer` from `signing/*.xml`. Each request needs its own manual
  SignPath approval. Only the signing environments hold `SIGNPATH_API_TOKEN`.
- `SIGNER_CERTIFICATE_SHA256`: independently confirmed lower-case SHA256 of the
  expected DER signing certificate. Renewal requires review of the new identity.
- Release immutability and tag rules. Publication's `RELEASE_SETTINGS_TOKEN` is a
  separate single-repository Administration:read credential, used only for the
  fixed GitHub immutable-settings GET. Prefer a short-lived GitHub App installation
  token supplied by the protected environment; a narrowly scoped fine-grained token
  is also supported. It has no publication/signing write permission. The ordinary
  job token alone performs release writes. No credential is created by these scripts.
- Artifact retention of at least 40 days and approval within GitHub's environment
  wait limit (30 days). Expiration or a new run attempt needs fresh acceptance.

The order is fixed:

1. Build/test exact source on a hosted Windows runner. `build/build.ps1 -Version V
   -OutputDirectory DIR` stages complete unsigned `x64/` and `arm64/` payloads.
   Its retained cohort includes source, logs, test results and matching NSIS source.
2. Submit the exact current-run inner artifact ID. Sign only the two owned binaries
   per architecture. Verify returned complete inventories, unchanged third-party
   bytes, allowed PE signing changes, native chain/identity/product/version/timestamp.
3. `build/package.py --payload DIR --architecture x64|arm64 --version V --output FILE
   --inner-verification FILE` packages the verified bytes; it neither recompiles nor
   creates final hashes. It requires embedded-signature framing and an exact native
   verifier inventory. A copied JSON report alone is not a trust anchor.
4. Submit the new outer artifact ID as a second request. Sign only the two exact
   versioned installers. Compare signing mutations with the retained unsigned outer,
   decode final NSIS bytes, match the reviewed wrapper profile and all payload bytes,
   then use SignTool `/pa /all /v /tw` with exit zero and Authenticode identity,
   embedded-signature, product/version and timestamp checks on outer and owned inner.
5. `verify-release.ps1 -ReleaseDirectory DIR -SourceCommit SHA -Output FILE` also
   requires `-UnsignedReleaseDirectory`, `-PayloadDirectory`, `-Version` and the
   configured certificate/run identity. `write-release-metadata.py` binds its report
   to the final bytes and writes `release-manifest.json` and `SHA256SUMS` once.
6. Seal all attachments in `candidate-index.json`; retain signing request/run/attempt/
   artifact IDs and transport digests separately from EXE digests. Two environment
   approvals accept this exact original candidate: real Windows x64/ARM64 installation
   acceptance, and a fresh independent private audit of source/history plus this
   signed cohort. Reviewers retain detailed native/audit receipts privately, including
   commit, run/attempt, candidate artifact ID and index/file hashes. Public CI retains
   only the bound approval status. Source-only or unsigned receipts cannot authorize it.
7. Both gates re-download the stored ID and verify every byte. Attest explicit final
   EXEs, manifest, verification and candidate index in the same original run. Save the
   Sigstore bundle; no rebuild, re-sign or metadata regeneration occurs after approval.
8. Check immutable settings and exact tag commit, create a new draft, upload the full
   attachment set, check asset digests and complete downloaded bytes, then recheck
   settings/tag and publish. Require the resulting release to be immutable. Failure
   before publication leaves the draft; a failed postcheck requires operator inspection.
   Existing drafts/releases are never overwritten. New bytes require a new version.

The signing wait is bounded to three hours per request. A timeout does not authorize
another automatic request or reuse of an earlier approval. Retained request IDs are
for diagnosis; asynchronous recovery has not been implemented or validated.

## Extractor boundary and tests

`nsis_payload.py` is a narrow original stdlib reader of the pinned NSIS format
([source](https://github.com/nsis-dev/nsis/tree/e3f60402bcdf7be822d159b531c6e38ddf32de12/Source)).
The committed `wrapper-profile.json` pins the wrapper source/toolchain and complete
normalized header for each architecture, including strings, paths, instruction flags,
control-flow targets, section/function ranges, assignments and launch expression.
Only File data-record offsets/FILETIMEs and section size are normalized. Section KiB
is separately recomputed as the sum of rounded-up ordinary payload file lengths;
plugin emissions do not contribute (pinned `script.cpp` add_file, generatecode flag).
Never regenerate this profile from a candidate as part of verification.

Every File instruction and raw data record is classified. Plugin bytes use the exact
locked Windows `System.dll` hash; every app/support/license path and byte must match
verified staging. The outer is the pinned x86 NSIS stub; x64/ARM64 identity comes from
both owned inner PEs and the application's Framework configuration. PE certificate
framing requires one terminal aligned PKCS SignedData envelope; it establishes no
cryptographic trust. Native checks and whole-file hashes remain mandatory.

Portable Python tests use real NSIS compilation for structural extraction and explicit
synthetic certificate framing cases. Neither is signature acceptance. Pester policy
tests can run portably; the actual signed-candidate test requires Windows and explicit
fixture environment variables. Skipping it is an open native gate. The local profile
has not yet been validated by execution of the locked Windows compiler/service output;
any legitimate difference must receive a new explicit profile review, never a fallback.

Integration references: [SignPath syntax](https://docs.signpath.io/artifact-configuration/syntax),
[SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool),
[GitHub immutable settings](https://docs.github.com/en/rest/repos/repos#check-if-immutable-releases-are-enabled-for-a-repository).
