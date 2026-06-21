using HookScope.Configuration;
using HookScope.Domain;
using HookScope.Persistence;
using Microsoft.Extensions.Options;

namespace HookScope.Processing;

public sealed partial class DeliveryWorker(
    DeliveryStore deliveryStore,
    IWebhookEventProcessor processor,
    IOptions<HookOptions> hookOptions,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly TimeSpan pollInterval =
        TimeSpan.FromMilliseconds(hookOptions.Value.WorkerPollIntervalMilliseconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger, pollInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            DeliveryWorkItem? workItem;

            try
            {
                workItem = await deliveryStore.ClaimNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogClaimFailed(logger, exception, pollInterval.TotalMilliseconds);
                await DelayAsync(stoppingToken);
                continue;
            }

            if (workItem is null)
            {
                await DelayAsync(stoppingToken);
                continue;
            }

            await ProcessAsync(workItem, stoppingToken);
        }
    }

    private async Task ProcessAsync(
        DeliveryWorkItem workItem,
        CancellationToken stoppingToken)
    {
        try
        {
            string summary = await processor.ProcessAsync(workItem, stoppingToken);
            await deliveryStore.MarkCompletedAsync(workItem, summary, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogProcessingInterrupted(
                logger,
                workItem.Id,
                workItem.AttemptNumber);
        }
        catch (Exception exception)
        {
            string error =
                $"The webhook event processor rejected delivery '{workItem.Id}' on attempt {workItem.AttemptNumber}: {exception.Message}";

            LogProcessingFailed(
                logger,
                exception,
                workItem.Id,
                workItem.AttemptNumber);

            try
            {
                await deliveryStore.MarkFailedAsync(workItem, error, stoppingToken);
            }
            catch (Exception persistenceException)
            {
                LogFailurePersistenceFailed(
                    logger,
                    persistenceException,
                    workItem.Id,
                    workItem.AttemptNumber);
            }
        }
    }

    private async Task DelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(pollInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "HookScope delivery worker started with a polling interval of {PollingIntervalMilliseconds} milliseconds.")]
    private static partial void LogWorkerStarted(
        ILogger logger,
        double pollingIntervalMilliseconds);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Error,
        Message = "HookScope could not claim the next pending webhook delivery from SQLite. The worker will retry after {PollingIntervalMilliseconds} milliseconds; the database may be unavailable, locked, or malformed.")]
    private static partial void LogClaimFailed(
        ILogger logger,
        Exception exception,
        double pollingIntervalMilliseconds);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Webhook delivery {DeliveryId} attempt {AttemptNumber} was interrupted because HookScope is stopping. Startup recovery will return it to the pending queue.")]
    private static partial void LogProcessingInterrupted(
        ILogger logger,
        string deliveryId,
        int attemptNumber);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Webhook delivery {DeliveryId} failed during event processing on attempt {AttemptNumber}. The delivery remains available for an explicit retry.")]
    private static partial void LogProcessingFailed(
        ILogger logger,
        Exception exception,
        string deliveryId,
        int attemptNumber);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Critical,
        Message = "HookScope could not persist the failure state for webhook delivery {DeliveryId} attempt {AttemptNumber}. The delivery may remain marked as Processing until startup recovery runs.")]
    private static partial void LogFailurePersistenceFailed(
        ILogger logger,
        Exception exception,
        string deliveryId,
        int attemptNumber);
}
