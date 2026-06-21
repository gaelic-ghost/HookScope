# Architecture

## Classification

HookScope's initial architecture is a durable building-block change: it creates
one reusable delivery lifecycle that HTTP uses today and future adapters can use
without duplicating signature, persistence, processing, or retry rules.

The solution deliberately contains only two projects:

- `HookScope`: the ASP.NET Core host, application behavior, SQLite persistence,
  and background worker
- `HookScope.Tests`: integration tests that exercise the real host

A separate domain or infrastructure project is not currently earned. The code
already has narrow folders and types, while another assembly would add
navigation and dependency ceremony without creating a useful deployment,
ownership, or reuse boundary.

## Request and processing flow

1. The ingestion endpoint reads a bounded request body.
2. `GitHubSignatureValidator` validates the exact body bytes with HMAC-SHA256.
3. The endpoint verifies that the signed payload is a JSON object.
4. `DeliveryStore` inserts the delivery with `Pending` status, or returns the
   existing delivery for an exact duplicate.
5. Reusing a delivery identifier with a different event name or payload digest
   returns a conflict and never overwrites the original record.
6. `DeliveryWorker` atomically claims the oldest pending delivery and creates a
   processing-attempt row.
7. `IWebhookEventProcessor` performs the event-specific work.
8. The worker persists `Completed` or `Failed` status and closes the attempt.
9. A failed delivery can be returned to `Pending` only through the explicit
   retry operation.

The background worker is earned by the product behavior: it makes accepted
delivery processing asynchronous, exposes meaningful intermediate state, and
keeps pending work durable across process restarts. An in-memory queue would
lose accepted work when the process stops, so the worker polls SQLite directly.

## Boundaries

### HTTP adapter

`DeliveryEndpoints` owns headers, status codes, Problem Details responses, and
API response shapes. It does not own persistence SQL or event-processing rules.

### Signature validation

`GitHubSignatureValidator` is a pure cryptographic boundary. It uses
constant-time comparison and accepts the exact payload bytes received over HTTP.

### Persistence

`DeliveryStore` is a concrete SQLite component rather than a generic repository.
There is one persistence implementation and no current alternate store. Its
methods are shaped around actual HookScope operations: ingest, inspect, claim,
complete, fail, recover, and retry.

### Processing

`IWebhookEventProcessor` is the only interface in the initial application. It is
earned by two current callers: the production logging processor and the
deterministic integration-test processor used to prove failure and retry
behavior. The background worker owns lifecycle transitions around that
processor.

## Persistence model

`deliveries` stores the current state and payload needed for processing.
`delivery_attempts` is append-oriented history for each processing attempt.

Delivery states:

- `Pending`: accepted or explicitly requeued and waiting to be claimed
- `Processing`: claimed by the worker with an open attempt
- `Completed`: processing succeeded
- `Failed`: processing failed and is eligible for explicit retry

If HookScope starts with a delivery left in `Processing`, startup recovery marks
the interrupted attempt as failed and returns the delivery to `Pending`.

SQLite is accessed through `Microsoft.Data.Sqlite.Core` and the operating
system's SQLite library. This avoids shipping the vulnerable bundled native
library that NuGet reported during bootstrap.

## Configuration and secrets

`Hooks:Secret` is required and must be supplied through an environment variable,
user secrets, or another deployment configuration provider. HookScope refuses
to start when it is absent or too short.

Safe defaults are committed for payload size, polling interval, logging, and the
local SQLite path. Webhook payloads are persisted because processing requires
them, but their content is never written to structured logs.

## Future MCP adapter

The planned MCP server is an adapter over the same application operations used
by HTTP, not a second business-logic path. Its practical purpose is to let an
MCP client:

- inspect recent webhook deliveries
- retrieve one delivery's state and processing history
- summarize recent failures without exposing full payloads by default
- retry an eligible failed delivery through an explicit mutating tool

Before implementation, the application operations currently exposed through
`DeliveryStore` and endpoint composition should be factored only as far as
needed for both adapters to call the same ingest-independent query and retry
behavior.

The MCP design must include:

- authentication and authorization appropriate to the transport
- default redaction of payloads, signatures, secrets, and sensitive headers
- bounded list and history resources
- explicit user confirmation before a retry tool mutates delivery state
- audit logging for mutating tool calls without logging sensitive payload data
