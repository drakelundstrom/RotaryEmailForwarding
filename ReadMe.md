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
- `operatorEmail`
- `supportEmail`
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

Azure environments use Key Vault references for `sendingEmailAddress`, `operatorEmail`, `supportEmail`, `sendingEmailPassword`, `databaseConnectionString`, `adminApiKey`, and the test-only `nonProductionSafeRecipient`.

Create or update these Key Vault secrets after the environment Key Vault exists and before relying on email delivery:

```powershell
az keyvault secret set --vault-name <vault-name> --name sendingEmailAddress --value <smtp-sender-address>
az keyvault secret set --vault-name <vault-name> --name operatorEmail --value <maintenance-recipient-address>
az keyvault secret set --vault-name <vault-name> --name supportEmail --value <public-support-address>
az keyvault secret set --vault-name <vault-name> --name sendingEmailPassword --value <smtp-password>
az keyvault secret set --vault-name <vault-name> --name adminApiKey --value <admin-api-key>
az keyvault secret set --vault-name <test-vault-name> --name nonProductionSafeRecipient --value <test-sink-address>
```

## API Surface

- `POST /api/interest-form-entry`: public form submission endpoint protected by the Function key path.
- `POST /api/contacts-for-districts`: admin contact upload, requires `x-admin-api-key`.
- `POST /api/contacts-for-countries`: admin contact upload, requires `x-admin-api-key`.
- `GET /api/contacts-for-districts/{districtName}`: admin district lookup, requires `x-admin-api-key`.
- `GET /api/generate-submissions-by-month?start=...&end=...`: admin monthly reporting, requires `x-admin-api-key`.
- `GET /api/submissions/district/{districtName}`: admin district submission report, requires `x-admin-api-key`.
- `GET /api/interest-forms-per-district-per-quarter`: admin markdown report for the current quarter plus the prior 2 years by Cosmos `_ts`, grouped by country and district, requires `x-admin-api-key`.
- `GET /api/health`: anonymous health probe.

## Delivery and Retry

Submissions are persisted before outbound email starts. `SentOnUtc` is set only after every required outbound email message for that submission succeeds. Retryable SMTP/provider failures and quota failures leave `SentOnUtc=null`, set `EmailDeliveryStatus=RetryPending`, and keep the submission eligible for the scheduled retry function.

Flex Consumption does not support `WEBSITE_TIME_ZONE`, so the retry trigger wakes hourly and only runs when the configured `emailRetryTimeZone` resolves to local hour `03:00`.

## Email Examples

The examples below show the approximate plain-text email body generated for common routing paths. Blank or null form fields are omitted from the details block. If both `StudentEmail` and `ParentEmail` are provided, both are included as recipients.

### Student routed to one district

```text
To: district6630@example.org, jordan@example.com, parent@example.com
Subject: Rotary Youth Exchange interest from Jordan Example

Hello,

We are reaching out to connect you with our local coordinators in Rotary District 6630.

Here are the details from the interest form:
Who are you?: Student
Name: Jordan Example
Current age (years): 16
Student's email: jordan@example.com
Student's phone number: 555-0100
Parent's email: parent@example.com
Parent's phone number: 555-0101
Country of residence: USA
State or province: Ohio
City: Cleveland
Zip code or first 3 of CDN postal code: 44102
Specific questions: Can I choose a country?
```

### Parent submission on a district border

```text
To: district6630@example.org, district6650@example.org, student@example.com, parent@example.com, contact@example.com
Subject: Rotary Youth Exchange interest from Pat Parent

Hello,

You are on the border of 2 Rotary districts, so we have included both to make sure the right person gets in contact.
Districts included: 6630, 6650.

Here are the details from the interest form:
Who are you?: Parent
Name: Pat Parent
Current age of your student (years): 15
Student's email: student@example.com
Parent's email: parent@example.com
Contact email: contact@example.com
Country of residence: USA
State or province: Pennsylvania
City: Erie
Zip code or first 3 of CDN postal code: 16501
```

### Mexico country routing

```text
To: mexico-coordinator@example.org, contact@example.com
Subject: Rotary Youth Exchange interest from Alex Example

Hello,

We are reaching out to connect you with our local coordinators for Mexico.

Here are the details from the interest form:
Who are you?: Parent
Name: Alex Example
Current age of your student (years): 16
Contact email: contact@example.com
Contact phone number: 555-0102
Country of residence: Mexico
City: Monterrey
Specific questions: Is there an application deadline?
```

### Manual routing review

```text
To: operator@example.test, contact@example.com, support@example.test
Subject: Rotary Youth Exchange interest needs routing review

Hello,

Our automated system was not able to resolve where you should be forwarded, but an admin will take a look and should have this resolved in a week or less. In the meantime, feel free to reach out to your local Rotary club!

Here are the details from the interest form:
Who are you?: Other
Name: Taylor Example
Contact email: contact@example.com
Country of residence: USA
City: Unknown City

Routing notes: Zipcode missing for district routing
```

For `Rotarian` and `Other` submissions, `supportEmail` is copied on the outgoing message. For student and parent submissions, `supportEmail` is not copied.

## Security and Retention

Production and test use isolated Function Apps, Storage accounts, Key Vaults, and app settings. Both environments are configured to use the shared Cosmos account `studyabroadscholarshipsdb` in resource group `EmailForwardingApi`, with the `EmailForwarding` database and `ContactInfoAndRequests` container. Non-production email is routed to the `nonProductionSafeRecipient` Key Vault secret unless unsafe delivery is explicitly enabled.

Rotate Key Vault secrets per environment by creating a new secret version and restarting the Function App after validation. PII-bearing submissions and raw request logs should be retained only as long as the operating program requires; align Cosmos retention or scheduled purge jobs with the organization retention policy before production launch.
