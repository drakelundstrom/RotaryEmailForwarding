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

$requiredSnippets = @(
    'The examples below show representative HTML email output rendered by `EmailTemplateService`',
    '<p>Hello RYE District 6630 Representatives,</p>',
    '<p>Hello RYE District 6630 and District 6650 Representatives,</p>',
    '<p>Hello RYE Mexico Representatives,</p>',
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
