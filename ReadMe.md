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

The infrastructure deployment treats the Cosmos account, database, and containers as existing shared resources. Production uses `ContactInfoAndRequestsByType`, while test uses `TestContactInfoAndRequestsByType`. Both containers must use `/Type` as the partition key. The deployment generates the `databaseConnectionString` Key Vault secret directly from the configured Cosmos account; do not create or maintain that secret manually.

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
- `GET /api/interest-forms-sent-per-district-per-month`: admin markdown report with monthly columns from August 2026 through the current month by successful delivery time (`SentOnUtc`), grouped by country and district; unsent records, records sent before August 2026, testing districts `123` and `321`, and USA submissions with ZIP code `321` are excluded, requires `x-admin-api-key`.
- `GET /api/health`: anonymous health probe.

## Delivery and Retry

Submissions are persisted with `SentOnUtc=null` and `EmailDeliveryStatus=Pending` before outbound email starts. The complete routed submission is saved again immediately before the provider call. Only confirmed delivery sets `SentOnUtc` and changes `EmailDeliveryStatus` to `Sent`. Retryable SMTP/provider failures and quota failures change the status to `RetryPending`; terminal failures retain `TerminalFailed` for diagnosis. Both failure statuses set `NextEmailAttemptOnUtc` and remain eligible for the scheduled retry function so delivery can recover after configuration or recipient data is corrected.

The retry trigger runs once per day at `08:00` UTC. The configured `emailRetryTimeZone` is still used by the retry service to calculate local-day retry windows. Each run has a budget of 250 Gmail recipient quota units, reserving half of the consumer Gmail daily recipient allowance for new submissions and operator notifications. Every distinct address on a retry message consumes one unit, so a message sent to two addresses consumes two units. The service stops before sending a message that would exceed the remaining retry budget, and it also stops immediately if Gmail reports a provider quota failure.

## Exception Alerts

Each deployed environment includes three separate Azure Monitor log search alerts: application exceptions/non-delivery error traces/failed dependencies, final email delivery failures, and unsuccessful HTTP requests. The alerts evaluate five-minute windows. Individual handled SMTP attempt failures are warnings and do not alert; once the last SMTP attempt in a delivery run has failed, the orchestrator writes an `[EmailDeliveryFinalFailure]` error. That error includes a structured `CosmosSubmissionId`, and the delivery alert uses it as an alert dimension so the notification identifies the record to resend. Submission and routing anomalies that do not prevent delivery are warnings. The failed-request rule still fires when `success == false` or the HTTP result code is at least 400. All three alerts use the same email-only action group, which notifies `DrakeLundstrom@gmail.com` and `studyabroadscholarshipswebsite@gmail.com` using Azure Monitor's common alert schema. Azure may send an action-group enrollment/confirmation notification when receivers are first deployed.

## District-Managed Email Templates

District-specific student and parent follow-up content lives under `Email/DistrictTemplates`, separate from the shared route templates. `DistrictEmailTemplateRegistry` selects a district template only when routing produces one unambiguous district and that template supports the submitter type. Unsupported submitter types, districts without a custom template, and multi-district matches continue through the generic email flow.

District 6600 owns its costs, wording, and links in `District6600EmailTemplate`. Its exchange season is calculated from the original submission timestamp: a submission received in 2026 displays August 2027 through June/July 2028. To add another district, create an `IDistrictEmailTemplate` implementation in the same folder and register it with dependency injection in `Program.cs`; shared recipient selection, delivery, and retry behavior do not need to change.

## Email Examples

The examples below show representative HTML email output rendered by `EmailTemplateService` for every routing variation, the Rotarian-specific variation, and the District 6600 managed variation. Each body is shown as literal HTML source followed by a rendered preview of that same HTML. Blank or null form fields are omitted from the generic information block, including the optional `Question` line. Examples with and without a submitted question are included below. Each submission produces one email: all applicable representatives, submitters, students, parents, and support recipients are included together on that email's `To` line so the submitter can use **“Reply all”**. Top-level body elements are split onto separate lines here for readability.

### Student routed to one district (with a question)

To: district6630@example.org, jordan@example.com, parent@example.com

