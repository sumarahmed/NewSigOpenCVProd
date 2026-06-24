param(
    [string]$ConnectionString = "Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;",
    [string]$InputFolder = "C:\Temp\SignatureVerification\Output",
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"

function New-SqlConnection([string]$connectionString) {
    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    return $connection
}

function Add-Parameter($command, [string]$name, $value) {
    $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::NVarChar)
    $parameter.Size = -1
    $parameter.Value = if ($null -eq $value -or $value -eq "") { [DBNull]::Value } else { [string]$value }
    return $parameter
}

function Add-DecimalParameter($command, [string]$name, $value) {
    $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::Decimal)
    $parameter.Precision = 18
    $parameter.Scale = 2
    $parameter.Value = if ($null -eq $value) { 0 } else { [decimal]$value }
    return $parameter
}

function Add-IntParameter($command, [string]$name, $value) {
    $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::Int)
    $parameter.Value = if ($null -eq $value) { 0 } else { [int]$value }
    return $parameter
}

function Add-BitParameter($command, [string]$name, $value) {
    $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::Bit)
    $parameter.Value = [bool]$value
    return $parameter
}

function Add-DateParameter($command, [string]$name, $value) {
    $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::DateTimeOffset)
    $parameter.Value = if ($null -eq $value) { [DateTimeOffset]::UtcNow } else { [DateTimeOffset]$value }
    return $parameter
}

function Invoke-Command($connection, [string]$sql, [scriptblock]$parameters) {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = $sql
    if ($parameters) {
        & $parameters $command
    }
    return $command.ExecuteScalar()
}

function Get-JsonValue($object, [string]$name, $fallback = $null) {
    if ($null -ne $object -and $object.PSObject.Properties.Name -contains $name) {
        return $object.$name
    }

    return $fallback
}

function Get-DebugImageCount($signature) {
    $debugImages = Get-JsonValue (Get-JsonValue $signature "audit") "debugImages"
    if ($null -eq $debugImages) {
        return 0
    }

    return @($debugImages.PSObject.Properties).Count
}

function Get-BestReference($signature) {
    $audit = Get-JsonValue $signature "audit"
    $comparisons = @(Get-JsonValue $audit "referenceComparisons" @())
    if ($comparisons.Count -eq 0) {
        return $null
    }

    $best = $comparisons | Where-Object { $_.isBestMatch -eq $true } | Select-Object -First 1
    if ($null -eq $best) {
        $best = $comparisons | Sort-Object confidence -Descending | Select-Object -First 1
    }

    return $best
}

