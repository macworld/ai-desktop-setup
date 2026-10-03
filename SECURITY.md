# Security policy

## Report a vulnerability

Use GitHub [private vulnerability reporting](https://github.com/macworld/ai-desktop-setup/security/advisories/new)
to report a security issue in the assistant or its AGSP implementation. Include
the affected version or commit, Windows version and architecture, reproduction
steps, and the potential impact. Review any attachments for sensitive data first.

Do not put credentials, setup codes, auth files, private logs, or exploitable
details in public issues. If a credential was exposed, revoke or rotate it with
the issuing service. Issues in a gateway service or the official desktop client
should also be reported to the responsible provider.

## Release scope

A production-support lifecycle has not yet been designated. Release notes describe
the status of each available build; the [Windows acceptance matrix](docs/windows-acceptance.md)
and [code signing policy](docs/code-signing-policy.md) define release requirements.
An unsigned build does not establish signature validation or native acceptance.

Security reports are reviewed by repository maintainers. No fixed response time
is guaranteed.