Subject: Rotary Youth Exchange interest from Jordan Rivera

#### HTML source

```html
<p>Hello Jordan Rivera,</p>
<p><strong><u>For the submitting student:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>Your local Rotary Youth Exchange representatives from District 6630 have been added to this email. They should reply within 2 weeks with information about how the program works in your area.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Student<br><strong>Name:</strong> Jordan Rivera<br><strong>Current age (years):</strong> 16<br><strong>What high school do or will you attend?:</strong> Cleveland High School<br><strong>Student's email:</strong> jordan@example.com<br><strong>Student's phone number:</strong> 555-0100<br><strong>Parent's email:</strong> parent@example.com<br><strong>Parent's phone number:</strong> 555-0101<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Cleveland<br><strong>Zip code or first 3 of CDN postal code:</strong> 44102<br><strong>Question:</strong> Can I choose a country?</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hello Jordan Rivera,</p>
<p><strong><u>For the submitting student:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>Your local Rotary Youth Exchange representatives from District 6630 have been added to this email. They should reply within 2 weeks with information about how the program works in your area.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Student<br><strong>Name:</strong> Jordan Rivera<br><strong>Current age (years):</strong> 16<br><strong>What high school do or will you attend?:</strong> Cleveland High School<br><strong>Student's email:</strong> jordan@example.com<br><strong>Student's phone number:</strong> 555-0100<br><strong>Parent's email:</strong> parent@example.com<br><strong>Parent's phone number:</strong> 555-0101<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Cleveland<br><strong>Zip code or first 3 of CDN postal code:</strong> 44102<br><strong>Question:</strong> Can I choose a country?</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
<!-- email-example-rendered:end -->

### District 6600 managed student follow-up

To: district6600@example.org, jordan@example.com, parent@example.com

Subject: Rotary Youth Exchange interest from Jordan Rivera

#### HTML source

```html
<p>Hi Jordan Rivera,</p>
<p><strong><u>For the District 6600 Rotary representative:</u></strong></p>
<p>For reference, here is the information submitted:</p>
<p><strong>Who are you?:</strong> Student<br><strong>Name:</strong> Jordan Rivera<br><strong>Current age (years):</strong> 16<br><strong>What high school do or will you attend?:</strong> Sandusky High School<br><strong>Student's email:</strong> jordan@example.com<br><strong>Student's phone number:</strong> 555-0100<br><strong>Parent's email:</strong> parent@example.com<br><strong>Parent's phone number:</strong> 555-0101<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Sandusky<br><strong>Zip code or first 3 of CDN postal code:</strong> 44870<br><strong>Question:</strong> Can I choose a country?</p>
<p><strong><u>For the submitting student and family:</u></strong></p>
<p>Thank you for your interest in <strong>The Study Abroad Scholarship Program</strong> provided through <strong>Rotary Youth Exchange!</strong></p>
<p>This is much more than a study abroad program—it&rsquo;s a life-changing opportunity to gain independence, immerse yourself in a new culture, learn a language, build lifelong friendships, and stand out on college and scholarship applications.</p>
<p><strong>Choose the experience that&rsquo;s right for you:</strong></p>
<p><strong>Long-Term Exchange (9–11 Months) – The Study Abroad Scholarship</strong></p>
<ul><li>Attend high school abroad from <strong>August 2027–June/July 2028</strong></li><li>Live with carefully selected host families</li><li><strong>Scholarship value: approximately $25,000</strong><br>Covers tuition and school fees, room and board, a monthly stipend, pre-departure orientation and training, 24-hour worldwide emergency assistance, and more.</li><li><strong>Family investment: approximately $6,000–$8,000</strong><br>Includes airfare, insurance, visa costs, spending money, application fee, etc.</li></ul>
<p><strong>Short-Term Exchange (4–12 Weeks)</strong></p>
<ul><li>Participate in a family-to-family exchange during the summer</li><li>Spend 4–6 weeks overseas, then host your exchange sibling in the United States</li><li><strong>Family investment: approximately $2,500–$3,500</strong><br>Includes airfare, insurance, spending money, application fee, etc.</li></ul>
<p>Application fees are fully refunded if you are not accepted into the program.</p>
<p><strong><u>What do I need to do now?</u></strong></p>
<p>Talk to your parents and complete the <strong>first page of an application</strong> to show your interest.</p>
<p>This lets us know you are seriously considering the program. You can switch between the Long-Term (Study Abroad Scholarship) and Short-Term programs later, if needed.</p>
<p>Please select <strong>District 6600</strong> when applying.</p>
<p><strong>Long-Term Application:</strong><br><a href="https://yehub.net/OER-obapp">https://yehub.net/OER-obapp</a></p>
<p><strong>Short-Term Application:</strong><br><a href="https://yehub.net/OER-stapp">https://yehub.net/OER-stapp</a></p>
<p>You can also learn more about both programs, browse frequently asked questions, and register for upcoming information sessions at <a href="https://rotarydistrict6600.org/rye/">https://rotarydistrict6600.org/rye/</a>. You are welcome to attend an information session whether or not you have started an application.</p>
<p>If you have any questions, <strong>fill out the first page of the application</strong>, and we will then be happy to answer them.</p>
<p><strong>Welcome to Rotary Youth Exchange!</strong></p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hi Jordan Rivera,</p>
<p><strong><u>For the District 6600 Rotary representative:</u></strong></p>
<p>For reference, here is the information submitted:</p>
<p><strong>Who are you?:</strong> Student<br><strong>Name:</strong> Jordan Rivera<br><strong>Current age (years):</strong> 16<br><strong>What high school do or will you attend?:</strong> Sandusky High School<br><strong>Student's email:</strong> jordan@example.com<br><strong>Student's phone number:</strong> 555-0100<br><strong>Parent's email:</strong> parent@example.com<br><strong>Parent's phone number:</strong> 555-0101<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Sandusky<br><strong>Zip code or first 3 of CDN postal code:</strong> 44870<br><strong>Question:</strong> Can I choose a country?</p>
<p><strong><u>For the submitting student and family:</u></strong></p>
<p>Thank you for your interest in <strong>The Study Abroad Scholarship Program</strong> provided through <strong>Rotary Youth Exchange!</strong></p>
<p>This is much more than a study abroad program—it&rsquo;s a life-changing opportunity to gain independence, immerse yourself in a new culture, learn a language, build lifelong friendships, and stand out on college and scholarship applications.</p>
<p><strong>Choose the experience that&rsquo;s right for you:</strong></p>
<p><strong>Long-Term Exchange (9–11 Months) – The Study Abroad Scholarship</strong></p>
<ul><li>Attend high school abroad from <strong>August 2027–June/July 2028</strong></li><li>Live with carefully selected host families</li><li><strong>Scholarship value: approximately $25,000</strong><br>Covers tuition and school fees, room and board, a monthly stipend, pre-departure orientation and training, 24-hour worldwide emergency assistance, and more.</li><li><strong>Family investment: approximately $6,000–$8,000</strong><br>Includes airfare, insurance, visa costs, spending money, application fee, etc.</li></ul>
<p><strong>Short-Term Exchange (4–12 Weeks)</strong></p>
<ul><li>Participate in a family-to-family exchange during the summer</li><li>Spend 4–6 weeks overseas, then host your exchange sibling in the United States</li><li><strong>Family investment: approximately $2,500–$3,500</strong><br>Includes airfare, insurance, spending money, application fee, etc.</li></ul>
<p>Application fees are fully refunded if you are not accepted into the program.</p>
<p><strong><u>What do I need to do now?</u></strong></p>
<p>Talk to your parents and complete the <strong>first page of an application</strong> to show your interest.</p>
<p>This lets us know you are seriously considering the program. You can switch between the Long-Term (Study Abroad Scholarship) and Short-Term programs later, if needed.</p>
<p>Please select <strong>District 6600</strong> when applying.</p>
<p><strong>Long-Term Application:</strong><br><a href="https://yehub.net/OER-obapp">https://yehub.net/OER-obapp</a></p>
<p><strong>Short-Term Application:</strong><br><a href="https://yehub.net/OER-stapp">https://yehub.net/OER-stapp</a></p>
<p>You can also learn more about both programs, browse frequently asked questions, and register for upcoming information sessions at <a href="https://rotarydistrict6600.org/rye/">https://rotarydistrict6600.org/rye/</a>. You are welcome to attend an information session whether or not you have started an application.</p>
<p>If you have any questions, <strong>fill out the first page of the application</strong>, and we will then be happy to answer them.</p>
<p><strong>Welcome to Rotary Youth Exchange!</strong></p>
<!-- email-example-rendered:end -->

