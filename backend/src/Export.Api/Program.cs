using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Export.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("react", policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddSingleton(_ =>
{
    var storage = builder.Configuration.GetConnectionString("Storage")
        ?? throw new InvalidOperationException("ConnectionStrings:Storage is required.");
    var tableName = builder.Configuration["ExportStorage:TableName"] ?? "ExportStatuses";
    var tableClient = new TableClient(storage, tableName);
    tableClient.CreateIfNotExists();
    return tableClient;
});

builder.Services.AddSingleton(_ =>
{
    var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
        ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is required.");
    return new ServiceBusClient(serviceBus);
});

builder.Services.AddSingleton(sp =>
{
    var queueName = builder.Configuration["ServiceBus:ExportRequestQueueName"]
        ?? "account-summary-export-requests";
    return sp.GetRequiredService<ServiceBusClient>().CreateSender(queueName);
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("react");

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "Export.Api" }));

app.MapPost("/api/exports/account-summary", async (
    StartAccountSummaryExportRequest request,
    HttpContext http,
    TableClient table,
    ServiceBusSender sender,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("Export.Api");
    var correlationId = GetHeaderOrNew(http, "x-correlation-id");
    var idempotencyKey = GetHeaderOrNew(http, "Idempotency-Key");

    using var scope = logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["TenantId"] = request.TenantId ?? string.Empty,
        ["AccountId"] = request.AccountId ?? string.Empty
    });

    if (string.IsNullOrWhiteSpace(request.TenantId))
    {
        return Results.BadRequest(new { error = "tenantId is required." });
    }

    if (string.IsNullOrWhiteSpace(request.AccountId))
    {
        return Results.BadRequest(new { error = "accountId is required." });
    }

    var tenantId = request.TenantId.Trim();
    var accountId = request.AccountId.Trim();

    var existing = await FindByIdempotencyKeyAsync(table, tenantId, idempotencyKey, cancellationToken);
    if (existing is not null)
    {
        logger.LogInformation(
            "Idempotent export request found. Returning existing exportId {ExportId} without sending another Service Bus message.",
            existing.RowKey);

        return Results.Accepted($"/api/exports/{existing.RowKey}", ToAcceptedResponse(existing));
    }

    var now = DateTimeOffset.UtcNow;
    var exportId = Guid.NewGuid().ToString("N");
    var entity = ExportStatusEntity.Requested(exportId, tenantId, accountId, correlationId, idempotencyKey, now);

    await table.AddEntityAsync(entity, cancellationToken);

    var message = new ExportRequestMessage(exportId, tenantId, accountId, correlationId, idempotencyKey, now);
    var serviceBusMessage = new ServiceBusMessage(BinaryData.FromObjectAsJson(message))
    {
        MessageId = $"{tenantId}-{idempotencyKey}",
        CorrelationId = correlationId,
        Subject = "AccountSummaryExportRequested"
    };
    serviceBusMessage.ApplicationProperties["tenantId"] = tenantId;
    serviceBusMessage.ApplicationProperties["exportId"] = exportId;

    // The API returns HTTP 202 after durable status storage and queueing.
    // File generation is deliberately moved out of the request path.
    await sender.SendMessageAsync(serviceBusMessage, cancellationToken);

    logger.LogInformation(
        "Accepted account summary export {ExportId}; sent request message to Service Bus queue.",
        exportId);

    return Results.Accepted($"/api/exports/{exportId}", ToAcceptedResponse(entity));
});

app.MapGet("/api/exports/{exportId}", async (
    string exportId,
    string tenantId,
    TableClient table,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(tenantId))
    {
        return Results.BadRequest(new { error = "tenantId query string is required." });
    }

    try
    {
        var response = await table.GetEntityAsync<ExportStatusEntity>(tenantId.Trim(), exportId, cancellationToken: cancellationToken);
        return Results.Ok(ToStatusResponse(response.Value));
    }
    catch (Azure.RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
    {
        return Results.NotFound(new { error = "Export status was not found." });
    }
});

app.Run();

static string GetHeaderOrNew(HttpContext http, string name)
{
    var value = http.Request.Headers[name].FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
}

static async Task<ExportStatusEntity?> FindByIdempotencyKeyAsync(
    TableClient table,
    string tenantId,
    string idempotencyKey,
    CancellationToken cancellationToken)
{
    var filter = TableClient.CreateQueryFilter<ExportStatusEntity>(
        e => e.PartitionKey == tenantId && e.IdempotencyKey == idempotencyKey);

    await foreach (var entity in table.QueryAsync<ExportStatusEntity>(filter, maxPerPage: 1, cancellationToken: cancellationToken))
    {
        return entity;
    }

    return null;
}

static object ToAcceptedResponse(ExportStatusEntity entity) => new
{
    exportId = entity.RowKey,
    tenantId = entity.TenantId,
    accountId = entity.AccountId,
    status = entity.Status,
    correlationId = entity.CorrelationId,
    statusUrl = $"/api/exports/{entity.RowKey}"
};

static object ToStatusResponse(ExportStatusEntity entity) => new
{
    exportId = entity.RowKey,
    tenantId = entity.TenantId,
    accountId = entity.AccountId,
    status = entity.Status,
    downloadUrl = entity.DownloadUrl,
    errorMessage = entity.ErrorMessage,
    correlationId = entity.CorrelationId
};

public sealed record StartAccountSummaryExportRequest(string? TenantId, string? AccountId);
