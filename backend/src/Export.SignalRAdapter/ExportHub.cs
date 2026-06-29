using Microsoft.AspNetCore.SignalR;

namespace Export.SignalRAdapter;

public sealed class ExportHub : Hub
{
    private readonly ILogger<ExportHub> _logger;

    public ExportHub(ILogger<ExportHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinExportGroup(string exportId)
    {
        if (string.IsNullOrWhiteSpace(exportId))
        {
            throw new HubException("exportId is required.");
        }

        var groupName = $"export-{exportId.Trim()}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("SignalR connection {ConnectionId} joined export group {GroupName}.", Context.ConnectionId, groupName);
    }

    public async Task LeaveExportGroup(string exportId)
    {
        if (string.IsNullOrWhiteSpace(exportId))
        {
            return;
        }

        var groupName = $"export-{exportId.Trim()}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("SignalR connection {ConnectionId} left export group {GroupName}.", Context.ConnectionId, groupName);
    }

    public async Task JoinTenantGroup(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new HubException("tenantId is required.");
        }

        var groupName = $"tenant-{tenantId.Trim()}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("SignalR connection {ConnectionId} joined tenant group {GroupName}.", Context.ConnectionId, groupName);
    }
}
