# Security Policy

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for security issues. Do not
open a public issue containing secrets, webhook payloads, exploit details, or
other sensitive data.

## Supported versions

HookScope is pre-release software. Security fixes are applied to the current
`main` branch.

## Operational expectations

- Supply `Hooks:Secret` through a deployment secret provider.
- Restrict the inspection and retry API to trusted callers before exposing the
  service outside a development environment.
- Terminate TLS before webhook traffic reaches HookScope.
- Apply payload retention and redaction policies appropriate to the webhook
  sources being received.
