#!/usr/bin/env bash
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-export-signalr-poc}"

echo "This will delete resource group ${RESOURCE_GROUP} and all POC resources in it."
read -r -p "Continue? [y/N] " confirm

if [[ "${confirm}" != "y" && "${confirm}" != "Y" ]]; then
  echo "Cleanup cancelled."
  exit 0
fi

az group delete --name "${RESOURCE_GROUP}" --yes --no-wait
echo "Delete submitted for ${RESOURCE_GROUP}."
