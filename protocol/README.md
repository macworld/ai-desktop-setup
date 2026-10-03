# AGSP v1 contract

The AI Gateway Setup Protocol (AGSP) defines how a gateway issues setup codes and
provides authenticated configuration to a desktop setup client. This directory
contains the v1 implementation contract used by [AI Desktop Setup](../README.md),
along with its schema and shared test vectors.

AGSP v1 is a candidate contract for implementers. Gateways must implement the
protocol to issue usable setup codes, and client adapters determine which desktop
configurations they accept. All services, dates, credentials, models, packages,
and hashes in the examples are fictional. Predictable fixture secrets must never
be issued in a real service.

- [Protocol](agsp-v1.md): normative behavior, trust boundaries and validation rules.
- [JSON Schema](agsp-v1.schema.json): Draft 2020-12 message structures.
- [Vectors](vectors.json): shared parsing, normalization and state-fixture cases.

Consumers must use the same vector bytes. Record their SHA256 when copying them
into a client or server test suite. Cross-end field changes require coordinated
changes to this specification, schema, vectors and both implementations' tests.

## Schema use

Select `$defs.code` for a decoded installation code, `$defs.bootstrap` for the
bootstrap JSON body, `$defs.session` for bootstrap/GET session success,
`$defs.credentials` for credential success, `$defs.complete` or `$defs.cancel`
for receipts, and `$defs.error` for failures. `$defs.emptyRequest` validates the
other session POST bodies. The root union is a convenience, not an endpoint
validator. Enable `date-time` and `uuid` format assertions.

Use strict JSON decoding before schema validation: reject duplicate keys at any
nesting depth and invalid UTF-8. Schema string lengths count Unicode code points.
Schema patterns are only lexical screens for URLs. Apply the protocol's URI,
byte-size, authorization, expiry, binding, adapter and state rules separately.
Unknown optional fields are ignored as inert data, never mapped to commands,
raw configuration, privileges or trust policies. Invalid known fields reject.
The explicitly forbidden `value` in client-provided credential responses rejects;
error objects have a fixed shape and cannot include discovery data.

A schema-valid opaque ticket may be invalid or expired. Ticket entropy is an
issuer guarantee; proof/session bearers have a separate exact 32-byte encoding
rule. API keys allow standard bearer `+ / = ~` characters without a private
prefix or a standalone arbitrary length limit. Neither schema validation nor
these fixtures proves live authorization, package trust or installation success.

## Vector interface

The top-level object is `{version: 1, cases: [...]}`. Each case contains exactly
`id`, `kind`, `input` and `expected`. IDs are stable, unique descriptive labels.
`kind` is `code`, `url`, `bootstrap`, `session`, `credentials` or `error`.
`input` is a string or an object; `expected` contains `accept` and optionally
`http_status`, `error_code`, and an object `canonical`.

`accept` means the input passes that case's validation layer. For a valid error
response, it is true: the error envelope is understood, not a successful setup.
`http_status` asserts the fixture's server/response status when present. An
`error_code` on a rejected local parsing/response case classifies the rejection;
it does not instruct the client to invent a network response. A rejected server
request fixture asserts that status/code and must reveal no discovery data.
For accepted cases, compare the specified canonical object exactly after
projecting to the known fields. Unknown optional fields disappear from that
projection and can never influence behavior. Normalize omitted `mirrors` to `[]`
before canonical comparison; both omission and an empty array select the official
source. Explicit `null` and non-array values reject. Schema validation does not
insert this semantic default.

| Kind and input shape | Validation and canonical object |
| --- | --- |
| `code`: raw string | Trim only surrounding whitespace, validate the envelope, strict Base64url/UTF-8/JSON, schema and URLs; return known decoded fields with normalized base URLs. No authentication is implied. |
| `url`: `{role, value, bound?}` | `setup_base_url` and `api_base_url` use the base rules; canonical is `{url}`. `binding` also compares normalized `value` and `bound`, preserving API slash and path case. |
| `bootstrap`: direct body object or raw JSON string | Request-body parsing, size, schema and known-field validation only; canonical is the known body. Headers and database state are not supplied in this layer. |
| `bootstrap`: `{request, context}` | Authenticated claim/replay scenario. `request` has `headers` and `body`; canonical is the full expected session configuration. |
| `session`: direct body object or raw JSON string | Configuration response validation; canonical is known session configuration. |
| `session`: `{body, context}` | Configuration plus explicitly supplied adapter constraints; currently `supported_reasoning_effort` lists that fixture's accepted values. It is not a global protocol enum. |
| `session`: `{operation, request, context}` | Stateful `get`, `credentials`, `complete` or `cancel`; canonical is the corresponding session/receipt. `request.session_id` selects the path, `headers` supplies the session bearer, and POST `body` must be `{}`. A string body is raw JSON, never pre-parsed. |
| `credentials`: direct body or `{body, credential_type?, binding?}` | Response structure, bearer syntax and, when supplied, original mode and session/API binding. Canonical is the known response body. |
| `error`: `{http_status, headers, body}` | Fixed error shape, status/code pairing and `no-store`; canonical is the error body. 429 carries the fixture's `Retry-After`; 5xx uses `temporarily_unavailable`. |

