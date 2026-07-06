# Email Forwarding Application Rewrite Tickets

This file breaks [SYSTEM_REWRITE_SPEC.md](./SYSTEM_REWRITE_SPEC.md) into ordered implementation tickets. The tickets are designed to be handed to an AI coding agent one at a time, in order.

## How To Use These Tickets

Before starting any ticket, the agent must:

- Read [SYSTEM_REWRITE_SPEC.md](./SYSTEM_REWRITE_SPEC.md) and this ticket file.
- Inspect the current code before editing.
- Preserve user work and unrelated changes.
- Keep the application buildable at the end of the ticket.
- Add or update tests for the behavior changed by the ticket.
- Run the most relevant validation commands available in the repo.
- Update this ticket file only if scope changes are discovered and intentionally approved.

Each ticket is complete only when:

- All acceptance criteria in that ticket pass.
- Existing behavior covered by prior tickets still works.
- No plaintext secrets are added.
- Null and missing form fields do not cause unhandled exceptions.
- The implementation remains aligned with [SYSTEM_REWRITE_SPEC.md](./SYSTEM_REWRITE_SPEC.md).

## Ticket 01 - Establish Testable Architecture Foundation

Goal:

- Create a clean, testable project structure for the rewrite while keeping the Azure Functions app buildable.

Scope:

- Add test project(s) to `EmailForwardingApplication.sln`.
- Introduce folders/namespaces for domain models, DTOs, storage, email, routing, reporting, configuration, and function entry points.
- Add dependency-injection-friendly services so business logic can be tested outside Azure Function triggers.
- Add a central clock abstraction so `ReceivedOnUtc`, `SentOnUtc`, reporting windows, and retry selection are testable.

Deliverables:

- Test project added to the solution.
- A shared application/service layer callable from Azure Function classes.
- A documented local test command in `ReadMe.md`.
- At least one passing placeholder or smoke unit test proving the test project is wired correctly.

Acceptance Criteria:

- `dotnet build EmailForwardingApplication.sln` succeeds.
- `dotnet test EmailForwardingApplication.sln` succeeds.
- Azure Function trigger classes remain thin wrappers over testable services.
- No live secrets are required to run unit tests.

## Ticket 02 - Configuration And Secret Management

Goal:

- Replace hardcoded and conflicting configuration with one authoritative, environment-aware configuration model.

Scope:

- Define strongly typed configuration for Cosmos, SMTP, app environment, retry timezone, public/admin auth configuration, and feature/debug flags.
- Stop treating `api/appsettings.json` as a live secret source.
- Add or update sample local configuration documentation without committing real secret values.
- Ensure SMTP host, port, security mode, username, and password are configurable.

Deliverables:

- Strongly typed config classes or equivalent validated configuration service.
- `local.settings.json` guidance or sample file that contains placeholders only.
- `ReadMe.md` updated with local configuration setup.

Acceptance Criteria:

- App startup/config validation reports missing required runtime settings clearly.
- No plaintext production-like secrets remain in checked-in config files touched by this ticket.
- Unit tests can construct services with fake configuration.
- SMTP provider details are not hardcoded in business logic.

## Ticket 03 - Nullable Submission DTO And Canonical Model

Goal:

- Implement the mostly nullable submission request/storage model required by the spec.

Scope:

- Make public form fields nullable in request DTOs and stored submission model.
- Add server-generated metadata: `id`, `Type`, `ReceivedOnUtc`, `SentOnUtc`, `EmailDeliveryStatus`, `EmailDeliveryAttempts`, `NextEmailAttemptOnUtc`, and `Errors`.
- Support unknown/new form fields by preserving them in `AdditionalFields` or safely ignoring them with a documented decision.
- Ensure removed legacy fields do not break deserialization, persistence, routing, reporting, retry, or email rendering.
- Preserve legacy request shape compatibility.

Deliverables:

- Updated submission models/DTOs.
- Null-safe mapping from request DTO to stored submission.
- Tests for full legacy payload, sparse payload, null fields, missing fields, and unknown fields.

Acceptance Criteria:

- A sparse payload with only `Name`, `Email`, `CountryOfResidence`, and an unknown field can deserialize and map without throwing.
- A payload with null legacy fields can deserialize and map without throwing.
- `ReceivedOnUtc` is set exactly once during initial persistence.
- `SentOnUtc` is null on initial persistence.
- `EmailDeliveryStatus` starts as `Pending`.
- Server-generated metadata is non-null after mapping.

## Ticket 04 - Storage Abstraction And Cosmos Repository

Goal:

