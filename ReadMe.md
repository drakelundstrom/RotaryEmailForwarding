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

The infrastructure deployment treats the Cosmos account, database, and containers as existing shared resources. Production uses `ContactInfoAndRequests`, while test uses `TestContactInfoAndRequestsByType`. Both containers must use `/Type` as the partition key. The deployment generates the `databaseConnectionString` Key Vault secret directly from the configured Cosmos account; do not create or maintain that secret manually.

Create or update these Key Vault secrets after the environment Key Vault exists and before relying on email delivery:

```powershell
az keyvault secret set --vault-name <vault-name> --name sendingEmailAddress --value <gmail-or-google-workspace-sender-address>
az keyvault secret set --vault-name <vault-name> --name operatorEmail --value <maintenance-recipient-address>
az keyvault secret set --vault-name <vault-name> --name supportEmail --value <public-support-address>
az keyvault secret set --vault-name <vault-name> --name sendingEmailPassword --value <gmail-app-password>
az keyvault secret set --vault-name <vault-name> --name adminApiKey --value <admin-api-key>
az keyvault secret set --vault-name <test-vault-name> --name nonProductionSafeRecipient --value <test-sink-address>
```

Azure deployments default to Gmail SMTP: `smtp.gmail.com`, port `587`, `StartTls`.

## API Surface

- `POST /api/interest-form-entry`: public form submission endpoint protected by the Function key path.
- `POST /api/contacts-for-districts`: admin contact upload, requires `x-admin-api-key`.
- `POST /api/contacts-for-countries`: admin contact upload, requires `x-admin-api-key`.
- `GET /api/contacts-for-districts/{districtName}`: admin district lookup, requires `x-admin-api-key`.
- `GET /api/generate-submissions-by-month?start=...&end=...`: admin monthly reporting, requires `x-admin-api-key`.
- `GET /api/submissions/district/{districtName}`: admin district submission report, requires `x-admin-api-key`.
- `GET /api/interest-forms-per-district-per-quarter`: admin markdown report for the current quarter plus the prior 2 years by `ReceivedOnUtc`, falling back to Cosmos `_ts` for legacy records, grouped by country and district, requires `x-admin-api-key`.
- `GET /api/health`: anonymous health probe.

## Delivery and Retry

Submissions are persisted before outbound email starts. `SentOnUtc` is set only after every required outbound email message for that submission succeeds. Retryable SMTP/provider failures and quota failures leave `SentOnUtc=null`, set `EmailDeliveryStatus=RetryPending`, and keep the submission eligible for the scheduled retry function.

Flex Consumption does not support `WEBSITE_TIME_ZONE`, so the retry trigger wakes hourly and only runs when the configured `emailRetryTimeZone` resolves to local hour `03:00`.

## Email Examples

The examples below show representative HTML email output rendered by `EmailTemplateService` for common routing paths. Blank or null form fields are omitted from the information block. If both `StudentEmail` and `ParentEmail` are provided, both are included as recipients. Top-level `<p>` elements are split onto separate lines here for readability.

### Student routed to one district

To: district6630@example.org, jordan@example.com, parent@example.com

Subject: Rotary Youth Exchange interest from Jordan Example

Body:

```html
<p>Hello RYE District 6630 Representatives,</p>
<p>An interested person in your district has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>.</p>
<p>They have been informed of the relevant Rotary district and told to expect a follow up within 2 weeks.</p>
<p>Here is the information from the form submission:</p>
<p><strong>Who are you?:</strong> Student<br><strong>Name:</strong> Jordan Example<br><strong>Current age (years):</strong> 16<br><strong>Student's email:</strong> jordan@example.com<br><strong>Student's phone number:</strong> 555-0100<br><strong>Parent's email:</strong> parent@example.com<br><strong>Parent's phone number:</strong> 555-0101<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Cleveland<br><strong>Zip code or first 3 of CDN postal code:</strong> 44102<br><strong>Question:</strong> Can I choose a country?</p>
<p>If you have any admin support questions, advice for the process, need to add or remove email addresses for your district, or want a list of previous submissions, please reach out to <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your support of <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

### Parent submission on a district border

To: district6630@example.org, district6650@example.org, student@example.com, parent@example.com, contact@example.com

Subject: Rotary Youth Exchange interest from Pat Parent

Body:

```html
<p>Hello RYE District 6630 and District 6650 Representatives,</p>
<p>An interested person has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>.</p>
<p>This submission matched multiple Rotary districts (District 6630 and District 6650), so all matching districts have been included.</p>
<p>The submitter should expect a follow up within 2 weeks.</p>
<p>Here is the information from the form submission:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Pat Parent<br><strong>Current age of your student (years):</strong> 15<br><strong>Student's email:</strong> student@example.com<br><strong>Parent's email:</strong> parent@example.com<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Pennsylvania<br><strong>City:</strong> Erie<br><strong>Zip code or first 3 of CDN postal code:</strong> 16501</p>
<p>If you have any admin support questions, advice for the process, need to add or remove email addresses for your district, or want a list of previous submissions, please reach out to <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your support of <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

### Mexico country routing

To: mexico-coordinator@example.org, contact@example.com

Subject: Rotary Youth Exchange interest from Alex Example

Body:

```html
<p>Hello RYE Mexico Representatives,</p>
<p>An interested person in your country has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>.</p>
<p>They have been told to expect a follow up within 2 weeks.</p>
<p>Here is the information from the form submission:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Alex Example<br><strong>Current age of your student (years):</strong> 16<br><strong>Contact email:</strong> contact@example.com<br><strong>Contact phone number:</strong> 555-0102<br><strong>Country of residence:</strong> Mexico<br><strong>City:</strong> Monterrey<br><strong>Question:</strong> Is there an application deadline?</p>
<p>If you have any admin support questions, advice for the process, need to add or remove email addresses for your country, or want a list of previous submissions, please reach out to <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your support of <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

### Manual routing review

To: operator@example.test, contact@example.com, support@example.test

Subject: Rotary Youth Exchange interest needs routing review

Body:

```html
<p>Hello,</p>
<p>An interested person has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>.</p>
<p>The automated system was not able to resolve where this submission should be forwarded, so an admin should review it.</p>
<p>The submitter should expect a follow up within 2 weeks.</p>
<p>Here is the information from the form submission:</p>
<p><strong>Who are you?:</strong> Other<br><strong>Name:</strong> Taylor Example<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>City:</strong> Unknown City</p>
<p>Routing notes: Zipcode missing for district routing</p>
<p>If you have any admin support questions, advice for the process, need to add or remove email addresses for this submission, or want a list of previous submissions, please reach out to <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your support of <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

For `Rotarian` and `Other` submissions, `supportEmail` is copied on the outgoing message. For student and parent submissions, `supportEmail` is not copied.

## Security and Retention

Production and test use isolated Function Apps, Storage accounts, Key Vaults, app settings, and Cosmos containers. Both environments use the existing Cosmos account `studyabroadscholarshipsdb` and `EmailForwarding` database in resource group `EmailForwardingApi`; production uses `ContactInfoAndRequests`, while test uses `TestContactInfoAndRequestsByType`. Non-production email is routed to the `nonProductionSafeRecipient` Key Vault secret unless unsafe delivery is explicitly enabled.

Rotate Key Vault secrets per environment by creating a new secret version and restarting the Function App after validation. PII-bearing submissions and raw request logs should be retained only as long as the operating program requires; align Cosmos retention or scheduled purge jobs with the organization retention policy before production launch.
