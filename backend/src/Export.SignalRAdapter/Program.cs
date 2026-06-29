using Azure.Messaging.ServiceBus;
using Export.SignalRAdapter;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("react", policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

var azureSignalR = builder.Configuration.GetConnectionString("AzureSignalR");
if (!string.IsNullOrWhiteSpace(azureSignalR))
{
    builder.Services.AddSignalR().AddAzureSignalR(azureSignalR);
}
else
{
    builder.Services.AddSignalR();
}

builder.Services.AddSingleton(_ =>
{
    var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
        ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is required.");
    return new ServiceBusClient(serviceBus);
});
builder.Services.AddHostedService<ExportStatusEventSubscriber>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("react");

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "Export.SignalRAdapter" }));
app.MapHub<ExportHub>("/hubs/exports");

app.Run();