- Centralize all Cosmos access and make persistence behavior explicit and testable.

Scope:

- Introduce repository abstractions for submissions, raw request logs, district contacts, country contacts, and reporting queries.
- Store raw request body before submission processing when possible.
- Store normalized submissions before email sending.
- Implement update methods for errors and email delivery state.
- Add optimistic concurrency or equivalent support needed by retry/idempotency.

Deliverables:

- Cosmos repository implementation.
- In-memory or fake repository for tests.
- Query methods needed by later routing, reporting, and retry tickets.

Acceptance Criteria:

- Raw request logging is a distinct persisted entity or equivalent audit record.
- Submission insert stores `ReceivedOnUtc` and `SentOnUtc=null`.
- Delivery-state updates can set `SentOnUtc` only through an explicit success path.
- Repository tests cover insert, update, query by id, query unsent, and concurrency behavior.
- No function class constructs ad hoc Cosmos query clients for business logic after this ticket.

## Ticket 05 - Contact Versioning Model

Goal:

- Replace accidental append-only contact behavior with deterministic effective contact versions.

Scope:

- Add explicit version/effective metadata for district and country contacts.
- Define active/effective selection logic using `Version`, `EffectiveFromUtc`, and either `EffectiveToUtc` or `IsActive`.
- Preserve historical versions for audit.
- Ensure routing and lookup use only deterministic effective records.

Deliverables:

- Updated contact models.
- Contact repository methods for create/update effective versions and retrieve active versions.
- Tests for active version selection, historical version exclusion, and deterministic ordering.

Acceptance Criteria:

- A newer inactive or future-effective record does not affect current routing.
- Historical zipcode membership does not affect live district routing unless that historical version is explicitly effective.
- Country routing selects exactly one active effective country record.
- District lookup selects active effective district records deterministically.

## Ticket 06 - Contact Admin Endpoints

Goal:

- Implement supported workflows for maintaining district and country contacts.

Scope:

- Update `POST /api/contacts-for-districts`.
- Update `POST /api/contacts-for-countries`.
- Update `GET /api/contacts-for-districts/{id}`.
- Validate contact payloads and reject malformed or duplicate entries with structured errors.
- Return a single object or `404` from district lookup, not a collection wrapper.

Deliverables:

- Updated function endpoints.
- Admin/internal authorization hook or placeholder integration from Ticket 13 if not complete yet.
- Unit/API tests for success, validation failure, duplicate handling, version creation, and not found.

Acceptance Criteria:

- Bulk district contact upload creates deterministic effective versions.
- Bulk country contact upload creates deterministic effective versions with certification metadata.
- Malformed contacts return structured HTTP `400`.
- `GET /api/contacts-for-districts/{id}` returns one effective district contact or `404`.
- All contact endpoints use admin/internal auth, not the public submission credential.

## Ticket 07 - Routing And Normalization Services

Goal:

- Implement routing decisions as explicit, testable domain services.

Scope:

- Implement zipcode/postal-prefix normalization for USA and Canada.
- Treat country as a canonical dropdown value for new submissions.
- Keep legacy country alias normalization only as a compatibility/migration adapter.
- Handle missing country or zipcode as routing errors, not exceptions.
- Implement fallback routing when district or country is missing.

Deliverables:

- Routing service with explicit result types.
- Tests for USA, Canada, non-USA/Canada, missing country, missing zipcode, unknown country, uncertified country, multiple district matches, and no district matches.

Acceptance Criteria:

- USA zipcode `44102-1234` routes using `44102`.
- Canada postal code `M5V 2T6` routes using `M5V`.
- Missing zipcode for USA/Canada produces `Zipcode missing for district routing` and fallback behavior.
- Missing country produces `Country of residence missing` and fallback behavior.
- Free-text country validation is not required for new dropdown values.
- Non-certified country returns the rejection path and is not treated as a system error.

## Ticket 08 - Null-Safe Email Template Rendering

Goal:

- Make every outbound email body and subject render safely from nullable submissions.

Scope:

- Centralize email body/subject generation.
- Implement district representative, country representative, submitter confirmation, submitter rejection, operator fallback, and failure/operator emails.
- Render missing student fields as blank, `Unknown`, or a documented placeholder.
- Include raw serialized submission JSON in operator fallback emails.
- Correct rejection wording to refer to country/program certification, not district certification.

Deliverables:

- Email template service.
- Snapshot/string tests for every email type.
- Null-field rendering tests.

Acceptance Criteria:

