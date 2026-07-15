# Email Forwarding Application Replacement Specification

## 1. Purpose

This document is the replacement-grade specification for the current Email Forwarding Application repository.

It captures:

- The business purpose of the system
- The exact behaviors implemented by the current Azure Functions application
- The logical data models and storage semantics
- The HTTP API surface and downstream side effects
- The deployment and configuration model
- Legacy defects and ambiguities that the rewrite must resolve intentionally

The goal is that a new implementation can replace the current application without requiring the original codebase at runtime.

## 2. Scope

This repository contains a backend-only application. It does not contain the public website UI, but it does implement the API that the external website uses.

In scope:

- Form submission intake
- Contact directory storage and versioned contact management for district and country representatives
- Submission forwarding by email
- Submitter confirmation and rejection emails
- Admin/reporting endpoints
- Automated testing, deployment validation, and release promotion
- Azure deployment infrastructure for the function app, Cosmos DB resources, and supporting platform resources across `dev`, `test`, and `prod`

Out of scope in this repository:

- The public `studyabroadscholarships.org` website UI
- A hosted admin UI
- Provisioning the third-party SMTP tenant/account itself
- Storing live secret values in source control

## 3. System Summary

The system receives a contact/interest form submission from `studyabroadscholarships.org`, stores the raw payload and normalized submission data, determines who should receive the submission, forwards the submission by email, and sends a reply email back to the submitter.

Routing rules:

- If the submitter lives in the USA or Canada, route by district based on zipcode or postal prefix.
- If the submitter lives anywhere else, route by country.
- If no district or country contact can be found, notify the default operator email and still email the submitter.
- If a country exists but is marked not certified, do not forward to representatives; instead send a rejection email to the submitter.

The same Cosmos container also stores the contact directory and raw request logs.

## 4. Actors

- Prospect or family: submits the form on the public website.
- District representative: receives submissions for a matching USA/Canada district.
- Country representative: receives submissions for a matching non-USA/non-Canada country.
- Site operator: maintains contact records and receives fallback notifications when routing fails.
- Admin/reporting consumer: calls lookup/report endpoints to inspect districts or submission history.
- DevOps operator: deploys infrastructure and code to Azure.

## 5. High-Level Architecture

### 5.1 Runtime Components

- Public website
  - External to this repo
  - Collects user form data
  - Calls the Azure Function HTTP API
- Azure Functions app
  - .NET 6
  - Azure Functions v4
  - In-process model
  - HTTP-triggered submission/admin functions
  - Timer-triggered retry function for unsent submissions
- Cosmos DB
  - Database: `EmailForwarding`
  - Container: `ContactInfoAndRequestsByType`
  - Stores submissions, contact records, and raw request logs together
- SMTP provider
  - Used to forward emails and send submitter notifications
- Azure Key Vault
  - Provides secrets to the Function App in Azure
- Azure Storage
  - Required for Azure Functions runtime
  - Not used as an application-domain data store by the business logic
- Application Insights
  - Receives runtime telemetry

### 5.2 External Identifiers and Brands Used by the App

- Public site referenced in email bodies: `https://studyabroadscholarships.org/`
- Support contact referenced in email bodies: `StudyAbroadScholarshipsWebsite@gmail.com`
- Current SMTP host in code: `smtp.gmail.com`

## 6. Current Technology Baseline

- Language: C#
- Target framework: `net6.0`
- Hosting model: Azure Functions v4
- Data SDK: `Microsoft.Azure.Cosmos`
- Email SDK: `MailKit` + `MimeKit`
- Secret-related packages are referenced for Azure identity/Key Vault, but the function code itself reads secrets through environment variables.

## 7. Environment and Configuration Specification

### 7.1 Required Runtime Settings

The replacement must resolve the following settings from environment variables, app configuration, or Key Vault references:

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

Additional platform settings required by Azure Functions are set through infrastructure:

- `FUNCTIONS_WORKER_RUNTIME=dotnet`
- `FUNCTIONS_EXTENSION_VERSION=~4`
- `AzureWebJobsStorage`
- `APPINSIGHTS_INSTRUMENTATIONKEY`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

### 7.2 Deployment Environment Model

The rewrite must support three isolated deployment environments:

- `dev`
- `test`
- `prod`

Environment requirements:

- Each environment must have its own Function App, Storage account, Application Insights instance, Key Vault, and Cosmos data resources.
- Production data, secrets, and credentials must never be shared with `dev` or `test`.
- Non-production email delivery must use sandbox recipients, a mail sink, or another approved safe-delivery mechanism.
- Each environment must have its own function keys, admin credentials, and deployment approvals.

### 7.3 Azure Key Vault Wiring

All secrets must be supplied through Azure Key Vault references or an equivalent secret-management integration.

At minimum, the Azure-hosted app must resolve these secrets from Key Vault:

- `sendingEmailAddress`
- `databaseConnectionString`
- `sendingEmailPassword`

The rewrite must not rely on checked-in plaintext secrets or conflicting secondary config sources.

### 7.4 Local Development Notes

- `api/Properties/launchSettings.json` sets the local port to `7011`.
- `local.settings.json` is referenced by the project file but is not checked in.
- `api/appsettings.json` exists in source control and contains plaintext values, but there is no custom startup/configuration code in the repo that loads it into the Functions configuration pipeline. The rewrite should treat this file as a legacy artifact, remove the live secrets from it, and replace it with a sample-only configuration file if one is still needed.
- The `azurite/` files in the repo are empty local Azure Storage emulator state files. They do not contain application business data.
- Local development must use local emulators or dedicated `dev` resources only; it must never require production secrets.

### 7.5 Authorization Model

The rewrite must separate public submission access from admin/reporting access.

Authorization requirements:

- The public submission endpoint must use a credential path that is distinct from admin/reporting credentials.
- Admin/reporting endpoints must require stronger authentication than a shared function key, preferably Microsoft Entra-backed application auth, managed identity, or equivalent service-to-service access.
- Each environment must have distinct credentials, keys, and role assignments.
- Debug-only endpoints must be disabled in `prod` or protected as internal-only operations.

## 8. Logical Storage Specification

### 8.1 Logical Data Store

The current system stores multiple logical entity types in one Cosmos container:

- `InterestFormSubmission`
- `ContactsForDistrict`
- `ContactsForCountry`
- `RequestBodyLog`

The document discriminator field is `Type`.

### 8.2 Effective Versioning Rules

Contacts are append-only in the current implementation.

- Creating district contacts inserts new documents.
- Creating country contacts inserts new documents.
- Existing contact documents are never updated or deleted by the API.

