#!/usr/bin/env bash
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-export-signalr-poc}"
LOCATION="${LOCATION:-centralindia}"
SUFFIX="$(LC_ALL=C tr -dc 'a-z0-9' </dev/urandom | head -c 8)"

STORAGE_ACCOUNT="expsigstor${SUFFIX}"
SERVICEBUS_NAMESPACE="expsigbus${SUFFIX}"
SIGNALR_NAME="expsigsr${SUFFIX}"

QUEUE_NAME="account-summary-export-requests"
TOPIC_NAME="export-status-events"
SUBSCRIPTION_NAME="signalr-adapter"
BLOB_CONTAINER="exports"
TABLE_NAME="ExportStatuses"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Checking Azure CLI login..."
az account show >/dev/null

echo "Creating resource group ${RESOURCE_GROUP} in ${LOCATION}..."
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" >/dev/null

echo "Creating storage account ${STORAGE_ACCOUNT}..."
az storage account create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${STORAGE_ACCOUNT}" \
  --location "${LOCATION}" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false >/dev/null

STORAGE_CONNECTION="$(az storage account show-connection-string \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${STORAGE_ACCOUNT}" \
  --query connectionString \
  --output tsv)"

echo "Creating blob container and table..."
az storage container create --name "${BLOB_CONTAINER}" --connection-string "${STORAGE_CONNECTION}" >/dev/null
az storage table create --name "${TABLE_NAME}" --connection-string "${STORAGE_CONNECTION}" >/dev/null

echo "Creating Service Bus namespace ${SERVICEBUS_NAMESPACE}..."
az servicebus namespace create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${SERVICEBUS_NAMESPACE}" \
  --location "${LOCATION}" \
  --sku Standard >/dev/null

az servicebus queue create \
  --resource-group "${RESOURCE_GROUP}" \
  --namespace-name "${SERVICEBUS_NAMESPACE}" \
  --name "${QUEUE_NAME}" >/dev/null

az servicebus topic create \
  --resource-group "${RESOURCE_GROUP}" \
  --namespace-name "${SERVICEBUS_NAMESPACE}" \
  --name "${TOPIC_NAME}" >/dev/null

az servicebus topic subscription create \
  --resource-group "${RESOURCE_GROUP}" \
  --namespace-name "${SERVICEBUS_NAMESPACE}" \
  --topic-name "${TOPIC_NAME}" \
  --name "${SUBSCRIPTION_NAME}" >/dev/null

SERVICEBUS_CONNECTION="$(az servicebus namespace authorization-rule keys list \
  --resource-group "${RESOURCE_GROUP}" \
  --namespace-name "${SERVICEBUS_NAMESPACE}" \
  --name RootManageSharedAccessKey \
  --query primaryConnectionString \
  --output tsv)"

echo "Creating Azure SignalR Service ${SIGNALR_NAME}..."
az signalr create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${SIGNALR_NAME}" \
  --location "${LOCATION}" \
  --sku Free_F1 \
  --service-mode Default >/dev/null

SIGNALR_CONNECTION="$(az signalr key list \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${SIGNALR_NAME}" \
  --query primaryConnectionString \
  --output tsv)"

write_api_settings() {
  cat >"${ROOT_DIR}/backend/src/Export.Api/appsettings.Development.json" <<EOF
{
  "ConnectionStrings": {
    "Storage": "${STORAGE_CONNECTION}",
    "ServiceBus": "${SERVICEBUS_CONNECTION}"
  },
  "ExportStorage": {
    "TableName": "${TABLE_NAME}"
  },
  "ServiceBus": {
    "ExportRequestQueueName": "${QUEUE_NAME}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
EOF
}

write_processor_settings() {
  cat >"${ROOT_DIR}/backend/src/Export.Processor/appsettings.Development.json" <<EOF
{
  "ConnectionStrings": {
    "Storage": "${STORAGE_CONNECTION}",
    "ServiceBus": "${SERVICEBUS_CONNECTION}"
  },
  "ExportStorage": {
    "BlobContainerName": "${BLOB_CONTAINER}",
    "TableName": "${TABLE_NAME}"
  },
  "ServiceBus": {
    "ExportRequestQueueName": "${QUEUE_NAME}",
    "ExportStatusTopicName": "${TOPIC_NAME}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
EOF
}

write_signalr_settings() {
  cat >"${ROOT_DIR}/backend/src/Export.SignalRAdapter/appsettings.Development.json" <<EOF
{
  "ConnectionStrings": {
    "ServiceBus": "${SERVICEBUS_CONNECTION}",
    "AzureSignalR": "${SIGNALR_CONNECTION}"
  },
  "ServiceBus": {
    "ExportStatusTopicName": "${TOPIC_NAME}",
    "SignalRAdapterSubscriptionName": "${SUBSCRIPTION_NAME}"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
EOF
}

write_api_settings
write_processor_settings
write_signalr_settings

cat >"${ROOT_DIR}/frontend/account-export-ui/.env" <<EOF
VITE_EXPORT_API_BASE_URL=http://localhost:5001
VITE_SIGNALR_HUB_URL=http://localhost:5003/hubs/exports
EOF

echo "Created resources:"
echo "  Resource group: ${RESOURCE_GROUP}"
echo "  Storage account: ${STORAGE_ACCOUNT}"
echo "  Service Bus namespace: ${SERVICEBUS_NAMESPACE}"
echo "  Azure SignalR Service: ${SIGNALR_NAME}"
echo "Local appsettings.Development.json files and frontend .env were generated."
