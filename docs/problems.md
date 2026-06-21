# Problem responses

HookScope returns standard Problem Details JSON for request failures. Each
problem type is stable enough for documentation and operator diagnosis, but
clients should primarily branch on HTTP status and documented response fields.

## missing-delivery-id

The request omitted `X-GitHub-Delivery`, so HookScope cannot provide idempotent
ingestion.

## missing-event-name

The request omitted `X-GitHub-Event`, so HookScope cannot identify the event
type.

## payload-too-large

The request body exceeded `Hooks:MaximumPayloadBytes`.

## invalid-signature

`X-Hub-Signature-256` was missing, malformed, or did not match the exact request
body under the configured secret.

## invalid-payload-shape

The signed body was valid JSON but its root value was not an object.

## malformed-payload

The signed body was not valid JSON.

## delivery-payload-conflict

The supplied delivery identifier already belongs to a different event name or
payload digest. HookScope preserved the original delivery.

## invalid-delivery-limit

The recent-deliveries limit was outside the supported range of 1 through 100.

## delivery-not-found

No stored delivery matched the requested identifier.

## delivery-not-retryable

The delivery exists but is not in `Failed` status. HookScope retries only failed
deliveries.