The code treats the most recent document as authoritative in some cases by querying:

- `SELECT TOP 1 ... ORDER BY c._ts desc`

Important legacy behavior:

- District lookup by zipcode does **not** use only the newest district document. It searches across all `ContactsForDistrict` documents and groups by district name.
- Once a zipcode appears in any historical district record, that district may still be returned during routing even if a newer record no longer contains that zipcode.

The rewrite should preserve the intended business meaning, but it should not preserve accidental historical-lookup bugs unless explicitly chosen.

Replacement requirements:

- District and country contacts must have explicit version metadata such as `Version`, `EffectiveFromUtc`, and either `EffectiveToUtc` or `IsActive`.
- Routing must use one deterministic effective version per district or country at the time of submission processing.
- Historical versions may be retained for audit purposes, but historical zipcode membership must not influence current routing unless that version is explicitly effective.
- Lookup and reporting endpoints must select the newest effective version deterministically rather than relying on implicit query ordering.

### 8.3 Timestamps

The business models do not define explicit created/updated timestamp fields.

The current code relies on Cosmos system metadata:

- `_ts` is used for:
  - picking the newest district/country contact record
  - monthly reporting
  - last-year district submission reporting

Replacement requirement:

- The new system must expose reliable received and sent timestamps for submissions, plus a reliable effective-version timestamp for contact records.
- If storage technology changes, these timestamps must become explicit application fields rather than implicit database metadata.
- Submission receipt and email-delivery timestamps must be stored on the submission record itself:
  - `ReceivedOnUtc`: set exactly once when a valid submission is first stored.
  - `SentOnUtc`: null until all required outbound email work for that submission has completed successfully.
- Failed or quota-blocked email attempts must not populate `SentOnUtc`.

## 9. Data Model Specification

The replacement may use a different physical schema, but it must preserve these logical entities and behaviors.

### 9.1 InterestFormSubmission

Stored document type: `InterestFormSubmission`

Purpose:

- Canonical stored form submission
- Used for routing, reporting, and troubleshooting

Legacy field-level behavior:

The current implementation treated most incoming fields as effectively required because it called string methods such as `.Trim()` without null checks. That behavior is legacy context only. The replacement request and storage model must be mostly nullable because fields may be removed, renamed, or added as the public form evolves.

| Field | Legacy Type | Legacy Null Handling | Normalization / Behavior | Notes |
| --- | --- | --- | --- | --- |
| `id` | string | generated server-side | New GUID string | Primary identifier returned to caller |
| `Type` | string | generated server-side | Always `InterestFormSubmission` | Discriminator |
| `IsInterestedOutboundStudent` | string | Legacy code throws if null | `Trim().ToLower()` | Stored as string, not boolean |
| `IsInterestedInHosting` | string | Legacy code throws if null | `Trim().ToLower()` | Stored as string, not boolean |
| `SubmissionQuestion` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `Name` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `Age` | string | Legacy code throws if null | `Trim()` | No numeric validation |
| `Gender` | string | Legacy code throws if null | `Trim()` | Free text |
| `Email` | string | Legacy code throws if null | `Trim()` | No format validation |
| `Phone` | string | Legacy code throws if null | `Trim()` | No format validation |
| `CountryOfResidence` | string | Legacy code throws if null | Lowercase, remove `"the "`, remove periods, remove spaces, then alias-map | Drives routing |
| `State` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `City` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `Zipcode` | string | Legacy code throws if null | `Trim().ToUpper()`, then truncate by country | USA -> first 5 chars, Canada -> first 3 chars, others unchanged |
| `CountryChoiceOne` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `CountryChoiceTwo` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `CountryChoiceThree` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `CountryChoiceFour` | string | Legacy code throws if null | `Trim()` | Empty string is allowed |
| `Errors` | array of string | initialized server-side | Starts empty, accumulates processing errors | Upserted back into storage on failure |

Replacement nullability and schema-evolution requirements:

- Public form fields must be nullable in the request DTO and stored submission model unless explicitly listed as server-generated metadata.
- The API must tolerate missing, null, empty, or newly added form fields without throwing.
- Unknown fields should either be preserved in a flexible `AdditionalFields`/raw payload area or safely ignored according to the chosen storage design.
- Removed legacy fields must not break deserialization, persistence, routing retry, reporting, or email rendering.
- Email templates must render missing values as blank, `Unknown`, or another documented placeholder instead of failing.
- Server-generated metadata remains non-null after persistence: `id`, `Type`, `ReceivedOnUtc`, `EmailDeliveryStatus`, `EmailDeliveryAttempts`, and `Errors`.
- `SentOnUtc` and `NextEmailAttemptOnUtc` are intentionally nullable.
- `Email` should be nullable in the model, but successful submitter email delivery requires a usable email address.
- `CountryOfResidence` should be nullable in the model, but successful routing requires a selected canonical country value.
- `Zipcode` should be nullable in the model, but USA/Canada district routing requires a usable zipcode or postal prefix.

Replacement delivery-tracking fields:

| Field | Type | Required | Behavior |
| --- | --- | --- | --- |
| `ReceivedOnUtc` | datetime | yes | Set when the normalized submission is first persisted; never changed afterward |
| `SentOnUtc` | datetime or null | yes | Null on initial insert; set only after every required outbound email message for the submission is sent successfully |
| `EmailDeliveryStatus` | string | yes | One of `Pending`, `Sent`, `RetryPending`, or `TerminalFailed` |
| `EmailDeliveryAttempts` | array of object | yes | Structured attempt history containing timestamp, message type, recipient group, outcome, provider response/code when available, and retry classification |
| `NextEmailAttemptOnUtc` | datetime or null | no | Optional scheduling hint for retryable failures |

The API may expose camelCase names such as `receivedOnUtc` and `sentOnUtc`, but the canonical model must use consistently spelled "received" terminology rather than the misspelled `recieved`.

Legacy country normalization rules:

- Remove leading `"the "`
- Remove periods
- Remove spaces
- Lowercase the value
- Map any of the following to `usa`:
  - `unitedstatesofamerica`
  - `us`
  - `unitedstates`
  - `america`
- Map any of the following to `uk`:
  - `britian`
  - `unitedkingdom`
  - `england`

Legacy zipcode normalization rules:

- If normalized country is `usa`, keep only the first 5 characters.
- If normalized country is `canada`, keep only the first 3 characters.
- Otherwise keep the entire uppercase value.

Compatibility requirement for the rewrite:

