using System.Globalization;
using System.Text.Json;

namespace StaticSignatureVerification.Storage;

public static class JsonVerificationResultMapper
{
    public static VerificationResultRecord FromJson(
        string resultJson,
        string? documentName = null,
        string? sourceDocumentPath = null,
        string? resultPath = null)
    {
        using var document = JsonDocument.Parse(resultJson);
        var root = document.RootElement;
        var audit = Object(root, "operationalAudit");

        var documentResultId = String(root, "documentResultId");
        if (string.IsNullOrWhiteSpace(documentResultId))
        {
            throw new InvalidOperationException("Result JSON does not contain documentResultId.");
        }

        var eventUtc = Date(audit, "finishedUtc") ??
                       Date(audit, "startedUtc") ??
                       DateTimeOffset.UtcNow;

        var cases = new List<SignatureCaseRecord>();
        foreach (var signature in Array(root, "signatureResults"))
        {
            cases.Add(MapSignature(documentResultId, signature));
        }

        return new VerificationResultRecord(
            DocumentResultId: documentResultId,
            CorrelationId: String(audit, "correlationId"),
            DocumentName: documentName ?? InferDocumentName(resultPath, sourceDocumentPath),
            SourceDocumentPath: sourceDocumentPath,
            ResultPath: resultPath,
            OverallDecision: String(root, "overallDecision") ?? "Unknown",
            OverallConfidence: Number(root, "overallConfidence"),
            InputDocumentType: String(root, "inputDocumentType") ?? String(audit, "inputDocumentType") ?? "Unknown",
            SignatureCountExpected: Int(root, "signatureCountExpected"),
            SignatureCountDetected: Int(root, "signatureCountDetected"),
            EventUtc: eventUtc,
            DurationMs: Number(audit, "durationMs"),
            ErrorCode: String(audit, "errorCode") ?? FirstErrorCode(root),
            ResultJson: resultJson,
            SignatureCases: cases);
    }

    private static SignatureCaseRecord MapSignature(string documentResultId, JsonElement signature)
    {
        var mapping = Object(signature, "mapping");
        var detectedRegion = Object(signature, "detectedRegion");
        var audit = Object(signature, "audit");
        var debugArtifacts = MapDebugArtifacts(Object(audit, "debugImages"));
        var comparisons = MapComparisons(Array(audit, "referenceComparisons"));
        var bestComparison = comparisons.FirstOrDefault(item => item.IsBestMatch) ??
                             comparisons.OrderByDescending(item => item.Confidence).FirstOrDefault();

        return new SignatureCaseRecord(
            DocumentResultId: documentResultId,
            SignatureId: String(signature, "signatureId") ?? string.Empty,
            DisplayName: String(signature, "displayName"),
            Decision: String(signature, "decision") ?? "Unknown",
            Confidence: Number(signature, "confidence"),
            IsMatched: Bool(signature, "isMatched"),
            ReviewRequired: Bool(signature, "reviewRequired"),
            SignatureDetected: Bool(signature, "signatureDetected"),
            SignatureQuality: String(signature, "signatureQuality"),
            PartyId: String(mapping, "partyId"),
            PartyName: String(mapping, "partyName"),
            ReferenceSetId: String(mapping, "referenceSetId"),
            ExpectedSignerId: String(mapping, "expectedSignerId"),
            ActualSignerId: String(mapping, "actualSignerId"),
            ExpectedClass: String(mapping, "expectedClass"),
            MappingSource: String(mapping, "source"),
            BestReferenceId: String(signature, "bestReferenceId") ?? bestComparison?.ReferenceId,
            BestReferenceFileName: bestComparison?.ReferenceFileName,
            BestReferenceFilePath: bestComparison?.ReferenceFilePath,
            ExtractedSignatureImagePath: ExtractedSignaturePath(debugArtifacts),
            DetectedPageIndex: NullableInt(detectedRegion, "pageIndex"),
            DetectedX: NullableInt(detectedRegion, "x"),
            DetectedY: NullableInt(detectedRegion, "y"),
            DetectedWidth: NullableInt(detectedRegion, "width"),
            DetectedHeight: NullableInt(detectedRegion, "height"),
            WarningCount: Array(signature, "warnings").Count,
            CandidateCount: Array(signature, "candidateRegions").Count,
            DebugFileCount: debugArtifacts.Count,
            SignatureResultJson: signature.GetRawText(),
            ReferenceComparisons: comparisons,
            DebugArtifacts: debugArtifacts);
    }

    private static IReadOnlyList<ReferenceComparisonRecord> MapComparisons(IReadOnlyList<JsonElement> elements)
    {
        var comparisons = new List<ReferenceComparisonRecord>();
        foreach (var comparison in elements)
        {
            comparisons.Add(new ReferenceComparisonRecord(
                ReferenceId: String(comparison, "referenceId"),
                ReferenceFileName: String(comparison, "referenceFileName"),
                ReferenceFilePath: String(comparison, "referenceFilePath"),
                Confidence: Number(comparison, "confidence"),
                QualityAdjustedScore: Number(comparison, "qualityAdjustedScore"),
                IsBestMatch: Bool(comparison, "isBestMatch"),
                ComparisonJson: comparison.GetRawText()));
        }

        return comparisons;
    }

    private static IReadOnlyList<DebugArtifactRecord> MapDebugArtifacts(JsonElement? debugImages)
    {
        var artifacts = new List<DebugArtifactRecord>();
        if (debugImages is not { ValueKind: JsonValueKind.Object })
        {
            return artifacts;
        }

        foreach (var property in debugImages.Value.EnumerateObject())
        {
            var path = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                artifacts.Add(new DebugArtifactRecord(property.Name, path));
            }
        }

        return artifacts;
    }

    private static string? ExtractedSignaturePath(IReadOnlyList<DebugArtifactRecord> artifacts)
    {
        string? Find(string name) => artifacts.FirstOrDefault(item =>
            item.ArtifactType.Equals(name, StringComparison.OrdinalIgnoreCase))?.ArtifactPath;

        return Find("cleanedCropPath") ??
               Find("normalizedPath") ??
               Find("rawCropPath") ??
               Find("skeletonPath") ??
               artifacts.FirstOrDefault()?.ArtifactPath;
    }

    private static string? InferDocumentName(string? resultPath, string? sourceDocumentPath)
    {
        if (!string.IsNullOrWhiteSpace(sourceDocumentPath))
        {
            return Path.GetFileNameWithoutExtension(sourceDocumentPath);
        }

        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return null;
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(resultPath));
        return string.IsNullOrWhiteSpace(parent)
            ? Path.GetFileNameWithoutExtension(resultPath)
            : parent;
    }

    private static string? FirstErrorCode(JsonElement root)
    {
        var errors = Array(root, "errors");
        return errors.Count == 0 ? null : String(errors[0], "code");
    }

    private static JsonElement? Object(JsonElement? element, string name)
    {
        return element is { ValueKind: JsonValueKind.Object } value &&
               value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static string? String(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static double Number(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static int Int(JsonElement? element, string name) => NullableInt(element, name) ?? 0;

    private static int? NullableInt(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool Bool(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static DateTimeOffset? Date(JsonElement? element, string name)
    {
        var text = String(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<JsonElement>();
        }

        return property.EnumerateArray().ToArray();
    }
}
