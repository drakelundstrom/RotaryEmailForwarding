# Rotary Email Forwarding

Backend Azure Functions app for the Study Abroad Scholarships interest form.

## Local Development

1. Copy `code/RotaryEmailForwarding.FunctionApp/local.settings.sample.json` to `local.settings.json`.
2. Keep local secret values out of source control.
3. For unit tests, no Cosmos DB or SMTP provider is required.

Useful commands:

```powershell
dotnet restore code\EmailForwardingApplication.sln
dotnet build code\EmailForwardingApplication.sln
dotnet test code\EmailForwardingApplication.sln
az bicep build --file infra/main.bicep
az bicep build-params --file infra/params/parameters.test.bicepparam
az bicep build-params --file infra/params/parameters.prod.bicepparam
```

## Configuration

Runtime settings are resolved from environment variables or Azure App Settings:

- `sendingEmailAddress`
- `sendingEmailPassword`
- `databaseConnectionString`
- `cosmosDatabaseName`
- `cosmosContainerName`
- `mailHost`
- `mailPort`
- `mailSecurityMode`
- `emailRetryTimeZone`
- `appEnvironment`
- `adminApiKey`
- `nonProductionSafeRecipient`
- `allowUnsafeNonProductionEmail`
- `maxRequestBodyBytes`

Azure environments use Key Vault references for `sendingEmailAddress`, `sendingEmailPassword`, `databaseConnectionString`, and `adminApiKey`.

## API Surface

- `POST /api/interest-form-entry`: public form submission endpoint protected by the Function key path.
- `POST /api/contacts-for-districts`: admin contact upload, requires `x-admin-api-key`.
- `POST /api/contacts-for-countries`: admin contact upload, requires `x-admin-api-key`.
- `GET /api/contacts-for-districts/{districtName}`: admin district lookup, requires `x-admin-api-key`.
- `GET /api/generate-submissions-by-month?start=...&end=...`: admin monthly reporting, requires `x-admin-api-key`.
- `GET /api/submissions/district/{districtName}`: admin district submission report, requires `x-admin-api-key`.
- `GET /api/health`: anonymous health probe.

## Delivery and Retry

Submissions are persisted before outbound email starts. `SentOnUtc` is set only after every required outbound email message for that submission succeeds. Retryable SMTP/provider failures and quota failures leave `SentOnUtc=null`, set `EmailDeliveryStatus=RetryPending`, and keep the submission eligible for the scheduled retry function.

Flex Consumption does not support `WEBSITE_TIME_ZONE`, so the retry trigger wakes hourly and only runs when the configured `emailRetryTimeZone` resolves to local hour `03:00`.

## Security and Retention

Production and test use isolated Function Apps, Storage accounts, Key Vaults, and app settings. Both environments are configured to use the shared Cosmos account `studyabroadscholarshipsdb` in resource group `EmailForwardingApi`, with the `EmailForwarding` database and `ContactInfoAndRequests` container. Non-production email is routed to `DrakeLundstrom95@gmail.com` unless unsafe delivery is explicitly enabled.

Rotate Key Vault secrets per environment by creating a new secret version and restarting the Function App after validation. PII-bearing submissions and raw request logs should be retained only as long as the operating program requires; align Cosmos retention or scheduled purge jobs with the organization retention policy before production launch.