- The public form is expected to move to a controlled country dropdown, so replacement country handling should use canonical dropdown values instead of free-text country validation.
- Legacy country aliases may be kept only as a backwards-compatibility adapter for older callers or historical data migration.
- Do not add strict free-text country validation rules to the new public API contract.
- Replace string booleans with real booleans in the new domain model if desired, but maintain backward-compatible request handling or provide a migration layer.

Example logical stored document:

```json
{
  "id": "6c44192f-7b90-498b-95cb-e43364d3e89d",
  "Type": "InterestFormSubmission",
  "IsInterestedOutboundStudent": "yes",
  "IsInterestedInHosting": "no",
  "SubmissionQuestion": "Can I study in Japan?",
  "Name": "Jordan Example",
  "Age": "16",
  "Gender": "female",
  "Email": "jordan@example.com",
  "Phone": "555-111-2222",
  "CountryOfResidence": "usa",
  "State": "OH",
  "City": "Cleveland",
  "Zipcode": "44102",
  "CountryChoiceOne": "Japan",
  "CountryChoiceTwo": "Brazil",
  "CountryChoiceThree": "",
  "CountryChoiceFour": "",
  "ReceivedOnUtc": "2026-01-15T14:25:30Z",
  "SentOnUtc": null,
  "EmailDeliveryStatus": "Pending",
  "EmailDeliveryAttempts": [],
  "Errors": []
}
```

### 9.2 ContactsForDistrict

Stored document type: `ContactsForDistrict`

Purpose:

- Maps a district to email recipients and zipcode coverage
- Used for USA/Canada routing

Field specification:

| Field | Legacy Type | Required for Useful Behavior | Normalization / Behavior | Notes |
| --- | --- | --- | --- | --- |
| `id` | string | generated server-side in constructor | GUID string | Identifier |
| `Type` | string | generated server-side in constructor | Always `ContactsForDistrict` | Discriminator |
| `Country` | string | yes | No normalization in current code | Must match normalized submission country if used elsewhere |
| `District` | string | yes | No normalization in current code | Used in exact-match lookups |
| `EmailAddresses` | array of string | yes | No normalization | All values are forwarded to SMTP as-is |
| `ZipCodes` | array of string | yes for zipcode-based routing | No normalization in current code | Must already be in comparable normalized form |

Operational semantics:

- District contact uploads insert new records rather than replacing old ones.
- Zipcode lookup searches any historical record with a matching zipcode and groups by district.
- Email forwarding then fetches the newest record for each returned district and uses its email list.

This means legacy routing is a hybrid of:

- Historical zipcode membership
- Current email recipient list

That behavior is almost certainly accidental, but it is part of the current implementation.

Replacement semantics:

- District contact changes must create deterministic effective versions.
- Zipcode routing must consider only the effective version or versions that are active for the submission processing time.
- Historical versions must be queryable for audit, but they must not affect live routing unless explicitly marked effective.
- The replacement data model must add explicit version and effective-date metadata even if the legacy fields remain for compatibility.

Example logical stored document:

```json
{
  "id": "a7126dc6-0596-4706-8bff-8145ec9cd2cb",
  "Type": "ContactsForDistrict",
  "Country": "usa",
  "District": "6630",
  "EmailAddresses": [
    "rep1@example.org",
    "rep2@example.org"
  ],
  "ZipCodes": [
    "44102",
    "44103",
    "44104"
  ]
}
```

### 9.3 ContactsForCountry

Stored document type: `ContactsForCountry`

Purpose:

- Maps a country to email recipients and certification status
- Used for non-USA/non-Canada routing

Field specification:

| Field | Legacy Type | Required for Useful Behavior | Normalization / Behavior | Notes |
| --- | --- | --- | --- | --- |
| `id` | string | generated server-side in constructor | GUID string | Identifier |
| `Type` | string | generated server-side in constructor | Always `ContactsForCountry` | Discriminator |
| `Country` | string | yes | No normalization in current code | Must match normalized submission country exactly |
| `EmailAddresses` | array of string | required when `IsCertified=true` | No normalization | Used for forwarding |
| `IsCertified` | boolean | yes | Stored as provided | If false, submitter gets rejection email instead of forwarding |

Operational semantics:

- The newest record by `_ts` is considered the effective country record.
- If the country exists and `IsCertified=false`, the system treats that as a valid business outcome, not an error.

Replacement semantics:

- Country contact changes must create deterministic effective versions.
- Country routing must select the active effective country record using explicit version/effective-date metadata.
- `IsCertified=false` remains a successful business outcome if the rejection email is sent successfully.
- The replacement data model must add explicit version and effective-date metadata even if the legacy fields remain for compatibility.

Example logical stored document:

```json
{
  "id": "c0f8d6b8-5f22-4c8c-aa68-f6b2485df961",
  "Type": "ContactsForCountry",
  "Country": "japan",
  "EmailAddresses": [
    "country-rep@example.org"
  ],
  "IsCertified": true
}
```

### 9.4 RequestBodyLog

Stored document type: `RequestBodyLog`

Purpose:

- Stores the raw request body before deserialization and validation
- Used for troubleshooting malformed submissions

Field specification:

| Field | Legacy Type | Behavior |
| --- | --- | --- |
| `id` | string | Generated GUID |
| `Type` | string | Always `RequestBodyLog` |
| `RequestBody` | string | Exact raw request body payload |

Example logical stored document:

```json
{
  "id": "d9e7f05b-cf87-4dbd-9397-ee99717ff3a2",
  "Type": "RequestBodyLog",
  "RequestBody": "{\"name\":\"Jordan Example\",\"email\":\"jordan@example.com\"}"
}
```

### 9.5 Reporting DTOs

These are transport objects, not persisted business entities:

- `SubmissionsByMonthRequest`
  - `StartDate: DateTime`
  - `EndDate: DateTime`
- `SubmissionsByMonth`
  - `Month: string`
  - `CountryResults: List<countryResults>`
- `countryResults`
  - `Name: string`
  - `NumberOfSubmissions: int`
- `InterestFormsResponse`
  - Defined in source but currently unused by any function
- `ErrorSubmission`
  - Defined in source but currently unused by any function

## 10. Submission Processing Specification

This section specifies the main business flow for form submissions.

### 10.1 Entry Point

- Method: `POST`
- Route: `/api/interest-form-entry`
- Authorization: public submission credential required, distinct from admin/reporting credentials

### 10.2 Input Contract

The replacement must accept a JSON object compatible with the legacy `InterestFormSubmission` request shape, while allowing the public form to remove fields, add fields, or send null values over time.

Validation requirements:

