using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookScope.Api;
using HookScope.Domain;

namespace HookScope.Tests;

public sealed class DeliveryApiTests : IClassFixture<HookScopeApplicationFactory>
{
    private readonly HttpClient client;

    public DeliveryApiTests(HookScopeApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task IngestionRejectsInvalidSignatureWithoutPersistingDelivery()
    {
        using HttpRequestMessage request = CreateRequest(
            "invalid-signature",
            "push",
            """{"ref":"refs/heads/main"}""",
            signatureOverride: "sha256=not-a-valid-signature");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using HttpRequestMessage lookupRequest =
            CreateOperatorRequest(HttpMethod.Get, "/api/deliveries/invalid-signature");
        using HttpResponseMessage lookup = await client.SendAsync(lookupRequest);
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task DeliveryInspectionRequiresOperatorToken()
    {
        using HttpResponseMessage response = await client.GetAsync("/api/deliveries");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryInspectionIsDisabledWhenOperatorTokenIsNotConfigured()
    {
        using HookScopeApplicationFactory factory =
            HookScopeApplicationFactory.CreateWithoutOperatorToken();
        using HttpClient localClient = factory.CreateClient();

        using HttpRequestMessage request =
            CreateOperatorRequest(HttpMethod.Get, "/api/deliveries");
        using HttpResponseMessage response = await localClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryRetryRequiresOperatorToken()
    {
        using HttpResponseMessage response =
            await client.PostAsync("/api/deliveries/any-delivery/retries", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestionRejectsSignedMalformedJson()
    {
        using HttpRequestMessage request = CreateRequest(
            "malformed-json",
            "push",
            """{"ref":""");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateDeliveryReturnsExistingStateWithoutCreatingAnotherAttempt()
    {
        const string payload = """{"ref":"refs/heads/main","action":"synchronize"}""";

        using HttpResponseMessage first =
            await client.SendAsync(CreateRequest("duplicate-delivery", "push", payload));
        using HttpResponseMessage second =
            await client.SendAsync(CreateRequest("duplicate-delivery", "push", payload));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        IngestionResponse duplicate =
            await second.Content.ReadFromJsonAsync<IngestionResponse>()
            ?? throw new InvalidOperationException("Duplicate response body was empty.");
        Assert.True(duplicate.Duplicate);

        DeliveryDetailsResponse completed =
            await WaitForStatusAsync("duplicate-delivery", DeliveryStatus.Completed);
        Assert.Single(completed.Attempts);
    }

    [Fact]
    public async Task ReusedDeliveryIdentifierWithDifferentContentReturnsConflict()
    {
        using HttpResponseMessage first = await client.SendAsync(
            CreateRequest("conflicting-delivery", "push", """{"ref":"refs/heads/main"}"""));
        using HttpResponseMessage conflict = await client.SendAsync(
            CreateRequest("conflicting-delivery", "push", """{"ref":"refs/heads/release"}"""));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task AcceptedDeliveryMovesThroughBackgroundProcessingToCompleted()
    {
        using HttpResponseMessage response = await client.SendAsync(
            CreateRequest(
                "successful-delivery",
                "issues",
                """{"action":"opened","issue":{"number":42}}"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        DeliveryDetailsResponse completed =
            await WaitForStatusAsync("successful-delivery", DeliveryStatus.Completed);

        Assert.Equal(1, completed.Delivery.AttemptCount);
        DeliveryAttempt attempt = Assert.Single(completed.Attempts);
        Assert.Equal(AttemptStatus.Completed, attempt.Status);
        Assert.NotNull(attempt.Summary);
        Assert.Null(completed.Delivery.LastError);
    }

    [Fact]
    public async Task FailedDeliveryCanBeExplicitlyRetriedAndPreservesAttemptHistory()
    {
        using HttpResponseMessage ingestion = await client.SendAsync(
            CreateRequest(
                "retryable-delivery",
                "hookscope.failure",
                """{"action":"exercise-retry"}"""));
        Assert.Equal(HttpStatusCode.Accepted, ingestion.StatusCode);

        DeliveryDetailsResponse failed =
            await WaitForStatusAsync("retryable-delivery", DeliveryStatus.Failed);
        Assert.Equal(1, failed.Delivery.AttemptCount);
        Assert.Contains("intentionally failed", failed.Delivery.LastError);

        using HttpRequestMessage retryRequest =
            CreateOperatorRequest(HttpMethod.Post, "/api/deliveries/retryable-delivery/retries");
        using HttpResponseMessage retry = await client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        DeliveryDetailsResponse completed =
            await WaitForStatusAsync(
                "retryable-delivery",
                DeliveryStatus.Completed,
                expectedAttemptCount: 2);

        Assert.Collection(
            completed.Attempts,
            first => Assert.Equal(AttemptStatus.Failed, first.Status),
            second => Assert.Equal(AttemptStatus.Completed, second.Status));
    }

    [Fact]
    public async Task OpenApiDocumentIsExposed()
    {
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string document = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/deliveries", document, StringComparison.Ordinal);
    }

    private async Task<DeliveryDetailsResponse> WaitForStatusAsync(
        string deliveryId,
        DeliveryStatus expectedStatus,
        int? expectedAttemptCount = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!timeout.IsCancellationRequested)
        {
            DeliveryDetailsResponse? details =
                await SendOperatorJsonAsync<DeliveryDetailsResponse>(
                    HttpMethod.Get,
                    $"/api/deliveries/{deliveryId}",
                    timeout.Token);

            if (details?.Delivery.Status == expectedStatus
                && (expectedAttemptCount is null
                    || details.Delivery.AttemptCount == expectedAttemptCount))
            {
                return details;
            }

            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException(
            $"Delivery '{deliveryId}' did not reach {expectedStatus} within five seconds.");
    }

    private static HttpRequestMessage CreateRequest(
        string deliveryId,
        string eventName,
        string payload,
        string? signatureOverride = null)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string signature = signatureOverride ?? Sign(payloadBytes);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/deliveries")
        {
            Content = new ByteArrayContent(payloadBytes),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-Hub-Signature-256", signature);

        return request;
    }

    private async Task<T?> SendOperatorJsonAsync<T>(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateOperatorRequest(method, requestUri);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static HttpRequestMessage CreateOperatorRequest(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-HookScope-Operator-Token", HookScopeApplicationFactory.OperatorToken);
        return request;
    }

    private static string Sign(byte[] payload)
    {
        byte[] secret = Encoding.UTF8.GetBytes(HookScopeApplicationFactory.Secret);
        return $"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(secret, payload))}";
    }
}