function Infer-DocumentName([string]$resultPath) {
    $fileName = [System.IO.Path]::GetFileNameWithoutExtension($resultPath)
    if ($fileName.EndsWith(".result", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fileName.Substring(0, $fileName.Length - ".result".Length)
    }

    $parent = Split-Path (Split-Path $resultPath -Parent) -Leaf
    if ([string]::IsNullOrWhiteSpace($parent)) {
        return $fileName
    }

    return $parent
}

if (-not (Test-Path -LiteralPath $InputFolder)) {
    throw "Input folder was not found: $InputFolder"
}

$connection = New-SqlConnection $ConnectionString
$imported = 0
$caseCount = 0
try {
    $files = Get-ChildItem -LiteralPath $InputFolder -Filter *.json -Recurse | Sort-Object FullName
    foreach ($file in $files) {
        $raw = Get-Content -LiteralPath $file.FullName -Raw
        try {
            $doc = $raw | ConvertFrom-Json
        }
        catch {
            continue
        }

        if ($null -eq (Get-JsonValue $doc "signatureResults")) {
            continue
        }

        $operational = Get-JsonValue $doc "operationalAudit"
        $documentResultId = Get-JsonValue $doc "documentResultId"
        if ([string]::IsNullOrWhiteSpace($documentResultId)) {
            continue
        }

        $eventUtcText = Get-JsonValue $operational "finishedUtc" (Get-JsonValue $operational "startedUtc")
        $eventUtc = if ($eventUtcText) { [DateTimeOffset]::Parse($eventUtcText).ToUniversalTime() } else { [DateTimeOffset]$file.LastWriteTimeUtc }
        $documentName = Infer-DocumentName $file.FullName

        Invoke-Command $connection @"
MERGE ssv.VerificationDocument AS target
USING (SELECT @DocumentResultId AS DocumentResultId) AS source
ON target.DocumentResultId = source.DocumentResultId
WHEN MATCHED THEN UPDATE SET
    CorrelationId = @CorrelationId,
    DocumentName = @DocumentName,
    ResultPath = @ResultPath,
    InputDocumentType = @InputDocumentType,
    OverallDecision = @OverallDecision,
    OverallConfidence = @OverallConfidence,
    SignatureCountExpected = @SignatureCountExpected,
    SignatureCountDetected = @SignatureCountDetected,
    EventUtc = @EventUtc,
    DurationMs = @DurationMs,
    ErrorCode = @ErrorCode,
    ResultJson = @ResultJson,
    UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT
    (DocumentResultId, CorrelationId, DocumentName, ResultPath, InputDocumentType, OverallDecision, OverallConfidence,
     SignatureCountExpected, SignatureCountDetected, EventUtc, DurationMs, ErrorCode, ResultJson)
VALUES
    (@DocumentResultId, @CorrelationId, @DocumentName, @ResultPath, @InputDocumentType, @OverallDecision, @OverallConfidence,
     @SignatureCountExpected, @SignatureCountDetected, @EventUtc, @DurationMs, @ErrorCode, @ResultJson);
SELECT 1;
"@ {
            param($cmd)
            Add-Parameter $cmd "@DocumentResultId" $documentResultId | Out-Null
            Add-Parameter $cmd "@CorrelationId" (Get-JsonValue $operational "correlationId") | Out-Null
            Add-Parameter $cmd "@DocumentName" $documentName | Out-Null
            Add-Parameter $cmd "@ResultPath" $file.FullName | Out-Null
            Add-Parameter $cmd "@InputDocumentType" (Get-JsonValue $doc "inputDocumentType" "Unknown") | Out-Null
            Add-Parameter $cmd "@OverallDecision" (Get-JsonValue $doc "overallDecision" "Unknown") | Out-Null
            Add-DecimalParameter $cmd "@OverallConfidence" (Get-JsonValue $doc "overallConfidence" 0) | Out-Null
            Add-IntParameter $cmd "@SignatureCountExpected" (Get-JsonValue $doc "signatureCountExpected" 0) | Out-Null
            Add-IntParameter $cmd "@SignatureCountDetected" (Get-JsonValue $doc "signatureCountDetected" 0) | Out-Null
            Add-DateParameter $cmd "@EventUtc" $eventUtc | Out-Null
            Add-DecimalParameter $cmd "@DurationMs" (Get-JsonValue $operational "durationMs" 0) | Out-Null
            Add-Parameter $cmd "@ErrorCode" (Get-JsonValue $operational "errorCode") | Out-Null
            Add-Parameter $cmd "@ResultJson" $raw | Out-Null
        } | Out-Null

        foreach ($signature in @(Get-JsonValue $doc "signatureResults" @())) {
            $mapping = Get-JsonValue $signature "mapping"
            $detectedRegion = Get-JsonValue $signature "detectedRegion"
            $bestReference = Get-BestReference $signature
            $signatureJson = $signature | ConvertTo-Json -Depth 100 -Compress
            $signatureId = Get-JsonValue $signature "signatureId" ""

            $signatureCaseId = Invoke-Command $connection @"
MERGE ssv.SignatureCase AS target
USING (SELECT @DocumentResultId AS DocumentResultId, @SignatureId AS SignatureId) AS source
ON target.DocumentResultId = source.DocumentResultId AND target.SignatureId = source.SignatureId
WHEN MATCHED THEN UPDATE SET
    DisplayName = @DisplayName,
    Decision = @Decision,
    Confidence = @Confidence,
    IsMatched = @IsMatched,
    ReviewRequired = @ReviewRequired,
    SignatureDetected = @SignatureDetected,
    SignatureQuality = @SignatureQuality,
    PartyId = @PartyId,
    PartyName = @PartyName,
    ReferenceSetId = @ReferenceSetId,
    ExpectedSignerId = @ExpectedSignerId,
    ActualSignerId = @ActualSignerId,
    ExpectedClass = @ExpectedClass,
    MappingSource = @MappingSource,
    BestReferenceId = @BestReferenceId,
    BestReferenceFileName = @BestReferenceFileName,
    BestReferenceFilePath = @BestReferenceFilePath,
    DetectedPageIndex = @DetectedPageIndex,
    DetectedX = @DetectedX,
    DetectedY = @DetectedY,
    DetectedWidth = @DetectedWidth,
    DetectedHeight = @DetectedHeight,
    WarningCount = @WarningCount,
    CandidateCount = @CandidateCount,
    DebugFileCount = @DebugFileCount,
    SignatureResultJson = @SignatureResultJson,
    UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT
    (DocumentResultId, SignatureId, DisplayName, Decision, Confidence, IsMatched, ReviewRequired, SignatureDetected, SignatureQuality,
     PartyId, PartyName, ReferenceSetId, ExpectedSignerId, ActualSignerId, ExpectedClass, MappingSource,
     BestReferenceId, BestReferenceFileName, BestReferenceFilePath, DetectedPageIndex, DetectedX, DetectedY, DetectedWidth, DetectedHeight,
     WarningCount, CandidateCount, DebugFileCount, SignatureResultJson)
VALUES
    (@DocumentResultId, @SignatureId, @DisplayName, @Decision, @Confidence, @IsMatched, @ReviewRequired, @SignatureDetected, @SignatureQuality,
     @PartyId, @PartyName, @ReferenceSetId, @ExpectedSignerId, @ActualSignerId, @ExpectedClass, @MappingSource,
     @BestReferenceId, @BestReferenceFileName, @BestReferenceFilePath, @DetectedPageIndex, @DetectedX, @DetectedY, @DetectedWidth, @DetectedHeight,
     @WarningCount, @CandidateCount, @DebugFileCount, @SignatureResultJson);
SELECT SignatureCaseId FROM ssv.SignatureCase WHERE DocumentResultId = @DocumentResultId AND SignatureId = @SignatureId;
"@ {
                param($cmd)
                Add-Parameter $cmd "@DocumentResultId" $documentResultId | Out-Null
                Add-Parameter $cmd "@SignatureId" $signatureId | Out-Null
                Add-Parameter $cmd "@DisplayName" (Get-JsonValue $signature "displayName") | Out-Null
                Add-Parameter $cmd "@Decision" (Get-JsonValue $signature "decision" "Unknown") | Out-Null
                Add-DecimalParameter $cmd "@Confidence" (Get-JsonValue $signature "confidence" 0) | Out-Null
                Add-BitParameter $cmd "@IsMatched" (Get-JsonValue $signature "isMatched" $false) | Out-Null
                Add-BitParameter $cmd "@ReviewRequired" (Get-JsonValue $signature "reviewRequired" $false) | Out-Null
                Add-BitParameter $cmd "@SignatureDetected" (Get-JsonValue $signature "signatureDetected" $false) | Out-Null
                Add-Parameter $cmd "@SignatureQuality" (Get-JsonValue $signature "signatureQuality") | Out-Null
                Add-Parameter $cmd "@PartyId" (Get-JsonValue $mapping "partyId") | Out-Null
                Add-Parameter $cmd "@PartyName" (Get-JsonValue $mapping "partyName") | Out-Null
                Add-Parameter $cmd "@ReferenceSetId" (Get-JsonValue $mapping "referenceSetId") | Out-Null
                Add-Parameter $cmd "@ExpectedSignerId" (Get-JsonValue $mapping "expectedSignerId") | Out-Null
                Add-Parameter $cmd "@ActualSignerId" (Get-JsonValue $mapping "actualSignerId") | Out-Null
                Add-Parameter $cmd "@ExpectedClass" (Get-JsonValue $mapping "expectedClass") | Out-Null
                Add-Parameter $cmd "@MappingSource" (Get-JsonValue $mapping "source") | Out-Null
                Add-Parameter $cmd "@BestReferenceId" (Get-JsonValue $signature "bestReferenceId" (Get-JsonValue $bestReference "referenceId")) | Out-Null
                Add-Parameter $cmd "@BestReferenceFileName" (Get-JsonValue $bestReference "referenceFileName") | Out-Null
                Add-Parameter $cmd "@BestReferenceFilePath" (Get-JsonValue $bestReference "referenceFilePath") | Out-Null
                Add-IntParameter $cmd "@DetectedPageIndex" (Get-JsonValue $detectedRegion "pageIndex" $null) | Out-Null
                Add-IntParameter $cmd "@DetectedX" (Get-JsonValue $detectedRegion "x" $null) | Out-Null
                Add-IntParameter $cmd "@DetectedY" (Get-JsonValue $detectedRegion "y" $null) | Out-Null
                Add-IntParameter $cmd "@DetectedWidth" (Get-JsonValue $detectedRegion "width" $null) | Out-Null
                Add-IntParameter $cmd "@DetectedHeight" (Get-JsonValue $detectedRegion "height" $null) | Out-Null
                Add-IntParameter $cmd "@WarningCount" @((Get-JsonValue $signature "warnings" @())).Count | Out-Null
                Add-IntParameter $cmd "@CandidateCount" @((Get-JsonValue $signature "candidateRegions" @())).Count | Out-Null
                Add-IntParameter $cmd "@DebugFileCount" (Get-DebugImageCount $signature) | Out-Null
                Add-Parameter $cmd "@SignatureResultJson" $signatureJson | Out-Null
            }

            Invoke-Command $connection "DELETE FROM ssv.ReferenceComparison WHERE SignatureCaseId = @SignatureCaseId; SELECT 1;" {
                param($cmd)
                Add-IntParameter $cmd "@SignatureCaseId" $signatureCaseId | Out-Null
            } | Out-Null

            $comparisons = @(Get-JsonValue (Get-JsonValue $signature "audit") "referenceComparisons" @())
            foreach ($comparison in $comparisons) {
                $comparisonJson = $comparison | ConvertTo-Json -Depth 100 -Compress
                Invoke-Command $connection @"
INSERT INTO ssv.ReferenceComparison
    (SignatureCaseId, ReferenceId, ReferenceFileName, ReferenceFilePath, Confidence, QualityAdjustedScore, IsBestMatch, ComparisonJson)
VALUES
    (@SignatureCaseId, @ReferenceId, @ReferenceFileName, @ReferenceFilePath, @Confidence, @QualityAdjustedScore, @IsBestMatch, @ComparisonJson);
SELECT 1;
"@ {
                    param($cmd)
                    Add-IntParameter $cmd "@SignatureCaseId" $signatureCaseId | Out-Null
                    Add-Parameter $cmd "@ReferenceId" (Get-JsonValue $comparison "referenceId") | Out-Null
                    Add-Parameter $cmd "@ReferenceFileName" (Get-JsonValue $comparison "referenceFileName") | Out-Null
                    Add-Parameter $cmd "@ReferenceFilePath" (Get-JsonValue $comparison "referenceFilePath") | Out-Null
                    Add-DecimalParameter $cmd "@Confidence" (Get-JsonValue $comparison "confidence" 0) | Out-Null
                    Add-DecimalParameter $cmd "@QualityAdjustedScore" (Get-JsonValue $comparison "qualityAdjustedScore" 0) | Out-Null
                    Add-BitParameter $cmd "@IsBestMatch" (Get-JsonValue $comparison "isBestMatch" $false) | Out-Null
                    Add-Parameter $cmd "@ComparisonJson" $comparisonJson | Out-Null
                } | Out-Null
            }

            $caseCount++
        }

        $imported++
    }
}
finally {
    $connection.Close()
}

Write-Host "Imported documents: $imported"
Write-Host "Imported signature cases: $caseCount"