- Missing or null optional form fields must not produce server exceptions and should not produce validation failures by default.
- Validation must run before routing and email sending, but it should focus on JSON shape, dangerous payloads, size limits, and fields that are truly required for a specific downstream action.
- The response body for validation failures must be structured JSON that identifies the invalid fields and a correlation or submission identifier when available.
- `CountryOfResidence` should come from a controlled dropdown value, so the API should not perform free-text country validation.
- If `CountryOfResidence` is missing or null, store the submission and mark routing/email delivery according to the missing-routing-data rules rather than failing during deserialization.
- Additional domain validation, such as email-format checks or stricter age rules, may be added intentionally, but any such tightening must be documented as a business-rule change and must not assume every legacy field is still present.

Compatibility requirement:

- If the rewrite introduces a cleaner internal DTO, it must still accept the legacy payload shape or provide a documented compatibility adapter.
- The compatibility adapter must be null-safe for all legacy form fields.

Example legacy request payload:

```json
{
  "IsInterestedOutboundStudent": "Yes",
  "IsInterestedInHosting": "No",
  "SubmissionQuestion": "Can I participate next year?",
  "Name": "Jordan Example",
  "Age": "16",
  "Gender": "Female",
  "Email": "jordan@example.com",
  "Phone": "555-111-2222",
  "CountryOfResidence": "United States",
  "State": "OH",
  "City": "Cleveland",
  "Zipcode": "44102-1234",
  "CountryChoiceOne": "Japan",
  "CountryChoiceTwo": "Brazil",
  "CountryChoiceThree": "",
  "CountryChoiceFour": ""
}
```

Example sparse future-compatible payload:

```json
{
  "Name": "Jordan Example",
  "Email": "jordan@example.com",
  "CountryOfResidence": "usa",
  "Zipcode": null,
  "NewProgramInterest": "Summer exchange"
}
```

### 10.3 Required Processing Sequence

The replacement must preserve the legacy audit-first intent while adding explicit validation:

1. Resolve environment-specific configuration and secrets.
2. Read the entire raw request body as a string.
3. Store a `RequestBodyLog` document or equivalent audit record containing the raw request body.
4. Deserialize the request body to a request DTO.
5. Validate the request DTO for structural and safety requirements, while allowing nullable/missing optional form fields.
6. Construct a normalized `InterestFormSubmission` or replacement canonical submission model.
7. Set `ReceivedOnUtc` and persist the normalized submission with `SentOnUtc=null` and `EmailDeliveryStatus=Pending` before attempting downstream email sending.
8. Determine routing path: USA/Canada submissions use district routing; all other countries use country routing.
9. Send downstream representative/operator email as applicable.
10. Send the submitter confirmation or rejection email as applicable.
11. Record structured delivery-attempt outcomes on the submission record.
12. If all required outbound email messages succeed, update `SentOnUtc` and set `EmailDeliveryStatus=Sent`.
13. If any required outbound email message fails with a retryable error, leave `SentOnUtc=null`, set `EmailDeliveryStatus=RetryPending`, and return a response that clearly indicates the submission was stored but email delivery is pending.
14. If routing or email delivery reaches a terminal failure, leave `SentOnUtc=null`, set `EmailDeliveryStatus=TerminalFailed`, and return a structured error response.
15. Otherwise return the stored submission object.

### 10.4 District Routing Rules

District routing applies when the selected canonical `CountryOfResidence` is:

- `usa`
- `canada`

District routing algorithm:

1. Normalize zipcode during submission creation.
2. Query effective district membership by normalized zipcode using the active/effective district contact version data.
3. If one or more districts are found:
   - Aggregate all email addresses from the effective district contact records
   - Send the district forwarding email to all collected district email addresses
   - Send a confirmation email to the submitter
4. If no districts are found:
   - Add error: `District not found by zip code`
   - Send a fallback operator email to the default sending account
   - Send a generic follow-up email to the submitter

Behavior when multiple districts match:

- The district representative email explicitly states the system is not sure which district applies and sends the email to all districts found for that zipcode.
- The submitter confirmation email names all districts found.

Nullable-field behavior:

- If `Zipcode` is null or empty for a USA/Canada submission, do not throw.
- Add a structured routing error such as `Zipcode missing for district routing`.
- Send the fallback operator email when possible.
- Send the submitter follow-up email only when `Email` contains a usable address.
- Leave `SentOnUtc=null` unless every required outbound email for the degraded/fallback path is sent successfully.

### 10.5 Country Routing Rules

Country routing applies when the selected canonical `CountryOfResidence` is neither `usa` nor `canada`.

Country routing algorithm:

1. Fetch the effective `ContactsForCountry` record for the normalized country using explicit version/effective-date metadata.
2. If a record exists and `IsCertified=true`:
   - Send the country forwarding email to the country email addresses
   - Send a confirmation email to the submitter
3. If a record exists and `IsCertified=false`:
   - Do not forward to country representatives
   - Send a rejection email to the submitter
   - Treat this as a successful business outcome if email sending succeeds
4. If no record exists:
   - Add error: `country not found`
   - Send a fallback operator email to the default sending account
   - Send a generic follow-up email to the submitter

Nullable-field and dropdown behavior:

- `CountryOfResidence` should be stored as the canonical value selected from the public form dropdown.
- Do not reject submissions because a free-text country spelling does not match a validation list.
- If `CountryOfResidence` is null or empty, do not throw.
- Add a structured routing error such as `Country of residence missing`.
- Send the fallback operator email when possible.
- Send the submitter follow-up email only when `Email` contains a usable address.
- Leave `SentOnUtc=null` unless every required outbound email for the degraded/fallback path is sent successfully.

### 10.6 Persistence Semantics

Important legacy ordering:

- The submission document is stored **before** email sending starts.

Rationale stated in code:

- Email sending is considered the more fragile part of the system.

Implication:

- Submission records exist even when forwarding fails.
- A rewrite must preserve this auditability requirement unless business stakeholders deliberately change it.

Replacement delivery-state requirements:

- Initial persistence is the source of truth that the submission was received.
- `ReceivedOnUtc` is set before any outbound email attempt.
- `SentOnUtc` is set only after all required outbound emails for the submission are confirmed sent.
- If email delivery fails, including provider daily-send-limit failures, the system must keep `SentOnUtc=null`.
- Delivery failures must be stored as structured data on the submission record, not only as plain-text log messages.
- Partial email success must be tracked at the individual outbound-message level or made idempotent so the retry process does not resend messages that were already successfully delivered.

### 10.7 Error Accumulation Semantics

The legacy main flow accumulates string errors on `InterestFormSubmission.Errors`.

Sources of accumulated errors include:

