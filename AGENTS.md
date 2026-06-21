# AGENTS.md

## Project

HookScope is a .NET 10 ASP.NET Core webhook-ingestion service. Keep the solution
small: one service project and one integration-test project unless a concrete
deployment, ownership, or reuse boundary earns another project.

## Architecture

- Keep HTTP concerns in `Api`.
- Keep HMAC validation pure and based on the exact request bytes.
- Keep SQLite operations in the concrete `DeliveryStore`; do not add a generic
  repository abstraction without a second real persistence implementation.
- Keep lifecycle transitions around `IWebhookEventProcessor` in
  `DeliveryWorker`.
- Preserve the single delivery lifecycle for future adapters, including MCP.
- Treat the planned MCP server as an adapter over shared application operations,
  never as a second business-logic path.

Before adding a manager, coordinator, repository, service layer, project, or
package, explain the current duplication or near-term use case it removes and
which simpler extension was considered first.

## Safety

- Never commit webhook secrets, `.env` files, payload databases, or local
  configuration overrides.
- Never write webhook payload content, signatures, or secrets to logs.
- Keep error and log messages descriptive enough to identify the delivery,
  operation, and likely failure cause without exposing sensitive content.
- Preserve constant-time signature comparison.
- Keep retry mutation explicit and restricted to failed deliveries.

## Data

- Preserve exact-duplicate idempotency and conflicting-payload detection.
- Preserve attempt history when retrying.
- Add schema migration versioning before changing existing persisted columns or
  constraints.
- Keep restart recovery behavior covered when processing-state code changes.

## Validation

Run .NET commands serially:

```zsh
dotnet restore
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build --no-restore
```

Do not run multiple restore, format, build, or test commands concurrently.
Warnings are errors. Fix analyzer findings rather than weakening the repository
gate.

## Git

Use focused commits with `<scope>: <imperative summary>`. Do not publish a NuGet
package or deploy HookScope unless Gale explicitly asks.
