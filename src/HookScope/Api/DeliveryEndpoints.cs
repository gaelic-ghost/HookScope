using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookScope.Configuration;
using HookScope.Domain;
using HookScope.Persistence;
using HookScope.Security;
using Microsoft.Extensions.Options;

namespace HookScope.Api;

public static partial class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/deliveries")
            .WithTags("Deliveries");

        group.MapPost("/", IngestAsync)
            .WithName("IngestDelivery")
            .WithSummary("Validates and queues a GitHub-style webhook delivery.")
            .Accepts<string>("application/json")
            .Produces<IngestionResponse>(StatusCodes.Status202Accepted)
            .Produces<IngestionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/", GetRecentAsync)
            .WithName("GetRecentDeliveries")
            .WithSummary("Returns recent webhook deliveries and their current processing state.")
            .Produces<IReadOnlyList<DeliveryResponse>>();

        group.MapGet("/{deliveryId}", GetByIdAsync)
            .WithName("GetDelivery")
            .WithSummary("Returns one webhook delivery and its processing-attempt history.")
            .Produces<DeliveryDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{deliveryId}/retries", RetryAsync)
            .WithName("RetryDelivery")
            .WithSummary("Queues an explicit retry for a failed webhook delivery.")
            .Produces<RetryResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> IngestAsync(
        HttpRequest request,
        DeliveryStore deliveryStore,
        IOptions<HookOptions> hookOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger("HookScope.Api.Ingestion");
        HookOptions options = hookOptions.Value;

        if (!TryGetRequiredHeader(request, "X-GitHub-Delivery", out string deliveryId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Missing delivery identifier",
                "The X-GitHub-Delivery header is required so HookScope can ingest the webhook idempotently.",
                "missing-delivery-id");
        }

        if (!TryGetRequiredHeader(request, "X-GitHub-Event", out string eventName))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Missing event name",
                "The X-GitHub-Event header is required so HookScope can identify the webhook event type.",
                "missing-event-name");
        }

        byte[] payload;
        try
        {
            payload = await ReadPayloadAsync(
                request,
                options.MaximumPayloadBytes,
                cancellationToken);
        }
        catch (PayloadTooLargeException)
        {
            return Problem(
                StatusCodes.Status413PayloadTooLarge,
                "Webhook payload is too large",
                $"The request body exceeded HookScope's configured limit of {options.MaximumPayloadBytes} bytes.",
                "payload-too-large");
        }

        string? signature = request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!GitHubSignatureValidator.IsValid(payload, options.Secret, signature))
        {
            LogInvalidSignature(
                logger,
                deliveryId,
                eventName);

            return Problem(
                StatusCodes.Status401Unauthorized,
                "Webhook signature is invalid",
                "X-Hub-Signature-256 must contain a valid HMAC-SHA256 signature for the exact request body.",
                "invalid-signature");
        }

        string payloadText = Encoding.UTF8.GetString(payload);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Webhook payload must be a JSON object",
                    "The signed request body was valid JSON, but GitHub-style webhook payloads must use an object at the document root.",
                    "invalid-payload-shape");
            }
        }
        catch (JsonException exception)
        {
            LogMalformedPayload(
                logger,
                exception,
                deliveryId,
                eventName);

            return Problem(
                StatusCodes.Status400BadRequest,
                "Webhook payload is malformed",
                "The signed request body could not be parsed as JSON. Check for incomplete data, invalid encoding, or JSON syntax errors.",
                "malformed-payload");
        }

        string payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        IngestionResult result = await deliveryStore.IngestAsync(
            deliveryId,
            eventName,
            payloadText,
            payloadSha256,
            cancellationToken);

        if (result.Outcome == IngestionOutcome.PayloadConflict)
        {
            LogPayloadConflict(
                logger,
                deliveryId);

            return Problem(
                StatusCodes.Status409Conflict,
                "Delivery identifier conflicts with stored content",
                "This X-GitHub-Delivery value already belongs to a different event name or payload. HookScope will not overwrite the original delivery.",
                "delivery-payload-conflict");
        }

        bool isDuplicate = result.Outcome == IngestionOutcome.Duplicate;
        var response = new IngestionResponse(
            result.Delivery.Id,
            result.Delivery.Status,
            isDuplicate,
            $"/api/deliveries/{Uri.EscapeDataString(result.Delivery.Id)}");

        if (isDuplicate)
        {
            LogDuplicateDelivery(
                logger,
                deliveryId);
            return Results.Ok(response);
        }

        LogAcceptedDelivery(
            logger,
            deliveryId,
            eventName);
        return Results.Accepted(response.Location, response);
    }

    private static async Task<IResult> GetRecentAsync(
        DeliveryStore deliveryStore,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Delivery limit is outside the supported range",
                "The limit query parameter must be between 1 and 100.",
                "invalid-delivery-limit");
        }

        IReadOnlyList<WebhookDelivery> deliveries =
            await deliveryStore.GetRecentAsync(limit, cancellationToken);

        return Results.Ok(deliveries.Select(ToResponse));
    }

    private static async Task<IResult> GetByIdAsync(
        string deliveryId,
        DeliveryStore deliveryStore,
        CancellationToken cancellationToken)
    {
        DeliveryDetails? details =
            await deliveryStore.GetDetailsAsync(deliveryId, cancellationToken);

        return details is null
            ? Problem(
                StatusCodes.Status404NotFound,
                "Webhook delivery was not found",
                $"HookScope has no stored webhook delivery with identifier '{deliveryId}'.",
                "delivery-not-found")
            : Results.Ok(
                new DeliveryDetailsResponse(
                    ToResponse(details.Delivery),
                    details.Attempts));
    }

    private static async Task<IResult> RetryAsync(
        string deliveryId,
        DeliveryStore deliveryStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        RetryOutcome outcome =
            await deliveryStore.QueueRetryAsync(deliveryId, cancellationToken);

        if (outcome == RetryOutcome.NotFound)
        {
            return Problem(
                StatusCodes.Status404NotFound,
                "Webhook delivery was not found",
                $"HookScope has no stored webhook delivery with identifier '{deliveryId}'.",
                "delivery-not-found");
        }

        if (outcome == RetryOutcome.NotEligible)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                "Webhook delivery is not eligible for retry",
                "Only deliveries in Failed status can be retried. Pending, Processing, and Completed deliveries keep their current state.",
                "delivery-not-retryable");
        }

        ILogger logger = loggerFactory.CreateLogger("HookScope.Api.Retry");
        LogQueuedRetry(logger, deliveryId);

        return Results.Accepted(
            $"/api/deliveries/{Uri.EscapeDataString(deliveryId)}",
            new RetryResponse(
                deliveryId,
                DeliveryStatus.Pending,
                $"/api/deliveries/{Uri.EscapeDataString(deliveryId)}"));
    }

    private static DeliveryResponse ToResponse(WebhookDelivery delivery) =>
        new(
            delivery.Id,
            delivery.EventName,
            delivery.PayloadSha256,
            delivery.Status,
            delivery.ReceivedAt,
            delivery.UpdatedAt,
            delivery.AttemptCount,
            delivery.LastError,
            delivery.ProcessingSummary);

    private static bool TryGetRequiredHeader(
        HttpRequest request,
        string headerName,
        out string value)
    {
        value = request.Headers[headerName].FirstOrDefault()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static async Task<byte[]> ReadPayloadAsync(
        HttpRequest request,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumPayloadBytes)
        {
            throw new PayloadTooLargeException();
        }

        using var payload = new MemoryStream(
            request.ContentLength is > 0 and <= int.MaxValue
                ? (int)request.ContentLength.Value
                : 0);

        byte[] buffer = new byte[81_920];
        while (true)
        {
            int bytesRead = await request.Body.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (payload.Length + bytesRead > maximumPayloadBytes)
            {
                throw new PayloadTooLargeException();
            }

            await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return payload.ToArray();
    }

    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        string problemCode) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            type: $"https://github.com/gaelic-ghost/HookScope/blob/main/docs/problems.md#{problemCode}");

    private sealed class PayloadTooLargeException : Exception;

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Rejected webhook delivery {DeliveryId} for event {EventName} because X-Hub-Signature-256 was missing, malformed, or did not match the request body.")]
    private static partial void LogInvalidSignature(
        ILogger logger,
        string deliveryId,
        string eventName);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Rejected signed webhook delivery {DeliveryId} for event {EventName} because the request body was not valid JSON.")]
    private static partial void LogMalformedPayload(
        ILogger logger,
        Exception exception,
        string deliveryId,
        string eventName);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Rejected webhook delivery {DeliveryId} because the identifier was already stored with a different event name or payload digest.")]
    private static partial void LogPayloadConflict(
        ILogger logger,
        string deliveryId);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Returned the existing state for duplicate webhook delivery {DeliveryId}. No second processing attempt was queued.")]
    private static partial void LogDuplicateDelivery(
        ILogger logger,
        string deliveryId);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Information,
        Message = "Accepted webhook delivery {DeliveryId} for event {EventName} and persisted it with Pending status.")]
    private static partial void LogAcceptedDelivery(
        ILogger logger,
        string deliveryId,
        string eventName);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Information,
        Message = "Queued an explicit retry for failed webhook delivery {DeliveryId}.")]
    private static partial void LogQueuedRetry(
        ILogger logger,
        string deliveryId);
}

public sealed record IngestionResponse(
    string DeliveryId,
    DeliveryStatus Status,
    bool Duplicate,
    string Location);

public sealed record DeliveryResponse(
    string DeliveryId,
    string EventName,
    string PayloadSha256,
    DeliveryStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt,
    int AttemptCount,
    string? LastError,
    string? ProcessingSummary);

public sealed record DeliveryDetailsResponse(
    DeliveryResponse Delivery,
    IReadOnlyList<DeliveryAttempt> Attempts);

public sealed record RetryResponse(
    string DeliveryId,
    DeliveryStatus Status,
    string Location);
