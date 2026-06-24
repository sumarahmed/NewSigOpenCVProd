param(
    [string]$ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;",
    [string]$OutputFolder = "C:\Temp\SignatureVerification\BusinessReports",
    [string]$Title = "Signature Verification Database Review Queue"
)

$ErrorActionPreference = "Stop"

function New-SqlConnection([string]$connectionString) {
    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    return $connection
}

function Html([object]$value) {
    return [System.Net.WebUtility]::HtmlEncode([string]$value)
}

function Csv([object]$value) {
    $text = if ($null -eq $value) { "" } else { [string]$value }
    return '"' + $text.Replace('"', '""') + '"'
}

function FileLink([object]$path, [string]$label) {
    if ($null -eq $path -or [string]::IsNullOrWhiteSpace([string]$path)) {
        return Html $label
    }

    try {
        $uri = [System.Uri]::new([System.IO.Path]::GetFullPath([string]$path)).AbsoluteUri
        return "<a href=""$uri"">$(Html $label)</a>"
    }
    catch {
        return Html $label
    }
}

[System.IO.Directory]::CreateDirectory($OutputFolder) | Out-Null

$connection = New-SqlConnection $ConnectionString
try {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = @"
SELECT
    EventUtc,
    DocumentName,
    DocumentResultId,
    CorrelationId,
    ResultPath,
    SourceDocumentPath,
    SignatureId,
    DisplayName,
    PartyId,
    PartyName,
    ReferenceSetId,
    ExpectedSignerId,
    ActualSignerId,
    ExpectedClass,
    MappingSource,
    Decision,
    Confidence,
    SignatureDetected,
    BestReferenceId,
    BestReferenceFileName,
    BestReferenceFilePath,
    WarningCount,
    CandidateCount,
    DebugFileCount
FROM ssv.vwReviewQueue
ORDER BY EventUtc DESC, DocumentName, SignatureId;
"@
    $reader = $command.ExecuteReader()
    $table = New-Object System.Data.DataTable
    $table.Load($reader)
}
finally {
    $connection.Close()
}

$csvPath = Join-Path $OutputFolder "db-review-queue.csv"
$htmlPath = Join-Path $OutputFolder "db-review-queue.html"

$csvHeader = @(
    "eventUtc","documentName","documentResultId","correlationId","signatureId","partyId","partyName",
    "referenceSetId","expectedSignerId","actualSignerId","expectedClass","mappingSource","decision","confidence",
    "signatureDetected","bestReferenceId","bestReferenceFileName","bestReferenceFilePath","resultPath",
    "warningCount","candidateCount","debugFileCount"
)
$csvRows = New-Object System.Collections.Generic.List[string]
$csvRows.Add(($csvHeader -join ","))
foreach ($row in $table.Rows) {
    $csvRows.Add(@(
        Csv $row.EventUtc,
        Csv $row.DocumentName,
        Csv $row.DocumentResultId,
        Csv $row.CorrelationId,
        Csv $row.SignatureId,
        Csv $row.PartyId,
        Csv $row.PartyName,
        Csv $row.ReferenceSetId,
        Csv $row.ExpectedSignerId,
        Csv $row.ActualSignerId,
        Csv $row.ExpectedClass,
        Csv $row.MappingSource,
        Csv $row.Decision,
        Csv $row.Confidence,
        Csv $row.SignatureDetected,
        Csv $row.BestReferenceId,
        Csv $row.BestReferenceFileName,
        Csv $row.BestReferenceFilePath,
        Csv $row.ResultPath,
        Csv $row.WarningCount,
        Csv $row.CandidateCount,
        Csv $row.DebugFileCount
    ) -join ",")
}
Set-Content -LiteralPath $csvPath -Value $csvRows -Encoding UTF8

$html = New-Object System.Text.StringBuilder
[void]$html.AppendLine("<!doctype html><html><head><meta charset=""utf-8""><title>$(Html $Title)</title>")
[void]$html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:28px;color:#202124}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border:1px solid #d0d7de;padding:6px 8px;text-align:left;vertical-align:top}th{background:#f6f8fa;position:sticky;top:0}.bad{color:#b42318;font-weight:600}.warn{color:#b54708;font-weight:600}.good{color:#067647;font-weight:600}.muted{color:#667085}a{color:#175cd3;text-decoration:none}</style></head><body>")
[void]$html.AppendLine("<h1>$(Html $Title)</h1>")
[void]$html.AppendLine("<p class=""muted"">Generated $(Html ([DateTimeOffset]::UtcNow.ToString('u'))) from SQL view <code>ssv.vwReviewQueue</code>. Rows: $($table.Rows.Count). CSV: $(FileLink $csvPath 'db-review-queue.csv')</p>")
[void]$html.AppendLine("<table><thead><tr><th>Date</th><th>Document</th><th>Role</th><th>Mapping</th><th>Decision</th><th>Confidence</th><th>Reference</th><th>Result</th></tr></thead><tbody>")
foreach ($row in $table.Rows) {
    $cls = if ($row.Decision -eq "Matched") { "good" } elseif ($row.Decision -in @("ProbableMatch","ReviewRequired","InsufficientQuality")) { "warn" } else { "bad" }
    $mapping = @()
    if ($row.PartyId) { $mapping += "Party $(Html $row.PartyId)" }
    if ($row.PartyName) { $mapping += Html $row.PartyName }
    if ($row.ReferenceSetId) { $mapping += "Ref set $(Html $row.ReferenceSetId)" }
    if ($row.ExpectedSignerId) { $mapping += "Expected $(Html $row.ExpectedSignerId)" }
    if ($row.ActualSignerId) { $mapping += "Actual $(Html $row.ActualSignerId)" }
    if ($row.ExpectedClass) { $mapping += "Class $(Html $row.ExpectedClass)" }
    if ($row.MappingSource) { $mapping += "Source $(Html $row.MappingSource)" }
    $referenceLabel = if ($row.ReferenceSetId -and $row.BestReferenceFileName) { "$($row.ReferenceSetId) / $($row.BestReferenceFileName)" } else { $row.BestReferenceId }
    [void]$html.AppendLine("<tr><td>$(Html $row.EventUtc)</td><td>$(Html $row.DocumentName)</td><td>$(Html $row.SignatureId)</td><td>$($mapping -join '<br>')</td><td class=""$cls"">$(Html $row.Decision)</td><td>$(Html $row.Confidence)</td><td>$(FileLink $row.BestReferenceFilePath $referenceLabel)</td><td>$(FileLink $row.ResultPath 'result')</td></tr>")
}
[void]$html.AppendLine("</tbody></table></body></html>")
Set-Content -LiteralPath $htmlPath -Value $html.ToString() -Encoding UTF8

Write-Host "Database review queue generated: $htmlPath"
Write-Host "Rows: $($table.Rows.Count)"