- No email template calls string methods on nullable values without null handling.
- Every required email includes the student-information block.
- Operator fallback email includes raw serialized submission JSON.
- Rejection email wording matches country/program certification semantics.
- Missing submitter email causes submitter-facing email to be skipped and recorded, not thrown.

## Ticket 09 - Email Sending And Error Classification

Goal:

- Build a provider-agnostic email sending service that classifies SMTP/provider failures for durable retry.

Scope:

- Use configured SMTP host, port, security mode, username, and password.
- Return structured send results instead of throwing raw provider exceptions through business logic.
- Classify provider failures as `Retryable`, `QuotaExceeded`, or `Terminal`.
- Treat max-emails-per-day, quota-exceeded, and equivalent rate-limit responses as retryable quota failures.

Deliverables:

- Email sender abstraction and implementation.
- Fake email sender for tests.
- Error classification tests using representative SMTP/provider exceptions or simulated responses.

Acceptance Criteria:

- Successful sends return message-level success with recipient/message metadata.
- Daily send-limit or quota-exceeded responses produce `QuotaExceeded`.
- Transient network/provider failures produce `Retryable`.
- Bad credentials or permanent sender configuration problems produce `Terminal`.
- Business logic can record provider response/code when available.

## Ticket 10 - Durable Email Delivery Plan And Idempotency

Goal:

- Prevent lost or duplicate emails by tracking outbound message state per submission.

Scope:

- Define an outbound email plan for each submission path.
- Track message type, recipient group, recipient list, attempt timestamp, outcome, provider response/code, and retry classification in `EmailDeliveryAttempts`.
- Ensure partial success is either tracked per message or otherwise made idempotent.
- Ensure retry sends only messages not already marked successful.

Deliverables:

- Delivery orchestration service.
- Message-level delivery state model.
- Tests for all success, partial success, retryable failure, quota failure, and terminal failure cases.

Acceptance Criteria:

- `SentOnUtc` is set only when all required messages for the submission are successful.
- Partial success does not set `SentOnUtc`.
- Retry after partial success does not resend already successful messages.
- Retryable failures set `EmailDeliveryStatus=RetryPending`.
- Terminal failures set `EmailDeliveryStatus=TerminalFailed`.

## Ticket 11 - Public Submission Endpoint

Goal:

- Rebuild `POST /api/interest-form-entry` around durable persistence, routing, and delivery state.

Scope:

- Read and store raw request body before model processing when possible.
- Deserialize nullable request DTO.
- Validate only structural/safety requirements and truly action-required fields.
- Insert submission before email sending.
- Route by district/country/fallback/rejection path.
- Send emails through delivery orchestration.
- Return correct HTTP status and structured body.

Deliverables:

- Updated public submission function.
- Tests for success, validation failure, fallback, retry-pending, terminal failure, and catastrophic failure behavior.

Acceptance Criteria:

- Valid fully populated submission returns HTTP `200` after successful delivery and includes `ReceivedOnUtc`, `SentOnUtc`, and `EmailDeliveryStatus=Sent`.
- Valid stored submission with quota failure returns HTTP `202`, `SentOnUtc=null`, and `EmailDeliveryStatus=RetryPending`.
- Missing/null optional fields do not cause HTTP `500`.
- Missing country or zipcode stores the submission and records structured routing errors.
- Early raw-log or deserialization failures return HTTP `500` with a correlation identifier.
- No submission is sent before it is persisted.

## Ticket 12 - Scheduled Unsent Submission Retry Function

Goal:

- Add the 3:00 AM retry function for unsent submissions.

Scope:

- Implement timer-triggered Azure Function.
- Run every morning at 3:00 AM in the configured business timezone, currently `America/New_York`.
- Query previous local calendar day submissions where `SentOnUtc=null` and `EmailDeliveryStatus` is `Pending` or `RetryPending`.
- Include older retryable backlog so repeated quota failures do not strand records.
- Exclude `TerminalFailed` unless explicitly reset by an operator.
- Use lease/optimistic concurrency to avoid duplicate concurrent sends.
- Stop or throttle the batch on quota exhaustion.

Deliverables:

- Timer function.
- Retry service.
- Tests for date-window selection, backlog inclusion, terminal exclusion, quota stop/throttle, success update, and concurrency guard.

Acceptance Criteria:

- Previous-day unsent retryable submissions are retried.
- Older retryable unsent submissions are retried.
- Successful retry sets `SentOnUtc` and `EmailDeliveryStatus=Sent`.
- Quota exhaustion keeps affected submissions unsent and eligible for next retry.
- Already successful messages are not resent.
- The schedule timezone behavior is documented.

## Ticket 13 - Authentication And Authorization Separation

Goal:

