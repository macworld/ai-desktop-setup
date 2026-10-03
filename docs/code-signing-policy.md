# Code signing and release policy

Local and ordinary pull-request/main CI outputs are unsigned previews (`signed:false`).
They are not production downloads. Public source is available at
[macworld/ai-desktop-setup](https://github.com/macworld/ai-desktop-setup);
[Releases](https://github.com/macworld/ai-desktop-setup/releases) lists unsigned
previews when published. No signed release or signing certificate exists.
Hosted CI execution, native Windows acceptance,
signing and publication each require their own evidence for the exact candidate.

SignPath Foundation is the intended signing service. No application has been
submitted or approved, and eligibility is not guaranteed. The application packet
is this project's published source, MIT license, third-party notices, build workflow,
security policy, contributor/role assignments and a reviewable preview. The sole
known maintainer, author and reviewer is
[Wenhao Liu (macworld)](https://github.com/macworld), the owner and intended signing
authority. Actual SignPath submitter/approver roles remain pending Foundation
approval and setup. All contributors are required to use MFA as project policy;
this is not verification of any account's MFA status. Foundation support is not
claimed before acceptance. Signing does not guarantee SmartScreen trust.

The staged workflow follows this order (implementation and external setup are
documented in [signed-release.md](signed-release.md); it has not been executed):

1. Build and test a frozen source commit on the trusted hosted build system.
2. Inventory owned inner binaries and retained third-party files. Request signing
   only for `AI.Desktop.Setup.exe` and `AI.Desktop.Setup.Core.dll` per architecture.
3. Obtain the required human approval for the inner request; verify identity, chain,
   timestamp and byte inventory. Keep third-party identities and signatures intact.
4. Package those verified inner bytes, then request signing for each final outer EXE.
   This is a distinct request with its own approval policy.
5. Verify the final outer signatures/timestamps and extracted inner byte hashes.
   Compute final hashes only after the final signatures exist.
6. Require exact-candidate native Windows and fresh private audit acceptance, then
   attest the final source/run/artifact-bound bytes and publish an immutable release. Do not rebuild, alter, or re-sign an approved artifact during promotion.

Submitters may request signatures; designated approvers authorize each request.
Release approvers accept the exact completed candidate. An independent release
approver is not yet configured; the sole maintainer's review does not satisfy the
independent protected-environment gate. Formal signing remains disabled
(`RELEASE_ENABLED=false`). Configure these real roles,
expected certificate identity, protected environments, source-origin policies and
retention externally before enabling a release workflow. Ordinary CI has only
contents-read permission, no signing secrets and no publication permission.

Retain matching NSIS source alongside each distributed installer as described in
`licenses/NSIS/SOURCE.md`. Official proprietary clients are downloaded at runtime;
they are never signed as project-owned files or bundled into these previews.
