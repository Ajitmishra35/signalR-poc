using Azure.Messaging.ServiceBus;
using Export.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Export.SignalRAdapter;

public sealed class ExportStatusEventSubscriber : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHubContext<ExportHub> _hubContext;
    private readonly ILogger<ExportStatusEventSubscriber> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private ServiceBusProcessor? _processor;

    public ExportStatusEventSubscriber(
        ServiceBusClient serviceBusClient,
        IHubContext<ExportHub> hubContext,
        IConfiguration configuration,
        ILogger<ExportStatusEventSubscriber> logger)
    {
        _serviceBusClient = serviceBusClient;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topicName = _configuration["ServiceBus:ExportStatusTopicName"] ?? "export-status-events";
        var subscriptionName = _configuration["ServiceBus:SignalRAdapterSubscriptionName"] ?? "signalr-adapter";

        _processor = _serviceBusClient.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4
        });

        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "SignalR adapter Service Bus subscriber error from {ErrorSource}.", args.ErrorSource);
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("SignalR Adapter listening to Service Bus topic {TopicName}, subscription {SubscriptionName}.", topicName, subscriptionName);

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
        var evt = args.Message.Body.ToObjectFromJson<ExportStatusChangedEvent>()
            ?? throw new InvalidOperationException("ExportStatusChanged event body was empty or invalid.");

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = evt.CorrelationId,
            ["TenantId"] = evt.TenantId,
            ["ExportId"] = evt.ExportId,
            ["Status"] = evt.Status
        });

        _logger.LogInformation("Consumed ExportStatusChanged event; pushing to SignalR export-specific group.");

        // The adapter exists to bridge backend events to live clients.
        // Download URLs are sent only to export-{exportId}, not tenant-wide.
        await _hubContext.Clients
            .Group($"export-{evt.ExportId}")
            .SendAsync("ExportStatusChanged", evt, args.CancellationToken);

        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        _logger.LogInformation("Sent ExportStatusChanged to group export-{ExportId}.", evt.ExportId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
