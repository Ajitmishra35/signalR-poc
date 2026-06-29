using Azure;
using Azure.Data.Tables;

namespace Export.Contracts;

public static class ExportStatuses
{
    public const string Requested = "Requested";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed record ExportRequestMessage(
    string ExportId,
    string TenantId,
    string AccountId,
    string CorrelationId,
    string IdempotencyKey,
    DateTimeOffset RequestedAtUtc);

public sealed record ExportStatusChangedEvent(
    string ExportId,
    string TenantId,
    string AccountId,
    string Status,
    string? DownloadUrl,
    string? ErrorMessage,
    string CorrelationId,
    DateTimeOffset UpdatedAtUtc);

public sealed class ExportStatusEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TenantId { get; set; } = default!;
    public string AccountId { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string? DownloadUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public string CorrelationId { get; set; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static ExportStatusEntity Requested(
        string exportId,
        string tenantId,
        string accountId,
        string correlationId,
        string idempotencyKey,
        DateTimeOffset now) =>
        new()
        {
            PartitionKey = tenantId,
            RowKey = exportId,
            TenantId = tenantId,
            AccountId = accountId,
            Status = ExportStatuses.Requested,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            RequestedAtUtc = now,
            UpdatedAtUtc = now
        };
}