- No district found by zipcode
- No country found
- Failures returned from email sending methods
- Retryable SMTP/provider failures, including daily send-limit or quota-exceeded responses
- Terminal SMTP/provider failures, such as invalid authentication or permanently invalid sender configuration

Legacy behavior when `Errors` is non-empty after routing:

- The submission is upserted back into Cosmos with the error list
- The API returns HTTP `400`
- The response body is structured JSON containing the submission ID and serialized error list

Replacement error-handling and response requirements:

- Provider errors must be classified as `Retryable`, `QuotaExceeded`, or `Terminal`.
- Daily send-limit and max-emails-per-day failures must be treated as retryable quota failures.
- Retryable or quota failures must leave `SentOnUtc=null`, set `EmailDeliveryStatus=RetryPending`, and return HTTP `202` for the initial submission request when the submission itself was valid and persisted.
- Terminal business or provider failures must leave `SentOnUtc=null`, set `EmailDeliveryStatus=TerminalFailed`, and return a structured HTTP `400` or `500` depending on whether the failure is client/business-correctable or server/operator-correctable.
- Plain text logs may still exist for observability, but the submission record must contain the durable delivery state needed to retry or investigate the failure.

### 10.8 Catastrophic Error Behavior

If raw request logging fails:

- Log exception
- Attempt to email the default sending address
- Return HTTP `500`

If deserialization or submission-model creation fails:

- Log exception
- Attempt to email the default sending address
- Return HTTP `500`

If a later unexpected exception occurs:

- Add the exception message to `Errors`
- Log exception
- Attempt to email the default sending address
- Return HTTP `400`

Legacy caveat:

- The custom logging service dereferences nested `InnerException` values without null checks and may itself throw while handling another exception.

Replacement requirement:

- Logging and exception handling must be null-safe, structured, and unable to throw secondary exceptions while handling the original failure.

### 10.9 Scheduled Unsent Submission Retry

The replacement must include a timer-triggered Azure Function that retries unsent submissions.

Schedule:

- Run once every morning at 3:00 AM in the application's business timezone, currently `America/New_York`.
- If deployed on Azure Functions with NCRONTAB scheduling, the implementation must explicitly document whether the schedule uses UTC or a configured app timezone such as Windows `Eastern Standard Time`.

Selection rule:

- At minimum, select every submission from the previous local calendar day where `SentOnUtc` is null and `EmailDeliveryStatus` is `Pending` or `RetryPending`.
- The preferred implementation should also include older unsent retryable submissions so backlog does not become stranded after repeated provider quota failures.
- Terminal failures must not be retried automatically unless an operator explicitly resets them to a retryable state.

Retry behavior:

- Reconstruct or load the outbound email plan for each unsent submission.
- Send only the outbound messages that have not already been marked sent.
- If all required messages for a submission are sent successfully, set `SentOnUtc` and `EmailDeliveryStatus=Sent`.
- If the provider reports max-emails-per-day, quota exceeded, or equivalent rate-limit exhaustion, stop or throttle the batch, leave affected submissions with `SentOnUtc=null`, and keep them eligible for the next retry window.
- Record each retry attempt in `EmailDeliveryAttempts` with timestamp, outcome, provider response/code when available, and retry classification.
- Use a lease, optimistic concurrency, or equivalent guard so concurrent timer executions cannot send the same pending message twice.

## 11. Email Specification

### 11.1 SMTP Transport

Legacy transport behavior:

- Host: `smtp.gmail.com`
- Port: `587`
- Security: `StartTls`
- Auth: `sendingEmailAddress` + `sendingEmailPassword` (Gmail app password)

Replacement requirements:

- The rewrite must use a single authoritative mail-provider configuration source.
- SMTP host, port, security mode, username, and password must be environment-configurable.
- Checked-in config files must not contain contradictory live-provider settings.
- The initial production deployment uses Gmail SMTP by default, but the provider must not be hardcoded in application logic.

### 11.2 Email Subjects

District or country forwarding email subject:

- `Rotary Youth Exchange Form Submission From {submitterEmail}`

Submitter-facing email subject:

- `Study Abroad Scholarships with Rotary form submission `

Fallback operator email subject:

- `District or Country Not Found`

Raw-request-log failure email subject:

- `submission unable to be logged to database`

General processing failure email subject:

- `Failure to process submission or send email`
- or `Failure to send to database or process submission`

### 11.3 District Representative Email

Recipients:

- All email addresses from the newest matching `ContactsForDistrict` records

Body requirements:

- Greeting names the district or districts
- If multiple districts matched, explicitly say the system is not sure which district applies
- State that an interested person in the district submitted a Rotary Youth Exchange contact form at `studyabroadscholarships.org`
- State that the submitter was informed of the district number and told to expect follow-up within a couple of weeks
- Include a student-information block containing all submission fields
- Include an instruction to contact `StudyAbroadScholarshipsWebsite@gmail.com` for questions, advice, directory maintenance, or prior submissions

### 11.4 Country Representative Email

Recipients:

- All email addresses from the newest matching `ContactsForCountry` record

Body requirements:

- Greeting names the country in title case
- State that an interested person in the country submitted a Rotary Youth Exchange contact form
- State that the submitter was told to expect follow-up within a couple of weeks
- Include the same student-information block
- Include the same support contact text

### 11.5 Submitter Confirmation Email

Sent when:

- District was found
- Country was found and certified
- District/country was not found

Body requirements:

- Greet the submitter by name
- Thank them for their interest in StudyAbroadScholarships.org
- State that a representative from Study Abroad Scholarships / Rotary Youth Exchange will follow up within 2 weeks
- If district or country is known, mention it
- If not known, no district/country is named
- Tell the submitter to contact `StudyAbroadScholarshipsWebsite@gmail.com` if they do not hear back within 2 weeks
- Include the student-information block

Important legacy behavior:

- Even when no district/country was found, the submitter still receives a generic follow-up email saying a representative will follow up.

### 11.6 Submitter Rejection Email

Sent when:

- A country contact exists and `IsCertified=false`

Body requirements:

- Greet the submitter by name
- Thank them for their interest
- State that the relevant country program or Rotary exchange path is not currently certified for this scholarship/exchange flow
- State that they are not eligible for this scholarship
- Tell them to contact the support address if they believe there is a mistake
- Include the student-information block

### 11.7 Operator Fallback Email

Sent when:

- No district was found for a USA/Canada submission
- No country was found for a non-USA/non-Canada submission

Recipients:

- Only the default sending account address

Body requirements:

- State that no district or country was found
- Ask the operator to reach out to the student within a week
- Ask the operator to update the database if information is missing
- Suggest resubmission if the original form was incorrect
- Include the student-information block
- Include the raw serialized submission JSON