### Rotarian routed to one district (with a question)

To: district6630@example.org, morgan@example.com, support@example.test

Subject: Rotary Youth Exchange question from Morgan Chen

#### HTML source

```html
<p>Hello fellow Rotarian,</p>
<p><strong><u>For the submitting Rotarian:</u></strong></p>
<p>Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.</p>
<p>The Rotary Youth Exchange representatives from District 6630 and our support team have been added to this email.</p>
<p>To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.</p>
<p>They should reply within 2 weeks with guidance specific to your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Rotarian<br><strong>Name:</strong> Morgan Chen<br><strong>Contact email:</strong> morgan@example.com<br><strong>Contact phone number:</strong> 555-0110<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Cleveland<br><strong>Zip code or first 3 of CDN postal code:</strong> 44102<br><strong>Question:</strong> How can our club help a student apply?</p>
<p><strong><u>For the Rotary representatives and support team:</u></strong></p>
<p>This question was submitted by a fellow Rotarian. If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for participating in Rotary Youth Exchange and supporting the Study Abroad Scholarships through <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hello fellow Rotarian,</p>
<p><strong><u>For the submitting Rotarian:</u></strong></p>
<p>Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.</p>
<p>The Rotary Youth Exchange representatives from District 6630 and our support team have been added to this email.</p>
<p>To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.</p>
<p>They should reply within 2 weeks with guidance specific to your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Rotarian<br><strong>Name:</strong> Morgan Chen<br><strong>Contact email:</strong> morgan@example.com<br><strong>Contact phone number:</strong> 555-0110<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Ohio<br><strong>City:</strong> Cleveland<br><strong>Zip code or first 3 of CDN postal code:</strong> 44102<br><strong>Question:</strong> How can our club help a student apply?</p>
<p><strong><u>For the Rotary representatives and support team:</u></strong></p>
<p>This question was submitted by a fellow Rotarian. If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for participating in Rotary Youth Exchange and supporting the Study Abroad Scholarships through <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
<!-- email-example-rendered:end -->

### Parent submission on a district border (without a question)

To: district6630@example.org, district6650@example.org, student@example.com, parent@example.com, contact@example.com

Subject: Rotary Youth Exchange interest from Pat Nguyen

#### HTML source

```html
<p>Hello Pat Nguyen,</p>
<p><strong><u>For the submitting family:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>Your location matched multiple Rotary districts (District 6630 and District 6650), so representatives from each district have been added to this email.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>They should reply within 2 weeks with information about how the program works in your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Pat Nguyen<br><strong>Current age of your student (years):</strong> 15<br><strong>What high school does or will your student attend?:</strong> Erie High School<br><strong>Student's email:</strong> student@example.com<br><strong>Parent's email:</strong> parent@example.com<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Pennsylvania<br><strong>City:</strong> Erie<br><strong>Zip code or first 3 of CDN postal code:</strong> 16501</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hello Pat Nguyen,</p>
<p><strong><u>For the submitting family:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>Your location matched multiple Rotary districts (District 6630 and District 6650), so representatives from each district have been added to this email.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>They should reply within 2 weeks with information about how the program works in your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Pat Nguyen<br><strong>Current age of your student (years):</strong> 15<br><strong>What high school does or will your student attend?:</strong> Erie High School<br><strong>Student's email:</strong> student@example.com<br><strong>Parent's email:</strong> parent@example.com<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>State or province:</strong> Pennsylvania<br><strong>City:</strong> Erie<br><strong>Zip code or first 3 of CDN postal code:</strong> 16501</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your district, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
<!-- email-example-rendered:end -->

