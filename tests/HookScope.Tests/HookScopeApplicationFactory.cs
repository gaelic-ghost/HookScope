using HookScope.Configuration;
using HookScope.Domain;
using HookScope.Processing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookScope.Tests;

public sealed class HookScopeApplicationFactory : WebApplicationFactory<Program>
{
    public const string Secret = "hookscope-integration-test-secret";

    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"hookscope-tests-{Guid.NewGuid():N}.db");

    public ScriptedWebhookEventProcessor Processor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(
            (_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{DatabaseOptions.SectionName}:ConnectionString"] =
                            $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared",
                        [$"{HookOptions.SectionName}:Secret"] = Secret,
                        [$"{HookOptions.SectionName}:WorkerPollIntervalMilliseconds"] = "20",
                    });
            });

        builder.ConfigureServices(
            services =>
            {
                services.RemoveAll<IWebhookEventProcessor>();
                services.AddSingleton<IWebhookEventProcessor>(Processor);
            });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (string path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

public sealed class ScriptedWebhookEventProcessor : IWebhookEventProcessor
{
    private readonly Dictionary<string, int> attemptCounts = new(StringComparer.Ordinal);
    private readonly Lock attemptLock = new();

    public Task<string> ProcessAsync(
        DeliveryWorkItem delivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int observedAttempt;
        lock (attemptLock)
        {
            attemptCounts.TryGetValue(delivery.Id, out int priorAttempts);
            observedAttempt = priorAttempts + 1;
            attemptCounts[delivery.Id] = observedAttempt;
        }

        if (delivery.EventName == "hookscope.failure" && observedAttempt == 1)
        {
            throw new InvalidOperationException(
                "The scripted integration-test processor intentionally failed the first attempt.");
        }

        return Task.FromResult(
            $"Integration-test processor completed attempt {delivery.AttemptNumber}.");
    }
}