### 11.8 Nullable Field Rendering

Email body generation must be null-safe.

Requirements:

- Missing or null student-information fields must render as blank, `Unknown`, or another documented placeholder.
- Email rendering must not call string methods on nullable fields without null handling.
- If the submitter email address is missing or unusable, skip submitter-facing email and record a structured delivery/routing error.
- Representative and operator emails should still be sent when enough routing or fallback information exists.
- The raw request body or preserved additional fields should remain available for operator troubleshooting when known fields are missing.

## 12. HTTP API Specification

All routes below assume the default Azure Functions route prefix `/api`.

### 12.1 POST `/api/interest-form-entry`

Purpose:

- Accept and process a public interest form submission

Authorization:

- Public submission credential required
- This credential path must be separate from admin/reporting credentials

Request body:

- JSON object matching the `InterestFormSubmission` input shape

Success response:

- HTTP `200`
- Body: normalized `InterestFormSubmission` object
- Response body includes `ReceivedOnUtc`, `SentOnUtc`, and `EmailDeliveryStatus`

Delivery-pending response:

- HTTP `202`
- Used when the submission is valid and stored but email delivery failed with a retryable or quota-related error
- Body: normalized `InterestFormSubmission` object with `SentOnUtc=null`, `EmailDeliveryStatus=RetryPending`, and structured delivery-attempt detail

Business-error response:

- HTTP `400`
- Body: structured JSON containing submission ID when available and an error list

Failure response:

- HTTP `500` when early processing or logging/deserialization fails
- Failure responses should include a correlation identifier suitable for support investigation

Side effects:

- Writes `RequestBodyLog`
- Writes `InterestFormSubmission` with `ReceivedOnUtc` set and `SentOnUtc=null`
- May update `InterestFormSubmission.Errors` and structured delivery state
- Sends one or more emails
- Updates `SentOnUtc` only after all required email messages have been sent successfully

### 12.2 POST `/api/contacts-for-districts`

Purpose:

- Bulk insert district contact records

Authorization:

- Admin/internal auth required

Request body:

- JSON array of district contact records

Legacy payload shape example:

```json
[
  {
    "Country": "usa",
    "District": "6630",
    "EmailAddresses": ["rep1@example.org"],
    "ZipCodes": ["44102", "44103"]
  }
]
```

Legacy behavior:

- Deserializes the request body into `List<ContactsForDistrict>`
- Inserts every record as a new document
- Performs no validation, deduplication, or upsert-by-key behavior

Replacement requirements:

- Validate payload shape and reject malformed or duplicate entries with explicit `400` responses.
- Create or update deterministic effective contact versions instead of relying on historical zipcode accumulation.
- Record explicit version/timestamp metadata for each change.

Success response:

- HTTP `200`
- Body: the deserialized array that was submitted

Failure response:

- HTTP `500`

Side effects:

- Appends new district contact versions

### 12.3 POST `/api/contacts-for-countries`

Purpose:

- Bulk insert country contact records

Authorization:

- Admin/internal auth required

Request body:

- JSON array of country contact records

Legacy payload shape example:

```json
[
  {
    "Country": "japan",
    "EmailAddresses": ["country-rep@example.org"],
    "IsCertified": true
  }
]
```

Legacy behavior:

- Deserializes the request body into `List<ContactsForCountry>`
- Inserts every record as a new document
- Performs no validation, deduplication, or upsert-by-key behavior

Replacement requirements:

- Validate payload shape and reject malformed or duplicate entries with explicit `400` responses.
- Create or update deterministic effective contact versions with explicit certification metadata.
- Record explicit version/timestamp metadata for each change.

Success response:

- HTTP `200`
- Body: the deserialized array that was submitted

Failure response:

- HTTP `500`

Side effects:

- Appends new country contact versions

### 12.4 GET `/api/contacts-for-districts/{id}`

Purpose:

- Retrieve the newest district contact record for a district identifier

Authorization:

- Admin/internal auth required

Route parameter:

- `id`: district name/number, exact string match

Legacy lookup rule:

- `SELECT TOP 1 * FROM c WHERE c.District = @District and c.Type='ContactsForDistrict' ORDER BY c._ts desc`

Success response:

- HTTP `200`
- Body: a single district contact object representing the newest effective matching record

Not found response:

- HTTP `404`

Failure response:

- HTTP `500`

Replacement requirement:

- The rewrite must return a single object or `404`, not a collection wrapper.

### 12.5 GET `/api/generate-submissions-by-month`

Purpose:

- Produce month-grouped submission counts by high-level country bucket

Authorization:

- Admin/internal auth required

Query parameters:

- `startDate`
- `endDate`

Accepted format:

- Any format parseable by `DateTime.TryParse`

Legacy validation:

- If either value is missing or invalid, return HTTP `400` with:
  - `Invalid or missing startDate/endDate query parameters. Format: YYYY-MM-DD`

Legacy bucketing logic:

- Generates month-start dates from `StartDate` through `EndDate`, inclusive
- Iterates pairwise between `months[i]` and `months[i + 1]`
- Produces counts for:
  - `USA`
  - `CANADA`
  - `MEXICO`
  - `other country`

Legacy response shape example:

```json
[
  {
    "Month": "January 2026",
    "CountryResults": [
      { "Name": "USA", "NumberOfSubmissions": 4 },
      { "Name": "CANADA", "NumberOfSubmissions": 1 },
      { "Name": "MEXICO", "NumberOfSubmissions": 0 },
      { "Name": "other country", "NumberOfSubmissions": 2 }
    ]
  }
]
```

Replacement requirements:

- The reporting window must include every month touched by the requested range, including the final month.
- A same-month `startDate` and `endDate` must return one bucket for that month.
- Counts must be produced by a true aggregate query or an equivalently correct precomputed/reporting mechanism rather than page-size-dependent counting.

### 12.6 GET `/api/submissions/district/{districtName}`

Purpose:

- Return all stored submissions for the specified district over the last year

Authorization:

- Admin/internal auth required

Route parameter:

- `districtName`

Legacy behavior:

1. Query one district contact record to obtain country and zipcode list.
2. If no district record or zipcode list is found, return `200` with an empty array.
3. Compute one-year-ago timestamp using `DateTime.UtcNow.AddYears(-1)`.
4. Query all `InterestFormSubmission` documents where:
   - `CountryOfResidence = districtCountry`
   - `Zipcode IN districtZipCodes`
   - `_ts >= oneYearAgoTimestamp`
5. Return all matching submissions.

Success response:

- HTTP `200`
- Body: array of `InterestFormSubmission` documents

Notable legacy behaviors:

- It does not filter out submissions that contain errors.
- It returns raw stored submission documents exactly as found.
- The district metadata lookup uses `SELECT TOP 1 ...` without `ORDER BY`, so if multiple contact versions exist the chosen zipcode set is not deterministic.

Replacement requirements:

- The district metadata lookup must select the newest effective district version deterministically.
- Any decision to include or exclude errored submissions must be explicit and documented.

Failure responses:

- HTTP status code from `CosmosException` when possible
- Otherwise HTTP `500`

### 12.7 GET `/api/TestZipCodes/{id}`

Purpose:

- Debug endpoint that returns district names matching a zipcode

Authorization:

- Internal/debug auth only if retained

Route parameter:

- `id`: zipcode or postal prefix

Success response:

- HTTP `200`
- Body: array of district names

Side effects:

- None

Production treatment:

- Do not expose this endpoint in the supported production API unless an explicit operational requirement remains and the endpoint is protected as internal-only.

## 13. Lookup and Query Semantics

### 13.1 Zipcode Matching

The system does exact string matching after submission normalization.

Examples:

- USA submission `44102-1234` becomes `44102`
- Canada submission `M5V 2T6` becomes `M5V`
- District zipcode lists must contain values in that comparable form

### 13.2 Country Matching

The legacy system uses normalized `CountryOfResidence` for submission records but does **not** normalize `ContactsForCountry.Country`.

Implication:

- Country contact data must already use the same normalized naming convention as submission routing.

Replacement requirement:

- The public form should send canonical country values from a dropdown, not arbitrary free text.
- Contact-management workflows must use the same canonical country keys as the dropdown values.
- The API should not reject country values because of free-text spelling or alias validation; legacy alias normalization may exist only as a compatibility/migration layer.
- Null or missing country values must be handled as missing routing data, not as deserialization failures.

### 13.3 District Matching

District names are matched using exact equality.

Implication:

- Callers must know the exact district identifier format stored in Cosmos.

Replacement requirement:

- Contact-management workflows must enforce a documented canonical district identifier format before records become effective.

## 14. Observability and Logging Specification

### 14.1 Application Insights

`host.json` enables Application Insights sampling and excludes request telemetry from sampling exclusions by setting:

- `excludedTypes: "Request"`

### 14.2 Custom Logging

The app uses a `LoggingService` wrapper over `ILogger`.

Current behavior:

- Logs exception message plus the related request body/context
- Attempts to log stack traces and deeply nested inner exceptions

Legacy defect:

- The logging helper dereferences `InnerException` chains without null checks.
- Replacement must implement safe structured logging.

### 14.3 Auditability Requirements for Rewrite

The replacement should preserve or improve:

- Raw request-body capture for malformed submissions
- Submission ID traceability
- Ability to correlate failed routing/email attempts with a stored submission record

## 15. Deployment and Infrastructure Specification

### 15.1 Required Azure Resources Per Environment

Each environment must provision, at minimum:

- Function App
- Hosting plan
- Storage account
- Application Insights
- Key Vault
- Cosmos DB account or equivalently managed Cosmos data-plane resources required by the app
- Cosmos database and container(s) required for submissions, contacts, and request logs

Each Function App must have:

- A system-assigned or user-assigned managed identity
- Permission to read required secrets from Key Vault
- Environment-specific application settings and connection references

### 15.2 Resources Still Managed Outside the Repo

The repo should not store or directly provision:

- Third-party SMTP tenant/account ownership
- Live secret values in source control
- Public website hosting unless the repository scope expands later

DNS, API Management, and front-end hosting may remain separate, but any dependency on them must be documented explicitly.

### 15.3 Environment Parameterization

Infrastructure and deployment configuration must exist for:

- `dev`
- `test`
- `prod`

The repo must include environment-specific parameterization for at least:

- Key Vault name
- Function App name
- Hosting plan name
- Storage account name
- Cosmos account name
- Cosmos database name
- Cosmos container name
- Email retry timer timezone
- Environment label/application setting values

Required parameter files:

- `infra/params/parameters.dev.json`
- `infra/params/parameters.test.json`
- `infra/params/parameters.prod.json`

Production resource names must match deployment targets exactly; the previous `StudyAbroadScholarshipsEmailForwarding` vs. `ProdStudyAbroadScholarshipsEmailForwarding` mismatch is not allowed in the rewrite.

### 15.4 Configuration and Secrets Promotion Rules

- Infrastructure code must create the application configuration shape, Key Vault references, and resource bindings for all environments.
- Secret values must be injected per environment through Key Vault population, pipeline secret tasks, or a documented rotation process outside source control.
- The same application artifact must be promoted from `dev` to `test` to `prod`; only environment configuration may differ between promotions.
- Non-production environments must use non-production secrets, mail sinks, and safe data stores.

### 15.5 Azure DevOps Pipeline Flow

Pipeline files remain in `.azdo/pipelines`, but the rewrite pipeline must support continuous validation and controlled promotion.

Required pipeline sequence:

1. Pull request validation: restore, build, lint/static analysis, and automated tests.
2. Deploy or validate `dev` infrastructure.
3. Build the release artifact once.
4. Deploy the artifact to `dev` and run smoke tests.
5. Deploy or validate `test` infrastructure.
6. Promote the same artifact to `test` and run integration, contract, and end-to-end tests.
7. Require an approval gate before `prod`.
8. Deploy or validate `prod` infrastructure.
9. Promote the same tested artifact to `prod` and run post-deploy smoke validation.

Pipeline requirements:

- The primary validation/release pipeline must not rely on `trigger: none`.
- `dev`, `test`, and `prod` must be modeled as distinct pipeline environments with separate approvals and credentials.
- Build output must be immutable across environment promotion.
- Infrastructure deployment and application deployment must use the same canonical environment naming.

### 15.6 Release and Rollback Requirements

- Infrastructure deployments must be idempotent and safe to rerun.
- Release artifacts must be versioned and retained long enough to support rollback.
- Rollback must use a previously known-good artifact/configuration combination rather than ad hoc hotfix edits in production.
- Post-deploy smoke tests must verify API reachability, data-store connectivity, and safe email-delivery behavior for the target environment.

## 16. Testing Specification

### 16.1 Required Automated Test Suites

The rewrite must include automated coverage for:

- Input validation, nullable-field handling, and schema-evolution behavior
- Country and district routing decisions
- Contact effective-version selection
- Email recipient selection, subject generation, and body rendering
- Null-safe email rendering for missing or removed form fields
- Controlled-dropdown country values and missing-country routing fallback
- Submission persistence and error-upsert behavior
- `ReceivedOnUtc` creation, `SentOnUtc` update, and unsent-state persistence
- Retryable provider failures, especially max-emails-per-day or quota-exceeded responses
- Partial email-delivery behavior that avoids duplicate messages on retry
- 3:00 AM timer-trigger selection of previous-day unsent submissions
- Monthly reporting bucket generation and aggregate counts
- Authorization behavior for public versus admin/reporting endpoints

### 16.2 Integration and Environment Test Strategy

- `dev` must support fast smoke tests and developer verification against safe non-production resources.
- `test` must execute the full integration/end-to-end suite against deployed Azure resources and representative seeded data, including unsent-submission retry scenarios.
- `prod` must run post-deploy smoke validation only, using approved synthetic transactions and no real customer outreach.

Where practical, integration tests should run against Cosmos-compatible test infrastructure such as a dedicated test account or approved emulator strategy.

### 16.3 Test Data and Privacy Requirements

- Automated tests must use synthetic or sanitized data only.
- Test submissions must be identifiable as test traffic in logs and reporting.
- Non-production email tests must deliver to a mail sink, sandbox inbox, or explicitly approved test recipients.
- Test secrets must be isolated from production secrets.

### 16.4 Release Gates

The pipeline must block promotion when any of the following fail:

- Build/package creation
- Unit tests
- Contract/API tests
- Integration/end-to-end tests in `test`
- Post-deploy smoke tests in the target environment

Critical test failures must not be bypassed without an explicit recorded approval.

## 17. Security and Compliance Notes

Security and compliance requirements for the rewrite:

- Remove plaintext secrets from source control.
- Define and document a formal secret-management approach.
- Define retention rules for PII-bearing logs, raw request bodies, and submission records.
- Implement explicit validation and error handling for all public inputs.
- Separate public submission authentication from admin/reporting authentication.
- Ensure non-production environments do not send emails to live end users or representatives unintentionally.

## 18. Resolved Legacy Defects in This Specification

The following legacy defects are considered resolved by the target-state requirements in this document:

1. Monthly reporting must include the final month and use correct aggregate counting semantics.
2. District routing must use deterministic effective contact versions instead of hybrid historical zipcode lookup.
3. District submission reporting must select district metadata with explicit ordering/version rules.
4. Logging must be null-safe and must not fail while handling another exception.
5. Submission validation must be explicit, null-safe, and return structured client errors only for true validation failures.
6. Email-provider configuration must come from one authoritative source.
7. `GET /contacts-for-districts/{id}` must return a single object or `404`.
8. Rejection-email wording must correctly describe country/program certification status.
9. Contact records must use explicit schema/versioning metadata and explicit timestamps.
10. Cosmos infrastructure must be provisioned and configured as first-class infrastructure.
11. CI/CD must support automated validation and `dev`/`test`/`prod` promotion.
12. Dead configuration and unused abstractions must be removed or formally deprecated during the rewrite.
13. Email-send failures must persist structured delivery state on the submission record and must be recoverable through scheduled retry when retryable.

## 19. Replacement Acceptance Criteria

A modernization effort can be considered functionally complete when all of the following are true:

- Public form submissions can be accepted through a documented API contract with explicit validation behavior and mostly nullable form fields.
- Submission data is normalized, stored, and auditable.
- USA/Canada submissions are routed by zipcode/postal prefix to district contacts using deterministic effective versions.
- Non-USA/non-Canada submissions are routed by country contacts.
- Certified and non-certified country behavior is implemented intentionally.
- Fallback handling exists for missing routing data.
- Submitters receive an appropriate confirmation or rejection email.
- Submissions record `ReceivedOnUtc` when stored and record `SentOnUtc` only after all required outbound emails are sent.
- Retryable or quota-related email failures leave submissions unsent and eligible for scheduled retry.
- A timer-triggered retry function runs every morning at 3:00 AM and processes unsent retryable submissions from the previous day or older backlog.
- Operators can maintain district and country contacts through a supported, version-aware workflow.
- District-level lookup/reporting is available with deterministic version selection.
- Monthly aggregate reporting is available with clearly defined, correct date-bucket semantics.
- `dev`, `test`, and `prod` environments can all be provisioned from repository-managed infrastructure definitions.
- The same release artifact can be promoted through `dev`, `test`, and `prod`.
- Automated tests and smoke validations are part of the pipeline release gates.
- Secrets, observability, deployment, and access control are defined for production use.
- The rewrite documents which legacy quirks were preserved intentionally and which were corrected.

## 20. Mandated Rewrite Decisions

The following design decisions are mandatory for the rewrite unless this specification is amended:

- Introduce explicit request/response DTOs with mostly nullable form fields, validation, and meaningful `400` responses for true validation failures.
- Split admin endpoints from public submission endpoints with stronger authentication/authorization.
- Move from append-only contact uploads to explicit versioned contact management with effective dates or equivalent active-version semantics.
- Store explicit timestamps instead of relying on Cosmos `_ts`.
- Use controlled dropdown country values for new submissions, keep any legacy country alias normalization only as a compatibility adapter, and make district/zipcode normalization rules explicit and testable.
- Use a true aggregate query or equivalently correct reporting mechanism for monthly reporting.
- Replace string booleans with actual booleans in the canonical domain model while preserving backward-compatible request handling.
- Define and enforce retention policies for raw request logs and submissions containing PII.
- Persist structured email-delivery state, including `ReceivedOnUtc`, `SentOnUtc`, delivery status, and attempt history.
- Treat max-emails-per-day or quota-exceeded provider responses as retryable failures.
- Add a 3:00 AM scheduled retry function for unsent submissions.
- Provision Cosmos DB resources and required environment configuration as first-class infrastructure.
- Support `dev`, `test`, and `prod` deployment environments with artifact promotion through the pipeline.
- Remove or formally replace dead config such as plaintext `appsettings.json` secrets.

## 21. Source-of-Truth Mapping

This specification was derived from the current repository implementation, primarily:

- `api/CreateInterestFormEntry.cs`
- `api/CreateContactsForDistricts.cs`
- `api/CreateContactsForCountries.cs`
- `api/GetContactsForDistrict.cs`
- `api/GetSubmissionsByMonth.cs`
- `api/GetSubmissionsForDistrictByYear.cs`
- `api/TestZipCodes.cs`
- `api/Shared/Models/*`
- `api/Shared/Services/*`
- `infra/*.bicep`
- `.azdo/pipelines/*.yml`

If implementation and this document diverge during the rewrite, the target-state requirements in sections 15 through 20 take precedence over purely descriptive legacy behavior notes, and the document should be updated accordingly.
