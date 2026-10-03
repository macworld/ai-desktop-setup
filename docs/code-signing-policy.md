# Code signing and release policy

This policy defines how project-owned Windows binaries are signed and promoted
to a release. Local builds and ordinary pull-request/main CI produce unsigned
previews (`signed:false`). A production release requires build, signature, native
Windows acceptance, and audit evidence bound to the exact candidate.

SignPath Foundation is the intended signing service. Its acceptance, certificate
configuration, and role assignments are prerequisites for enabling signing.
Signing is disabled by default; the procedure below does not establish that these
prerequisites have been completed. Signing does not guarantee SmartScreen trust.

All contributors must use MFA. Repository maintainers manage access, signing
submitters request signatures, and designated signing approvers authorize each
request. Independent release approvers accept the completed candidate. See
[repository and release roles](../CONTRIBUTING.md#repository-and-release-roles).

The [signed release procedure](signed-release.md) implements this order:

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

The release workflow requires `RELEASE_ENABLED=true`; leave it unset or false
until all prerequisites are verified. Configure actual role assignments, the
expected certificate identity, protected environments, source-origin policies,
and retention externally before enabling it. Self-review does not satisfy an
independent protected-environment gate. Ordinary CI has only contents-read
permission, no signing secrets, and no publication permission.

Retain matching NSIS source alongside each distributed installer as described in
[licenses/NSIS/SOURCE.md](../licenses/NSIS/SOURCE.md). Official proprietary clients
are downloaded at runtime;
they are never signed as project-owned files or bundled into these previews.
