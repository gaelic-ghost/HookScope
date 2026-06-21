using System.Text.Json.Serialization;
using HookScope.Api;
using HookScope.Configuration;
using HookScope.Persistence;
using HookScope.Processing;

SqliteRuntime.Initialize();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<HookOptions>()
    .Bind(builder.Configuration.GetSection(HookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeliveryStore>();
builder.Services.AddSingleton<IWebhookEventProcessor, LoggingWebhookEventProcessor>();
builder.Services.AddHostedService<DeliveryWorker>();

WebApplication app = builder.Build();

await app.Services.GetRequiredService<DeliveryStore>().InitializeAsync();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapDeliveryEndpoints();

app.MapGet(
        "/",
        () => Results.Ok(
            new
            {
                service = "HookScope",
                description = "Signed webhook ingestion and processing-state API.",
                openApi = "/openapi/v1.json",
            }))
    .ExcludeFromDescription();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .WithName("GetHealth")
    .WithSummary("Reports whether the HookScope process is running.");

await app.RunAsync();

public partial class Program;
