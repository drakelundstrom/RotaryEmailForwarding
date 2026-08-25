[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InputCsv,

    [string] $OutputDirectory = ".catchup",

    [string] $Endpoint = "http://localhost:7071/api/catch-up-interest-form-entries"
)

$ErrorActionPreference = "Stop"
$rows = @(Import-Csv -LiteralPath $InputCsv)
if ($rows.Count -eq 0) {
    throw "The CSV contains no submissions."
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$items = foreach ($row in $rows) {
    $processedDate = [DateTimeOffset]::Parse(
        $row.'Date Updated',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
            [System.Globalization.DateTimeStyles]::AdjustToUniversal)

    [ordered]@{
        originalProcessedOnUtc = $processedDate.ToString("O")
        submission = [ordered]@{
            submissionType = $row.'Who are you?'
            submissionQuestion = $row.'Our local representatives will reach out with more information about the program, but do you have any specific questions?'
            name = $row.Name
            age = $row.'Current Age (Years)'
            parentEnteredAge = $row.'Current Age of your student (Years)'
            school = $row.'What high school do or will you attend?'
            parentEnteredSchool = $row.'What high school does or will your student attend?'
            studentEmail = $row.'Student''s Email (Do not use a school email address!)'
            studentPhone = $row.'Student''s Phone Number'
            parentEmail = $row.'Parent''s Email'
            parentPhone = $row.'Parent''s Phone Number'
            contactEmail = $row.'Contact Email'
            contactPhone = $row.'Contact Phone Number'
            countryOfResidence = $row.'Country of Residence'
            state = $row.'State or Province'
            city = $row.City
            zipcode = $row.'Zip Code or first 3 of CDN Postal Code Where You Live'
        }
    }
}

$batchFiles = [System.Collections.Generic.List[string]]::new()
for ($offset = 0; $offset -lt $items.Count; $offset += 20) {
    $batchNumber = [int]([Math]::Floor($offset / 20) + 1)
    $lastIndex = [Math]::Min($offset + 19, $items.Count - 1)
    $batch = @($items[$offset..$lastIndex])
    $batchPath = Join-Path $resolvedOutputDirectory ("batch-{0:D3}.json" -f $batchNumber)
    $batch | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $batchPath -Encoding utf8
    $batchFiles.Add($batchPath)
}

$curlPath = Join-Path $resolvedOutputDirectory "curl-commands.ps1"
$curlLines = @(
    '$localSettingsPath = Join-Path $PSScriptRoot "..\code\RotaryEmailForwarding.FunctionApp\local.settings.json"'
    '$adminApiKey = (Get-Content -Raw $localSettingsPath | ConvertFrom-Json).Values.adminApiKey'
    'if ([string]::IsNullOrWhiteSpace($adminApiKey)) { throw "adminApiKey is missing from local.settings.json." }'
    ''
)
foreach ($batchPath in $batchFiles) {
    $curlLines += 'curl.exe --fail-with-body --silent --show-error --request POST "{0}" --header "Content-Type: application/json" --header "x-admin-api-key: $adminApiKey" --data-binary "@{1}"' -f $Endpoint, $batchPath
}
$curlLines | Set-Content -LiteralPath $curlPath -Encoding utf8

Write-Host "Created $($batchFiles.Count) batch file(s) for $($items.Count) submissions."
Write-Host "Review them in: $resolvedOutputDirectory"
Write-Host "Curl commands: $curlPath"
