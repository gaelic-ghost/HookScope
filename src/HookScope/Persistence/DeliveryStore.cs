using System.Globalization;
using HookScope.Configuration;
using HookScope.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HookScope.Persistence;

public sealed partial class DeliveryStore(
    IOptions<DatabaseOptions> databaseOptions,
    TimeProvider timeProvider,
    ILogger<DeliveryStore> logger)
{
    private readonly string connectionString = databaseOptions.Value.ConnectionString;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseDirectory();
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS deliveries (
                id TEXT PRIMARY KEY,
                event_name TEXT NOT NULL,
                payload TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                status TEXT NOT NULL,
                received_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                processing_summary TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS delivery_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                delivery_id TEXT NOT NULL,
                attempt_number INTEGER NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                error TEXT NULL,
                summary TEXT NULL,
                FOREIGN KEY (delivery_id) REFERENCES deliveries(id) ON DELETE CASCADE,
                UNIQUE (delivery_id, attempt_number)
            );

            CREATE INDEX IF NOT EXISTS ix_deliveries_processing_queue
                ON deliveries(status, received_at);

            CREATE INDEX IF NOT EXISTS ix_delivery_attempts_delivery
                ON delivery_attempts(delivery_id, attempt_number);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        int recoveredCount = await RecoverInterruptedDeliveriesAsync(connection, cancellationToken);

        if (recoveredCount > 0)
        {
            LogRecoveredDeliveries(logger, recoveredCount);
        }
    }

    public async Task<IngestionResult> IngestAsync(
        string id,
        string eventName,
        string payload,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT OR IGNORE INTO deliveries (
                id,
                event_name,
                payload,
                payload_sha256,
                status,
                received_at,
                updated_at,
                attempt_count)
            VALUES (
                $id,
                $eventName,
                $payload,
                $payloadSha256,
                'Pending',
                $receivedAt,
                $updatedAt,
                0);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$eventName", eventName);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$payloadSha256", payloadSha256);
        insert.Parameters.AddWithValue("$receivedAt", Format(now));
        insert.Parameters.AddWithValue("$updatedAt", Format(now));

        int insertedRows = await insert.ExecuteNonQueryAsync(cancellationToken);
        WebhookDelivery delivery = await GetRequiredDeliveryAsync(connection, id, cancellationToken);

        if (insertedRows == 1)
        {
            return new IngestionResult(IngestionOutcome.Created, delivery);
        }

        IngestionOutcome duplicateOutcome =
            string.Equals(delivery.PayloadSha256, payloadSha256, StringComparison.Ordinal)
            && string.Equals(delivery.EventName, eventName, StringComparison.Ordinal)
                ? IngestionOutcome.Duplicate
                : IngestionOutcome.PayloadConflict;

        return new IngestionResult(duplicateOutcome, delivery);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                event_name,
                payload,
                payload_sha256,
                status,
                received_at,
                updated_at,
                attempt_count,
                last_error,
                processing_summary
            FROM deliveries
            ORDER BY received_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var deliveries = new List<WebhookDelivery>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            deliveries.Add(ReadDelivery(reader));
        }

        return deliveries;
    }

    public async Task<DeliveryDetails?> GetDetailsAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        WebhookDelivery? delivery = await GetDeliveryAsync(connection, id, cancellationToken);
        if (delivery is null)
        {
            return null;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                attempt_number,
                status,
                started_at,
                completed_at,
                error,
                summary
            FROM delivery_attempts
            WHERE delivery_id = $deliveryId
            ORDER BY attempt_number;
            """;
        command.Parameters.AddWithValue("$deliveryId", id);

        var attempts = new List<DeliveryAttempt>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(
                new DeliveryAttempt(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    ParseAttemptStatus(reader.GetString(2)),
                    ParseTimestamp(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return new DeliveryDetails(delivery, attempts);
    }

    public async Task<DeliveryWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText =
            """
            UPDATE deliveries
            SET
                status = 'Processing',
                attempt_count = attempt_count + 1,
                updated_at = $updatedAt
            WHERE id = (
                SELECT id
                FROM deliveries
                WHERE status = 'Pending'
                ORDER BY received_at
                LIMIT 1
            )
            RETURNING id, event_name, payload, attempt_count;
            """;
        claim.Parameters.AddWithValue("$updatedAt", Format(now));

        DeliveryWorkItem? workItem = null;
        await using (SqliteDataReader reader = await claim.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                workItem = new DeliveryWorkItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3));
            }
        }

        if (workItem is not null)
        {
            await using SqliteCommand attempt = connection.CreateCommand();
            attempt.Transaction = transaction;
            attempt.CommandText =
                """
                INSERT INTO delivery_attempts (
                    delivery_id,
                    attempt_number,
                    status,
                    started_at)
                VALUES (
                    $deliveryId,
                    $attemptNumber,
                    'Processing',
                    $startedAt);
                """;
            attempt.Parameters.AddWithValue("$deliveryId", workItem.Id);
            attempt.Parameters.AddWithValue("$attemptNumber", workItem.AttemptNumber);
            attempt.Parameters.AddWithValue("$startedAt", Format(now));
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return workItem;
    }

    public Task MarkCompletedAsync(
        DeliveryWorkItem workItem,
        string summary,
        CancellationToken cancellationToken) =>
        CompleteAttemptAsync(workItem, AttemptStatus.Completed, summary, null, cancellationToken);

    public Task MarkFailedAsync(
        DeliveryWorkItem workItem,
        string error,
        CancellationToken cancellationToken) =>
        CompleteAttemptAsync(workItem, AttemptStatus.Failed, null, error, cancellationToken);

    public async Task<RetryOutcome> QueueRetryAsync(
        string id,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE deliveries
            SET
                status = 'Pending',
                updated_at = $updatedAt,
                processing_summary = NULL
            WHERE id = $id
              AND status = 'Failed';
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$updatedAt", Format(now));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return RetryOutcome.Queued;
        }

        return await GetDeliveryAsync(connection, id, cancellationToken) is null
            ? RetryOutcome.NotFound
            : RetryOutcome.NotEligible;
    }

    private async Task CompleteAttemptAsync(
        DeliveryWorkItem workItem,
        AttemptStatus attemptStatus,
        string? summary,
        string? error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand delivery = connection.CreateCommand();
        delivery.Transaction = transaction;
        delivery.CommandText =
            """
            UPDATE deliveries
            SET
                status = $status,
                updated_at = $updatedAt,
                last_error = $error,
                processing_summary = $summary
            WHERE id = $id
              AND status = 'Processing'
              AND attempt_count = $attemptNumber;
            """;
        delivery.Parameters.AddWithValue("$status", attemptStatus.ToString());
        delivery.Parameters.AddWithValue("$updatedAt", Format(now));
        delivery.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        delivery.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        delivery.Parameters.AddWithValue("$id", workItem.Id);
        delivery.Parameters.AddWithValue("$attemptNumber", workItem.AttemptNumber);

        if (await delivery.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Webhook delivery '{workItem.Id}' could not complete attempt {workItem.AttemptNumber} because its persisted processing state changed unexpectedly.");
        }

        await using SqliteCommand attempt = connection.CreateCommand();
        attempt.Transaction = transaction;
        attempt.CommandText =
            """
            UPDATE delivery_attempts
            SET
                status = $status,
                completed_at = $completedAt,
                error = $error,
                summary = $summary
            WHERE delivery_id = $deliveryId
              AND attempt_number = $attemptNumber
              AND status = 'Processing';
            """;
        attempt.Parameters.AddWithValue("$status", attemptStatus.ToString());
        attempt.Parameters.AddWithValue("$completedAt", Format(now));
        attempt.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        attempt.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        attempt.Parameters.AddWithValue("$deliveryId", workItem.Id);
        attempt.Parameters.AddWithValue("$attemptNumber", workItem.AttemptNumber);
        await attempt.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> RecoverInterruptedDeliveriesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        const string recoveryMessage =
            "Processing was interrupted because the HookScope service stopped before the attempt completed.";

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand attempts = connection.CreateCommand();
        attempts.Transaction = transaction;
        attempts.CommandText =
            """
            UPDATE delivery_attempts
            SET
                status = 'Failed',
                completed_at = $completedAt,
                error = $error
            WHERE status = 'Processing';
            """;
        attempts.Parameters.AddWithValue("$completedAt", Format(now));
        attempts.Parameters.AddWithValue("$error", recoveryMessage);
        await attempts.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand deliveries = connection.CreateCommand();
        deliveries.Transaction = transaction;
        deliveries.CommandText =
            """
            UPDATE deliveries
            SET
                status = 'Pending',
                updated_at = $updatedAt,
                last_error = $error
            WHERE status = 'Processing';
            """;
        deliveries.Parameters.AddWithValue("$updatedAt", Format(now));
        deliveries.Parameters.AddWithValue("$error", recoveryMessage);
        int recoveredCount = await deliveries.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return recoveredCount;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private void EnsureDatabaseDirectory()
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);
        string dataSource = connectionStringBuilder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string fullDatabasePath = Path.GetFullPath(dataSource);
        string? databaseDirectory = Path.GetDirectoryName(fullDatabasePath);

        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }
    }

    private static async Task<WebhookDelivery> GetRequiredDeliveryAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken) =>
        await GetDeliveryAsync(connection, id, cancellationToken)
        ?? throw new InvalidOperationException(
            $"Webhook delivery '{id}' was inserted or observed as a duplicate, but its persisted row could not be read.");

    private static async Task<WebhookDelivery?> GetDeliveryAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                event_name,
                payload,
                payload_sha256,
                status,
                received_at,
                updated_at,
                attempt_count,
                last_error,
                processing_summary
            FROM deliveries
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDelivery(reader) : null;
    }

    private static WebhookDelivery ReadDelivery(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseDeliveryStatus(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static DeliveryStatus ParseDeliveryStatus(string status) =>
        Enum.TryParse(status, ignoreCase: false, out DeliveryStatus value)
            ? value
            : throw new InvalidOperationException(
                $"The HookScope database contains the unsupported delivery status '{status}'.");

    private static AttemptStatus ParseAttemptStatus(string status) =>
        Enum.TryParse(status, ignoreCase: false, out AttemptStatus value)
            ? value
            : throw new InvalidOperationException(
                $"The HookScope database contains the unsupported attempt status '{status}'.");

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Recovered {RecoveredDeliveryCount} webhook deliveries that were interrupted while processing. They have been returned to the pending queue.")]
    private static partial void LogRecoveredDeliveries(
        ILogger logger,
        int recoveredDeliveryCount);
}
