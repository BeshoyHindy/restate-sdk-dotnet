# Security Policy

## Supported Versions

Only the latest release receives security fixes. Pre-1.0 versions do not receive
backported patches — upgrade to the newest release to pick up fixes.

| Version        | Supported |
|----------------|-----------|
| latest release | yes       |
| older releases | no        |

## Reporting a Vulnerability

Please do not open a public issue for security problems.

Report vulnerabilities through
[GitHub private vulnerability reporting](https://github.com/BeshoyHindy/restate-sdk-dotnet/security/advisories/new).
You will get an acknowledgement within 72 hours. Once the report is confirmed, a fix
is developed privately and released together with a security advisory crediting you
(unless you prefer otherwise).

Areas of particular interest for this SDK:

- Request identity verification (signature validation on incoming requests)
- Serialization of untrusted payloads (handler inputs, awakeable payloads, state)
- The wire-protocol parser (frame handling on the invocation stream)

## Scope

This policy covers the packages published from this repository: `Restate.Sdk`,
`Restate.Sdk.Testing`, and `Restate.Sdk.Lambda`. Vulnerabilities in the Restate
server itself should be reported to the [Restate project](https://github.com/restatedev/restate/security).
