param(
    [string]$ResourceGroup = "rg-export-signalr-poc",
    [string]$Location = "centralindia"
)

$ErrorActionPreference = "Stop"

$suffix = -join ((48..57) + (97..122) | Get-Random -Count 8 | ForEach-Object {[char]$_})
$storageAccount = "expsigstor$suffix"
$serviceBusNamespace = "expsigbus$suffix"
$signalRName = "expsigsr$suffix"

$queueName = "account-summary-export-requests"
$topicName = "export-status-events"
$subscriptionName = "signalr-adapter"
$blobContainer = "exports"
$tableName = "ExportStatuses"
$rootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "Checking Azure CLI login..."
az account show | Out-Null

Write-Host "Creating resource group $ResourceGroup in $Location..."
az group create --name $ResourceGroup --location $Location | Out-Null

Write-Host "Creating storage account $storageAccount..."
az storage account create `
  --resource-group $ResourceGroup `
  --name $storageAccount `
  --location $Location `
  --sku Standard_LRS `
  --kind StorageV2 `
  --allow-blob-public-access false | Out-Null

$storageConnection = az storage account show-connection-string `
  --resource-group $ResourceGroup `
  --name $storageAccount `
  --query connectionString `
  --output tsv

Write-Host "Creating blob container and table..."
az storage container create --name $blobContainer --connection-string $storageConnection | Out-Null
az storage table create --name $tableName --connection-string $storageConnection | Out-Null

Write-Host "Creating Service Bus namespace $serviceBusNamespace..."
az servicebus namespace create `
  --resource-group $ResourceGroup `
  --name $serviceBusNamespace `
  --location $Location `
  --sku Standard | Out-Null

az servicebus queue create `
  --resource-group $ResourceGroup `
  --namespace-name $serviceBusNamespace `
  --name $queueName | Out-Null

az servicebus topic create `
  --resource-group $ResourceGroup `
  --namespace-name $serviceBusNamespace `
  --name $topicName | Out-Null

az servicebus topic subscription create `
  --resource-group $ResourceGroup `
  --namespace-name $serviceBusNamespace `
  --topic-name $topicName `
  --name $subscriptionName | Out-Null

$serviceBusConnection = az servicebus namespace authorization-rule keys list `
  --resource-group $ResourceGroup `
  --namespace-name $serviceBusNamespace `
  --name RootManageSharedAccessKey `
  --query primaryConnectionString `
  --output tsv

Write-Host "Creating Azure SignalR Service $signalRName..."
az signalr create `
  --resource-group $ResourceGroup `
  --name $signalRName `
  --location $Location `
  --sku Free_F1 `
  --service-mode Default | Out-Null

$signalRConnection = az signalr key list `
  --resource-group $ResourceGroup `
  --name $signalRName `
  --query primaryConnectionString `
  --output tsv

$apiSettings = @{
  ConnectionStrings = @{ Storage = $storageConnection; ServiceBus = $serviceBusConnection }
  ExportStorage = @{ TableName = $tableName }
  ServiceBus = @{ ExportRequestQueueName = $queueName }
  Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning" } }
} | ConvertTo-Json -Depth 10

$processorSettings = @{
  ConnectionStrings = @{ Storage = $storageConnection; ServiceBus = $serviceBusConnection }
  ExportStorage = @{ BlobContainerName = $blobContainer; TableName = $tableName }
  ServiceBus = @{ ExportRequestQueueName = $queueName; ExportStatusTopicName = $topicName }
  Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.Hosting.Lifetime" = "Information" } }
} | ConvertTo-Json -Depth 10

$adapterSettings = @{
  ConnectionStrings = @{ ServiceBus = $serviceBusConnection; AzureSignalR = $signalRConnection }
  ServiceBus = @{ ExportStatusTopicName = $topicName; SignalRAdapterSubscriptionName = $subscriptionName }
  Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning" } }
} | ConvertTo-Json -Depth 10

Set-Content -Path (Join-Path $rootDir "backend/src/Export.Api/appsettings.Development.json") -Value $apiSettings
Set-Content -Path (Join-Path $rootDir "backend/src/Export.Processor/appsettings.Development.json") -Value $processorSettings
Set-Content -Path (Join-Path $rootDir "backend/src/Export.SignalRAdapter/appsettings.Development.json") -Value $adapterSettings
Set-Content -Path (Join-Path $rootDir "frontend/account-export-ui/.env") -Value @"
VITE_EXPORT_API_BASE_URL=http://localhost:5001
VITE_SIGNALR_HUB_URL=http://localhost:5003/hubs/exports
"@

Write-Host "Created resources:"
Write-Host "  Resource group: $ResourceGroup"
Write-Host "  Storage account: $storageAccount"
Write-Host "  Service Bus namespace: $serviceBusNamespace"
Write-Host "  Azure SignalR Service: $signalRName"
Write-Host "Local appsettings.Development.json files and frontend .env were generated."
