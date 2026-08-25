$localSettingsPath = Join-Path $PSScriptRoot "..\code\RotaryEmailForwarding.FunctionApp\local.settings.json"
$adminApiKey = (Get-Content -Raw $localSettingsPath | ConvertFrom-Json).Values.adminApiKey
if ([string]::IsNullOrWhiteSpace($adminApiKey)) { throw "adminApiKey is missing from local.settings.json." }

curl.exe --fail-with-body --silent --show-error --request POST "http://localhost:7071/api/catch-up-interest-form-entries" --header "Content-Type: application/json" --header "x-admin-api-key: $adminApiKey" --data-binary "@C:\Users\Drake L\OneDrive\Documents\Projects\emailForwarding\RotaryEmailForwarding\.catchup-2026-08-24\batch-001.json"
curl.exe --fail-with-body --silent --show-error --request POST "http://localhost:7071/api/catch-up-interest-form-entries" --header "Content-Type: application/json" --header "x-admin-api-key: $adminApiKey" --data-binary "@C:\Users\Drake L\OneDrive\Documents\Projects\emailForwarding\RotaryEmailForwarding\.catchup-2026-08-24\batch-002.json"