### Mexico country routing (with a question)

To: mexico-coordinator@example.org, contact@example.com

Subject: Rotary Youth Exchange interest from Alex Martinez

#### HTML source

```html
<p>Hello Alex Martinez,</p>
<p><strong><u>For the submitting family:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>The Rotary Youth Exchange representatives for Mexico have been added to this email.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>They should reply within 2 weeks with information about how the program works in your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Alex Martinez<br><strong>Current age of your student (years):</strong> 16<br><strong>Contact email:</strong> contact@example.com<br><strong>Contact phone number:</strong> 555-0102<br><strong>Country of residence:</strong> Mexico<br><strong>City:</strong> Monterrey<br><strong>Question:</strong> Is there an application deadline?</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your country, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hello Alex Martinez,</p>
<p><strong><u>For the submitting family:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>The Rotary Youth Exchange representatives for Mexico have been added to this email.</p>
<p>To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.</p>
<p>They should reply within 2 weeks with information about how the program works in your area.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Parent<br><strong>Name:</strong> Alex Martinez<br><strong>Current age of your student (years):</strong> 16<br><strong>Contact email:</strong> contact@example.com<br><strong>Contact phone number:</strong> 555-0102<br><strong>Country of residence:</strong> Mexico<br><strong>City:</strong> Monterrey<br><strong>Question:</strong> Is there an application deadline?</p>
<p><strong><u>For the Rotary representative:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for your country, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
<!-- email-example-rendered:end -->

### Manual routing review (without a question)

To: operator@example.test, contact@example.com, support@example.test

Subject: Rotary Youth Exchange interest needs routing review

#### HTML source

```html
<p>Hello Taylor Brooks,</p>
<p><strong><u>For the submitter:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin team has been added to this email to review your request.</p>
<p>The admin team should reply within 2 weeks with information about the next steps.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Other<br><strong>Name:</strong> Taylor Brooks<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>City:</strong> Unknown City</p>
<p>Routing notes: Zipcode missing for district routing</p>
<p><strong><u>For the Rotary admin team:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for this submission, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
```

#### Rendered body

<!-- email-example-rendered:start -->
<p>Hello Taylor Brooks,</p>
<p><strong><u>For the submitter:</u></strong></p>
<p>Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.</p>
<p>We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin team has been added to this email to review your request.</p>
<p>The admin team should reply within 2 weeks with information about the next steps.</p>
<p>For reference, here is the information you submitted:</p>
<p><strong>Who are you?:</strong> Other<br><strong>Name:</strong> Taylor Brooks<br><strong>Contact email:</strong> contact@example.com<br><strong>Country of residence:</strong> USA<br><strong>City:</strong> Unknown City</p>
<p>Routing notes: Zipcode missing for district routing</p>
<p><strong><u>For the Rotary admin team:</u></strong></p>
<p>If you have any admin support questions, need advice about the process, need to add or remove email addresses for this submission, or want a list of previous submissions, please contact <a href="mailto:operator@example.test">operator@example.test</a>.</p>
<p>Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at <a href="https://studyabroadscholarships.org/">studyabroadscholarships.org</a>!</p>
<!-- email-example-rendered:end -->

For every `Rotarian` submission, `supportEmail` is included on the outgoing message and the subject, introduction, and closing use Rotarian-specific wording. `Other` submissions also copy `supportEmail`; student and parent submissions do not.

## Security and Retention

Production and test use isolated Function Apps, Storage accounts, Key Vaults, app settings, and Cosmos containers. Both environments use the existing Cosmos account `studyabroadscholarshipsdb` and `EmailForwarding` database in resource group `EmailForwardingApi`; production uses `ContactInfoAndRequestsByType`, while test uses `TestContactInfoAndRequestsByType`. Non-production email is routed to the `nonProductionSafeRecipient` Key Vault secret unless unsafe delivery is explicitly enabled.

Rotate Key Vault secrets per environment by creating a new secret version and restarting the Function App after validation. PII-bearing submissions and raw request logs should be retained only as long as the operating program requires; align Cosmos retention or scheduled purge jobs with the organization retention policy before production launch.
