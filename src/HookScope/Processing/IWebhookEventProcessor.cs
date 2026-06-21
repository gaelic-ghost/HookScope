using HookScope.Domain;

namespace HookScope.Processing;

public interface IWebhookEventProcessor
{
    Task<string> ProcessAsync(
        DeliveryWorkItem delivery,
        CancellationToken cancellationToken);
}
