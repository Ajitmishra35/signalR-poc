using System.Text;
using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Export.Contracts;

namespace Export.Processor;

public sealed class AccountSummaryExportWorker : BackgroundService
{
    private readonly BlobContainerClient _blobContainer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountSummaryExportWorker> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly TableClient _tableClient;
    private ServiceBusProcessor? _processor;
    private ServiceBusSender? _statusEventSender;

    public AccountSummaryExportWorker(
        ServiceBusClient serviceBusClient,
        TableClient tableClient,
        BlobContainerClient blobContainer,
        IConfiguration configuration,
        ILogger<AccountSummaryExportWorker> logger)
    {
        _serviceBusClient = serviceBusClient;
        _tableClient = tableClient;
        _blobContainer = blobContainer;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueName = _configuration["ServiceBus:ExportRequestQueueName"] ?? "account-summary-export-requests";
        var topicName = _configuration["ServiceBus:ExportStatusTopicName"] ?? "export-status-events";

        _statusEventSender = _serviceBusClient.CreateSender(topicName);
        _processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 2
        });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Service Bus processor error from {ErrorSource}.", args.ErrorSource);
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Export Processor listening to Service Bus queue {QueueName}.", queueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown.
        }
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message.Body.ToObjectFromJson<ExportRequestMessage>()
            ?? throw new InvalidOperationException("Service Bus request message body was empty or invalid.");

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["TenantId"] = message.TenantId,
            ["ExportId"] = message.ExportId,
            ["AccountId"] = message.AccountId
        });

        _logger.LogInformation("Consumed export request message for account summary export.");

        try
        {
            await UpdateStatusAsync(message, ExportStatuses.Processing, null, null, args.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), args.CancellationToken);

            if (string.Equals(message.AccountId, "FAIL001", StringComparison.OrdinalIgnoreCase))
            {
                const string error = "Simulated export failure for accountId FAIL001.";
                await UpdateStatusAsync(message, ExportStatuses.Failed, null, error, args.CancellationToken);
                await PublishStatusChangedAsync(message, ExportStatuses.Failed, null, error, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                _logger.LogWarning("Completed failed demo path for export {ExportId}.", message.ExportId);
                return;
            }

            var downloadUrl = await UploadCsvAndCreateSasAsync(message, args.CancellationToken);

            await UpdateStatusAsync(message, ExportStatuses.Completed, downloadUrl, null, args.CancellationToken);

            // The processor publishes a backend event after changing durable status.
            // The SignalR adapter consumes this event and handles client notification.
            await PublishStatusChangedAsync(message, ExportStatuses.Completed, downloadUrl, null, args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            _logger.LogInformation("Export {ExportId} completed; message completed after file upload and event publish.", message.ExportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export processing failed unexpectedly.");

            try
            {
                await UpdateStatusAsync(message, ExportStatuses.Failed, null, ex.Message, args.CancellationToken);
                await PublishStatusChangedAsync(message, ExportStatuses.Failed, null, ex.Message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Could not publish failure status. Abandoning Service Bus message for retry.");
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
            }
        }
    }

    private async Task<string> UploadCsvAndCreateSasAsync(ExportRequestMessage message, CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var csv = new StringBuilder()
            .AppendLine("Account Id,Account Name,Balance,Currency,Generated At")
            .AppendLine($"{message.AccountId},Demo Account {message.AccountId},125000.50,INR,{generatedAt:O}")
            .ToString();

        var blobPath = $"{message.TenantId}/{message.ExportId}/account-summary-{message.AccountId}.csv";
        var blob = _blobContainer.GetBlobClient(blobPath);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "text/csv" }
            },
            cancellationToken);

        if (!blob.CanGenerateSasUri)
        {
            throw new InvalidOperationException("Blob client cannot generate SAS. Use a storage account connection string with account key.");
        }

        var sas = new BlobSasBuilder
        {
            BlobContainerName = _blobContainer.Name,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        var url = blob.GenerateSasUri(sas).ToString();
        _logger.LogInformation("Uploaded CSV to blob path {BlobPath}; generated 30 minute read-only SAS URL.", blobPath);
        return url;
    }

    private async Task UpdateStatusAsync(
        ExportRequestMessage message,
        string status,
        string? downloadUrl,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var response = await _tableClient.GetEntityAsync<ExportStatusEntity>(
            message.TenantId,
            message.ExportId,
            cancellationToken: cancellationToken);

        var entity = response.Value;
        entity.Status = status;
        entity.DownloadUrl = downloadUrl;
        entity.ErrorMessage = errorMessage;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        _logger.LogInformation("Updated Azure Table status to {Status}.", status);
    }

    private async Task PublishStatusChangedAsync(
        ExportRequestMessage message,
        string status,
        string? downloadUrl,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (_statusEventSender is null)
        {
            throw new InvalidOperationException("Status event sender is not initialized.");
        }

        var evt = new ExportStatusChangedEvent(
            message.ExportId,
            message.TenantId,
            message.AccountId,
            status,
            downloadUrl,
            errorMessage,
            message.CorrelationId,
            DateTimeOffset.UtcNow);

        var serviceBusMessage = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt))
        {
            MessageId = $"{message.ExportId}-{status}-{evt.UpdatedAtUtc.ToUnixTimeMilliseconds()}",
            CorrelationId = message.CorrelationId,
            Subject = "ExportStatusChanged"
        };
        serviceBusMessage.ApplicationProperties["tenantId"] = message.TenantId;
        serviceBusMessage.ApplicationProperties["exportId"] = message.ExportId;
        serviceBusMessage.ApplicationProperties["status"] = status;

        await _statusEventSender.SendMessageAsync(serviceBusMessage, cancellationToken);
        _logger.LogInformation("Published ExportStatusChanged event with status {Status}.", status);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        if (_statusEventSender is not null)
        {
            await _statusEventSender.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
