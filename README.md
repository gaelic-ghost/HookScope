# HookScope

HookScope is a small ASP.NET Core service for receiving GitHub-style webhook
events and inspecting their processing state through a JSON API.

The initial vertical slice demonstrates:

- HMAC-SHA256 signature validation using `X-Hub-Signature-256`
- idempotent ingestion keyed by `X-GitHub-Delivery`
- conflict detection when a delivery identifier is reused with different content
- durable SQLite persistence for deliveries and processing-attempt history
- restart recovery for work interrupted during processing
- explicit failed-delivery retries
- structured logging that never writes webhook payload content
- generated OpenAPI documentation
- integration tests against the real HTTP host and background worker

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- a system SQLite library, available by default on macOS and the Ubuntu CI image

The repository pins SDK feature band `10.0.300` and allows forward movement
within later .NET 10 feature bands through `global.json`.

## Run locally

Set local webhook and operator secrets through configuration. Do not add these
values to `appsettings.json` or commit them.

```zsh
export Hooks__Secret='replace-with-a-long-random-local-secret'
export Hooks__OperatorToken='replace-with-a-different-long-random-local-token'
dotnet restore
dotnet run --project src/HookScope
```

HookScope creates `src/HookScope/data/hookscope.db` by default. The development
launch profile listens on `http://localhost:5115`.

OpenAPI is available at:

```text
http://localhost:5115/openapi/v1.json
```

## Send a signed delivery

```zsh
payload='{"action":"opened","issue":{"number":42}}'
secret="$Hooks__Secret"
signature="$(printf '%s' "$payload" | openssl dgst -sha256 -hmac "$secret" -hex | awk '{print $2}')"

curl --fail-with-body \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'X-GitHub-Delivery: demo-delivery-1' \
  --header 'X-GitHub-Event: issues' \
  --header "X-Hub-Signature-256: sha256=$signature" \
  --data "$payload" \
  http://localhost:5115/api/deliveries
```

Inspect recent deliveries:

```zsh
curl --fail-with-body \
  --header "X-HookScope-Operator-Token: $Hooks__OperatorToken" \
  http://localhost:5115/api/deliveries

curl --fail-with-body \
  --header "X-HookScope-Operator-Token: $Hooks__OperatorToken" \
  http://localhost:5115/api/deliveries/demo-delivery-1
```

Only failed deliveries are eligible for retry:

```zsh
curl --fail-with-body \
  --request POST \
  --header "X-HookScope-Operator-Token: $Hooks__OperatorToken" \
  http://localhost:5115/api/deliveries/demo-delivery-1/retries
```

Delivery inspection and retry endpoints are disabled until
`Hooks:OperatorToken` is configured, and they return `401 Unauthorized` when the
`X-HookScope-Operator-Token` header is missing or wrong. This intentionally
keeps operator-facing state and retry mutation out of unauthenticated traffic.

## API behavior

| Route | Behavior |
| --- | --- |
| `POST /api/deliveries` | Validates, deduplicates, persists, and queues a delivery |
| `GET /api/deliveries?limit=25` | Lists recent delivery states for callers with the operator token |
| `GET /api/deliveries/{deliveryId}` | Returns one delivery and all processing attempts for callers with the operator token |
| `POST /api/deliveries/{deliveryId}/retries` | Requeues a failed delivery for callers with the operator token |
| `GET /healthz` | Reports process health |
| `GET /openapi/v1.json` | Returns the generated OpenAPI document |

Errors use Problem Details responses with descriptive titles, details, and
stable links documented in [docs/problems.md](docs/problems.md).

## Validate

Run the commands serially:

```zsh
dotnet restore
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build --no-restore
```

## Design

See [docs/architecture.md](docs/architecture.md) for boundaries and tradeoffs,
and [docs/roadmap.md](docs/roadmap.md) for planned milestones, including an MCP
adapter over the same application operations used by HTTP.

## License

HookScope is available under the [Apache License 2.0](LICENSE).
