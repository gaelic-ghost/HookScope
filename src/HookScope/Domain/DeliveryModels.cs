using System.Text.Json.Serialization;

namespace HookScope.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryStatus>))]
public enum DeliveryStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<AttemptStatus>))]
public enum AttemptStatus
{
    Processing,
    Completed,
    Failed,
}

public sealed record WebhookDelivery(
    string Id,
    string EventName,
    string Payload,
    string PayloadSha256,
    DeliveryStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt,
    int AttemptCount,
    string? LastError,
    string? ProcessingSummary);

public sealed record DeliveryAttempt(
    long Id,
    int AttemptNumber,
    AttemptStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? Summary);

public sealed record DeliveryDetails(
    WebhookDelivery Delivery,
    IReadOnlyList<DeliveryAttempt> Attempts);

public sealed record DeliveryWorkItem(
    string Id,
    string EventName,
    string Payload,
    int AttemptNumber);

public enum IngestionOutcome
{
    Created,
    Duplicate,
    PayloadConflict,
}

public sealed record IngestionResult(
    IngestionOutcome Outcome,
    WebhookDelivery Delivery);

public enum RetryOutcome
{
    Queued,
    NotFound,
    NotEligible,
}
