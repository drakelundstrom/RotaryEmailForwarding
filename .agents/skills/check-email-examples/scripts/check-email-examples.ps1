param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
)

$ErrorActionPreference = "Stop"

$readmePath = Join-Path $RepoRoot "ReadMe.md"
if (-not (Test-Path $readmePath)) {
    throw "ReadMe.md was not found under $RepoRoot"
}

$readme = Get-Content -Raw $readmePath
$stalePatterns = @(
    'approximate plain-text',
    'Specific questions:',
    '```text\r?\nTo:'
)

foreach ($pattern in $stalePatterns) {
    if ($readme -match $pattern) {
        throw "README email examples contain stale wording matching '$pattern'."
    }
}

$emailSectionMatch = [regex]::Match(
    $readme,
    '(?ms)^## Email Examples\r?\n(?<section>.*?)(?=^## |\z)'
)
if (-not $emailSectionMatch.Success) {
    throw "README Email Examples section was not found."
}

$emailSection = $emailSectionMatch.Groups['section'].Value
$sourceMatches = [regex]::Matches(
    $emailSection,
    '(?ms)#### HTML source\r?\n\r?\n```html\r?\n(?<body>.*?)\r?\n```'
)
$renderedMatches = [regex]::Matches(
    $emailSection,
    '(?ms)#### Rendered body\r?\n\r?\n<!-- email-example-rendered:start -->\r?\n(?<body>.*?)\r?\n<!-- email-example-rendered:end -->'
)

if ($sourceMatches.Count -eq 0 -or $sourceMatches.Count -ne $renderedMatches.Count) {
    throw "Every README email example must include paired HTML source and rendered body views. Found $($sourceMatches.Count) source blocks and $($renderedMatches.Count) rendered blocks."
}

for ($index = 0; $index -lt $sourceMatches.Count; $index++) {
    $sourceBody = $sourceMatches[$index].Groups['body'].Value -replace "`r`n", "`n"
    $renderedBody = $renderedMatches[$index].Groups['body'].Value -replace "`r`n", "`n"
    if ($sourceBody -cne $renderedBody) {
        throw "README email example $($index + 1) has different HTML source and rendered body content."
    }
}

$requiredSnippets = @(
    'The examples below show representative HTML email output rendered by `EmailTemplateService`',
    '<p>Hello Jordan Rivera,</p>',
    '<p>Hello fellow Rotarian,</p>',
    '<p>Hello Pat Nguyen,</p>',
    '<p>Hello Alex Martinez,</p>',
    '<p>Hello Taylor Brooks,</p>',
    'reply all to ask your questions',
    'reply within 2 weeks with information about how the program works in your area',
    'Subject: Rotary Youth Exchange question from Morgan Chen',
    'support@example.test',
    '<p><strong><u>For the submitting student:</u></strong></p>',
    '<p><strong><u>For the submitting Rotarian:</u></strong></p>',
    '<p><strong><u>For the Rotary representative:</u></strong></p>',
    '<p><strong><u>For the Rotary representatives and support team:</u></strong></p>',
    'Study Abroad Scholarships offered as part of Rotary Youth Exchange',
    'Thank you for participating in Rotary Youth Exchange',
    'This question was submitted by a fellow Rotarian.',
    '<strong>Question:</strong> How can our club help a student apply?',
    'supporting the Study Abroad Scholarships',
    '<strong>Question:</strong> Can I choose a country?',
    '<strong>Question:</strong> Is there an application deadline?',
    '<p>Routing notes: Zipcode missing for district routing</p>'
)

foreach ($snippet in $requiredSnippets) {
    if (-not $readme.Contains($snippet)) {
        throw "README email examples are missing expected rendered snippet: $snippet"
    }
}

dotnet test (Join-Path $RepoRoot "code\EmailForwardingApplication.sln") --configuration Release --filter "FullyQualifiedName~RotaryEmailForwarding.Tests.SpecBehaviorTests"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
