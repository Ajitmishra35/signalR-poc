param(
    [string]$ResourceGroup = "rg-export-signalr-poc"
)

$confirm = Read-Host "This will delete resource group $ResourceGroup and all POC resources in it. Continue? [y/N]"

if ($confirm -ne "y" -and $confirm -ne "Y") {
    Write-Host "Cleanup cancelled."
    exit 0
}

az group delete --name $ResourceGroup --yes --no-wait
Write-Host "Delete submitted for $ResourceGroup."