- Separate public submission access from admin/reporting/debug access.

Scope:

- Define public submission credential behavior.
- Define admin/internal auth behavior for contact, reporting, and debug endpoints.
- Disable or strongly protect debug-only endpoints in production.
- Ensure dev/test/prod have separate credentials.

Deliverables:

- Authorization service or middleware/helper.
- Updated functions using correct authorization path.
- Tests for public endpoint access, admin endpoint denial with public credential, admin endpoint success with admin credential, and production debug endpoint blocking.

Acceptance Criteria:

- Public submission endpoint does not accept admin-only assumptions.
- Admin/reporting endpoints do not share the public submission credential.
- `GET /api/TestZipCodes/{id}` is disabled in production unless explicitly configured internal-only.
- Authorization failures return structured `401` or `403`.

## Ticket 14 - Reporting And Lookup Endpoints

Goal:

- Correct reporting and lookup semantics.

Scope:

- Update `GET /api/generate-submissions-by-month`.
- Update `GET /api/submissions/district/{districtName}`.
- Keep or retire `GET /api/TestZipCodes/{id}` according to production debug policy.
- Use explicit timestamps instead of Cosmos `_ts`.
- Use true aggregate counting or an equivalently correct reporting mechanism.

Deliverables:

- Reporting services.
- Updated function endpoints.
- Tests for same-month reports, final-month inclusion, large/page-sized result correctness, district version selection, and errored-submission inclusion policy.

Acceptance Criteria:

- Same-month start/end returns one monthly bucket.
- Requested final month is included.
- Counts are not dependent on Cosmos page size.
- District submission lookup uses newest effective district version deterministically.
- Any inclusion/exclusion of errored submissions is explicit and tested.

## Ticket 15 - Observability And Safe Logging

Goal:

- Make logging structured, safe, and useful for operations without becoming the source of truth for delivery state.

Scope:

- Replace null-unsafe exception logging.
- Add correlation IDs across request log, submission, delivery attempts, and responses.
- Log structured events for routing failures, provider failures, retry runs, and terminal failures.
- Ensure logs never replace database delivery state.

Deliverables:

- Safe logging service.
- Correlation ID handling.
- Tests for logging exception paths where `InnerException` is null.

Acceptance Criteria:

- Logging cannot throw a secondary exception while handling the original exception.
- Every public submission response includes or references a correlation identifier.
- Failed routing/email attempts can be correlated to a stored submission.
- Sensitive values such as SMTP passwords and connection strings are not logged.

## Ticket 16 - Unit Test Coverage Pass

Goal:

- Bring the core domain and service tests up to the required coverage before integration work.

Scope:

- Add or complete unit tests for nullable DTOs, routing, contact versioning, email rendering, email classification, delivery state, retry selection, reporting bucket logic, and authorization helpers.
- Use fake repositories and fake email sender.

Deliverables:

- Comprehensive unit test suite.
- Any small testability refactors needed to remove Azure/Cosmos/SMTP dependencies from unit tests.

Acceptance Criteria:

- `dotnet test EmailForwardingApplication.sln` passes locally without Azure, Cosmos, or SMTP access.
- Tests cover max-emails-per-day/quota classification.
- Tests cover missing/null fields and removed legacy fields.
- Tests cover unknown/new payload fields.
- Tests cover controlled-dropdown country behavior and missing-country fallback.

## Ticket 17 - Integration Test Harness

Goal:

- Add integration tests that exercise the deployed-style function stack with safe test dependencies.

Scope:

- Create integration test setup using safe non-production resources, Cosmos emulator strategy, or repository-level test doubles where Azure resources are not available.
- Use a mail sink or fake SMTP server for delivery tests.
- Seed representative district/country/contact/submission data.
- Mark test submissions as test traffic.

Deliverables:

- Integration test project/configuration.
- Test data builders/seeders.
- Documentation for running integration tests locally and in pipeline.

Acceptance Criteria:

- Integration tests cover public submission success, quota retry-pending, scheduled retry success, contact upload, monthly reporting, and district reporting.
- Tests use synthetic data only.
- Tests do not email real users or representatives.
- Tests do not require production secrets.

## Ticket 18 - Infrastructure For Dev/Test/Prod

Goal:

- Provision all required Azure resources per environment from repo-managed infrastructure.

Scope:

- Update Bicep to provision Function App, hosting plan, Storage account, Application Insights, Key Vault, Cosmos account/database/container(s), managed identity, app settings, and Key Vault references.
- Add `infra/params/parameters.test.json`.
- Ensure `dev`, `test`, and `prod` are isolated.
- Add `emailRetryTimeZone` and all app settings required by the spec.
- Resolve prod function app name mismatch.

