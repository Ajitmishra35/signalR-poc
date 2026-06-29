using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Export.Processor;

var builder = Host.CreateApplicationBuilder(args);

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
    var storage = builder.Configuration.GetConnectionString("Storage")
        ?? throw new InvalidOperationException("ConnectionStrings:Storage is required.");
    var containerName = builder.Configuration["ExportStorage:BlobContainerName"] ?? "exports";
    var container = new BlobContainerClient(storage, containerName);
    container.CreateIfNotExists();
    return container;
});

builder.Services.AddSingleton(_ =>
{
    var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
        ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is required.");
    return new ServiceBusClient(serviceBus);
});

builder.Services.AddHostedService<AccountSummaryExportWorker>();

var host = builder.Build();
host.Run();