IPv6 URL canonicalization is explicit across runtimes: bracketed expanded or
compressed literals use lowercase hexadecimal 16-bit groups without leading
zeros, compress the longest zero run of at least two groups (first on ties), and
render IPv4-mapped tails as hexadecimal groups. Zone/scope identifiers reject in
v1. Preserve paths and ports, and compare canonical addresses for origin/binding;
do not use a URI library's display spelling as the shared canonical value. The
IPv6 URL vectors cover equivalence, mapped tails, compression ties, ports and
scope rejection.

Size-boundary fixtures use `{body, wire_body_bytes}` for bootstrap or credentials.
Serialize `body` as compact UTF-8 JSON (no ASCII escaping), then append ASCII
space bytes until the complete body is exactly `wire_body_bytes` long. This is
valid JSON trailing whitespace, which counts toward the byte cap. Do not trust
a claimed length instead of checking those constructed raw bytes. The schema
applies to `body`, not the fixture wrapper. Installation-code size cases contain
the literal encoded bytes, including inert padding data; do not regenerate them.
Malformed and duplicate-key cases preserve raw strings and must not pass through
an ordinary parser before the strict parser being tested. Duplicate-key and
malformed-UTF-8 negatives are otherwise complete valid messages: dropping
duplicates or replacing invalid UTF-8 would pass the remaining structural and
semantic checks. The separate nonempty-session-POST case tests only the `{}`
body rule and does not stand in for duplicate-key rejection.

## Stateful fixture model

These objects are test metadata, never fields sent over AGSP. Time is fixed;
do not compare fixture dates with the machine clock. `context.bound` is the
server's saved bootstrap binding; compare installation ID, credential type, both
normalized URLs and app ID. `context.session` holds the expected session snapshot
and fixed expiry. `credential_valid` and `authorized` supply authentication and
current account/group/key authorization facts; `supported` supplies preflight.
The fictitious request bearer represents the bound fixture credential when
`credential_valid` is true. It is not a real authentication implementation.

For bootstrap, `record_state` is `unclaimed`, `claimed` or `completed`.
`first_claim_expires_at` applies only to first claim, with `now >= expiry`
rejecting. Claimed/completed fixtures provide the saved `claim_id` and `proof`.
Replay requires the same bound credential, claim and secret within the original
session expiry; a different valid claimant receives 409. A completed replay
returns the same snapshot with `state=completed`. Never extend expiry or create,
rotate or substitute a key. Authenticate before exposing a binding mismatch,
conflict or configuration; expired/unusable sessions return 401, revoked current
permissions return 403, and unsupported authorized configuration returns 422.

For session requests, the saved `proof` must match the request bearer and the
saved session ID. Every stage rechecks authorization and expiry. `get` returns
the same snapshot (including after completion until the original expiry).
`complete` requires `credentials_released` or an already completed session;
completing a `claimed` session is an `invalid_request`. `cancel` returns
`state=canceled`; only its original-secret-authorized idempotent cancel receipt
remains available. Cancellation after completion ends recovery, without changing
local completion facts or revoking the allocated API key.

These vectors are representative contract cases, not exhaustive client/backend
tests. Implementations must additionally test concurrent atomic claim/issuance,
response loss, revocation, snapshot equality, HTTP duplicate headers, protected
Logo constraints, secret redaction, mirrored package trust, original-user/UAC
boundaries, configuration merge/restore, and actual installation. A JSON-readability
check is not a claim of interoperability.

## Basic readability checks

From the repository root:

```sh
python3 -m json.tool protocol/vectors.json > /dev/null
python3 -m json.tool protocol/agsp-v1.schema.json > /dev/null
```

Client and server suites must separately execute the semantic rules and the
stateful fixtures against their implementations using these exact vector bytes.