Deliverables:

- Updated `infra/*.bicep`.
- Updated `infra/params/parameters.dev.json`.
- New `infra/params/parameters.test.json`.
- Updated `infra/params/parameters.prod.json`.
- Infrastructure documentation.

Acceptance Criteria:

- Each environment has distinct resource names and settings.
- Cosmos resources are provisioned by infrastructure.
- Function App identity can read required Key Vault secrets.
- Production deployment target name matches production infrastructure parameter name exactly.
- Non-production settings use safe mail/test configuration placeholders.

## Ticket 19 - Azure DevOps Pipeline Rewrite

Goal:

- Implement CI/CD with validation and controlled promotion through `dev`, `test`, and `prod`.

Scope:

- Update `.azdo/pipelines` so primary validation/release no longer relies on `trigger: none`.
- Add pull request validation: restore, build, static analysis/lint where available, unit tests.
- Build release artifact once.
- Deploy/validate infrastructure for `dev`, `test`, and `prod`.
- Promote the same artifact through `dev`, `test`, and `prod`.
- Run smoke tests in dev/prod and full integration tests in test.
- Add approval gate before production.

Deliverables:

- Updated pipeline YAML templates and main pipeline.
- Pipeline documentation in `ReadMe.md` or a docs section.

Acceptance Criteria:

- Pipeline has distinct `dev`, `test`, and `prod` environments.
- Same artifact is promoted across environments.
- Test failures block promotion.
- Production requires approval.
- App and infrastructure deployment use the same canonical environment names.

## Ticket 20 - Security, Privacy, And Retention

Goal:

- Close the security/compliance requirements in the spec.

Scope:

- Remove or sanitize checked-in plaintext secrets.
- Document secret rotation and environment-specific secret population.
- Define retention policy for raw request logs, submissions, and PII-bearing telemetry.
- Ensure admin/reporting access is documented.
- Ensure non-production email cannot accidentally reach live users/representatives.

Deliverables:

- Updated docs.
- Config or code safeguards for non-production mail delivery.
- Secret-free checked-in config.

Acceptance Criteria:

- No live-looking SMTP password, Cosmos connection string, or production secret remains in source-controlled config.
- Retention rules are documented.
- Non-production mail delivery requires explicit safe recipients or mail sink configuration.
- Admin/reporting caller requirements are documented.

## Ticket 21 - Backward Compatibility And Migration Readiness

Goal:

- Ensure the rewrite can coexist with historical data and legacy callers while supporting the new model.

Scope:

- Support legacy payload shape.
- Support canonical model with nullable fields and new delivery metadata.
- Handle historical records that lack `ReceivedOnUtc`, `SentOnUtc`, version metadata, or delivery attempts.
- Define migration/backfill strategy for explicit timestamps and contact versions.

Deliverables:

- Compatibility adapters or fallback readers.
- Migration/backfill documentation or scripts if needed.
- Tests with representative legacy documents.

Acceptance Criteria:

- Legacy submissions can still be read for reporting without throwing.
- Legacy contact records can be interpreted or migrated into deterministic effective versions.
- Missing new metadata on historical records is handled safely.
- New writes always use the new schema.

## Ticket 22 - End-To-End Acceptance Pass

Goal:

- Verify the implementation fully satisfies [SYSTEM_REWRITE_SPEC.md](./SYSTEM_REWRITE_SPEC.md).

Scope:

- Run complete local validation.
- Run integration tests against the intended `test` setup.
- Manually or automatically verify the most important user journeys.
- Fix any remaining spec gaps.
- Update docs to match the final implementation.

Deliverables:

- Final test results documented in the PR/commit notes.
- Any missing docs or small fixes.
- A checklist mapping spec acceptance criteria to implemented behavior.

Acceptance Criteria:

- Public form submission succeeds and records `ReceivedOnUtc` and `SentOnUtc`.
- Quota failure stores the submission, leaves `SentOnUtc=null`, returns retry-pending behavior, and is retried by the scheduled function.
- Sparse/null submission payloads are stored without unhandled exceptions.
- District, country, uncertified-country, missing-routing-data, and fallback paths all behave as specified.
- Contact management uses deterministic effective versions.
- Monthly reporting includes same-month and final-month buckets correctly.
- Dev/test/prod infrastructure and pipeline definitions are present and consistent.
- Unit and integration tests pass.
- `dotnet build EmailForwardingApplication.sln` passes.
- `dotnet test EmailForwardingApplication.sln` passes.
