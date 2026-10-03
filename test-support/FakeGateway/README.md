# Local interoperability fixtures

`dotnet test Tests/AI.Desktop.Setup.Tests.csproj -c Release -f net10.0 --filter Interoperability`
runs self-contained loopback HTTPS tests. Two fictitious services use distinct
paths, names and models through the same production parser, HTTP client,
coordinator, durable resume store, journal and configuration writer. One has
protected artwork and synthetic package/license assets; the other has neither.
All resources and credentials are generated at runtime. No website identity,
cloud account, real upstream credential, OS trust change or model call is used.

The tests assert zero requests before explicit authentication, invalid-ticket
zero configuration reads, actual anonymous asset request headers, configuration
binding, durable cleanup, certificate rejection, persistence-before-network,
bounded throttling, terminal malformed authorization errors, redirect refusal,
and cosmetic versus terminal protected-artwork failures.

The fake service is a protocol fixture, not an implementation of a production
issuer or authorization policy. The real installer is replaced with an installed
result; asset requests exercise `ProtocolHttpClients`, not package signature
validation or native installation. `TestProtection` substitutes for Windows
DPAPI. These tests execute the shared core assembly on net10.0; they do not
execute a signed desktop EXE, WPF, native DPAPI, UAC, registration or launch.
The test-only TLS server/process harness is excluded from net48 compilation.
The unchanged frozen protocol vectors remain separate layer-consumer evidence;
these generated compositions are integration analogues, not 133 end-to-end tests.

## Neutral external fixture process

An independent fixture owner can invoke the already-built test assembly:

```text
dotnet Tests/bin/Release/net10.0/AI.Desktop.Setup.Tests.dll agsp-interoperability
```

Set only `AGSP_LIVE_FIXTURE` to a private JSON file path. Required mode fails with
exit 76 on missing/malformed input or assertion failure; there is no successful
skip. The dispatcher prints only a fixed failure message, never fixture values.
The owner supplies these PascalCase JSON properties at runtime:

| Field | Meaning |
| --- | --- |
| Version | 1 |
| Root | Owned scratch directory (0700 on Unix; equivalent current-user ACL on Windows) |
| Origin, Certificate | Loopback HTTPS origin and base64 DER public certificate, pinned exactly per handler |
| Code, Key | Synthetic setup code and expected synthetic selected credential; secret input only |
| Brand, Model, Revision | Expected immutable configuration metadata |
| Now | Injected UTC timestamp shared with fixture service |
| Phase | happy, api-key, cancel, bootstrap-loss, resume, complete-loss, receipt, revoke-before-install, revoke-after-install, receipt-denied |
| Result | Secret-free result-file path beneath the owned root |

The owner is responsible for validated fixture paths, restricted file creation,
real server/DB setup, current-policy mutations, connection aborts after database
commit, child timeout/reaping and exact cleanup. It must not pass credentials in
arguments/environment or capture request/SQL arguments in logs. Fixture JSON,
resume records and generated auth/config files must not enter source control.

Each phase writes `preview.ready` after local cleanup/preview and waits for
`preview.go`. The owner compares actual HTTP counters before releasing it.
Revocation phases similarly use `revoke.ready`/`revoke.go`. Delete handshake files
before each new process. Loss phases must exhaust all three production transport
attempts before restart. `resume` uses the saved claim; `receipt` permits protected
GET reauthorization while asserting zero install/configure calls, unchanged
config/auth bytes and mtimes, acknowledgment and stage/intent cleanup. Results
contain only outcome, install/write counts, resume ID and process ID. Do not
publish runtime fixtures or claim native acceptance from these substitutes.
