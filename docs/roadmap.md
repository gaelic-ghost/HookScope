# Roadmap

## Completed: initial vertical slice

- [x] Pin the supported .NET 10 SDK policy
- [x] Validate GitHub-style HMAC-SHA256 signatures
- [x] Reject malformed payloads and invalid signatures explicitly
- [x] Persist deliveries and processing-attempt history in SQLite
- [x] Deduplicate exact retries by delivery identifier
- [x] Reject conflicting reuse of a delivery identifier
- [x] Process accepted deliveries through a durable background queue
- [x] Recover processing work interrupted by service restart
- [x] Expose recent delivery, delivery detail, and retry JSON endpoints
- [x] Guard delivery inspection and retry endpoints with an operator token
- [x] Generate OpenAPI
- [x] Cover success, failure, duplicate, conflict, malformed, retry, and
  documentation behavior with integration tests

## Next: production hardening

- [ ] Replace the bootstrap operator token with deployment-appropriate
  authentication and authorization when a concrete hosting target is selected
- [ ] Add retention and payload-redaction policies
- [ ] Add pagination with stable cursors
- [ ] Add configurable retry limits and backoff for automatic retry policies
- [ ] Add readiness checks that verify SQLite is writable
- [ ] Add metrics for queue depth, processing duration, failures, and retries
- [ ] Add schema migration versioning before the persistence model changes

## Future: MCP server adapter

Add an MCP server surface only after authentication and redaction policies are
settled.

The MCP adapter should reuse the same application operations as the HTTP API and
provide:

- a bounded resource for recent deliveries
- a delivery resource containing status and processing history
- a read-only tool that summarizes failures with sensitive content redacted
- a mutating retry tool limited to eligible failed deliveries

The retry tool must require explicit confirmation before changing state.
Authentication must map callers to allowed operations, and resources must redact
payloads, signatures, secrets, and sensitive headers by default. MCP-specific
code should translate protocol inputs and outputs only; it must not recreate
delivery lifecycle or retry rules.

## Later: deployment and operations

- [ ] Add a container image after a concrete hosting target is selected
- [ ] Document reverse-proxy and TLS expectations
- [ ] Add backup and restore procedures for SQLite
- [ ] Add deployment-specific secret management guidance

HookScope does not publish a NuGet package and has no live deployment in the
bootstrap milestone.
