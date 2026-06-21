using System.Text.Json;
using HookScope.Domain;

namespace HookScope.Processing;

public sealed partial class LoggingWebhookEventProcessor(
    ILogger<LoggingWebhookEventProcessor> logger) : IWebhookEventProcessor
{
    public Task<string> ProcessAsync(
        DeliveryWorkItem delivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using JsonDocument payload = JsonDocument.Parse(delivery.Payload);
        int rootPropertyCount = payload.RootElement.EnumerateObject().Count();
        string summary =
            $"Processed '{delivery.EventName}' delivery with {rootPropertyCount} root payload properties.";

        LogProcessedDelivery(
            logger,
            delivery.Id,
            delivery.EventName,
            delivery.AttemptNumber);

        return Task.FromResult(summary);
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Processed webhook delivery {DeliveryId} for event {EventName} on attempt {AttemptNumber}. Payload content was not written to logs.")]
    private static partial void LogProcessedDelivery(
        ILogger logger,
        string deliveryId,
        string eventName,
        int attemptNumber);
}
