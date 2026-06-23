using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using StaticSignatureVerification.Core;
using StaticSignatureVerification.PdfRendering;

namespace StaticSignatureVerification.TotalAgilityWrapper;

/// <summary>
/// Stable string-only facade intended for Tungsten/Kofax TotalAgility .NET integration.
/// </summary>
public sealed class SignatureVerificationWrapper
{
    private const string EngineVersion = "1.0.0";
    private readonly IVerificationApplicationService _applicationService;
    private readonly IPdfPageRenderer _pdfRenderer;
    private readonly IVerificationLogger _logger;

    public SignatureVerificationWrapper()
        : this(new VerificationApplicationService(), new GhostscriptCommandLinePdfRenderer(), NoopVerificationLogger.Instance)
    {
    }

    public SignatureVerificationWrapper(IVerificationApplicationService applicationService, IPdfPageRenderer pdfRenderer)
        : this(applicationService, pdfRenderer, NoopVerificationLogger.Instance)
    {
    }

    public SignatureVerificationWrapper(IVerificationApplicationService applicationService, IPdfPageRenderer pdfRenderer, IVerificationLogger logger)
    {
        _applicationService = applicationService;
        _pdfRenderer = pdfRenderer;
        _logger = logger;
    }

    /// <summary>
    /// Hardened TotalAgility entry point that accepts one JSON request and returns the standard result JSON.
    /// Required fields: documentBase64, referenceSignaturesJson. Optional fields: correlationId, ocrLayoutJson, optionsJson,
    /// matchedThreshold, probableMatchThreshold, reviewRequiredThreshold, signatureRoleMode, signatureMappings, signatureMappingsJson.
    /// </summary>
    public string VerifySignaturesRequest(string requestJson)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        string? correlationId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return CreateWrapperError("REQUEST_INVALID", "The request JSON is required.", startedUtc, null);
            }

            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CreateWrapperError("REQUEST_INVALID", "The request JSON must be an object.", startedUtc, null);
            }

            correlationId = TryGetString(root, "correlationId");
            var documentBase64 = TryGetString(root, "documentBase64");
            var referenceSignaturesJson = TryGetString(root, "referenceSignaturesJson");
            if (string.IsNullOrWhiteSpace(documentBase64))
            {
                return CreateWrapperError("REQUEST_INVALID", "The documentBase64 field is required.", startedUtc, correlationId);
            }

            if (string.IsNullOrWhiteSpace(referenceSignaturesJson))
            {
                return CreateWrapperError("REQUEST_INVALID", "The referenceSignaturesJson field is required.", startedUtc, correlationId);
            }

            var optionsJson = MergeStructuredRequestOptions(
                TryGetString(root, "optionsJson"),
                correlationId,
                TryGetString(root, "matchedThreshold"),
                TryGetString(root, "probableMatchThreshold"),
                TryGetString(root, "reviewRequiredThreshold"),
                TryGetString(root, "signatureRoleMode"));

            referenceSignaturesJson = MergeSignatureMappingsIntoReferenceJson(referenceSignaturesJson, root);

            return VerifySignatures(
                documentBase64,
                TryGetString(root, "ocrLayoutJson") ?? string.Empty,
                referenceSignaturesJson,
                optionsJson);
        }
        catch (JsonException ex)
        {
            _logger.Error(correlationId ?? "unknown", "WrapperRequestParsing", ex, "REQUEST_INVALID");
            return CreateWrapperError("REQUEST_INVALID", "The request JSON could not be parsed.", startedUtc, null);
        }
        catch (FormatException ex)
        {
            _logger.Error(correlationId ?? "unknown", "WrapperOptionsParsing", ex, "OPTIONS_JSON_INVALID");
            return CreateWrapperError("OPTIONS_JSON_INVALID", "One or more threshold parameters could not be parsed.", startedUtc, correlationId);
        }
        catch (Exception ex)
        {
            _logger.Error(correlationId ?? "unknown", "WrapperVerifyRequest", ex, "UNHANDLED_EXCEPTION");
            return CreateWrapperError("UNHANDLED_EXCEPTION", "An unexpected wrapper error occurred while verifying signatures.", startedUtc, correlationId);
        }
    }

    /// <summary>
    /// Verifies one or more expected signatures and always returns the public result JSON contract.
    /// </summary>
    public string VerifySignatures(
        string documentBase64,
        string ocrLayoutJson,
        string referenceSignaturesJson,
        string optionsJson)
    {
        try
        {
            return _applicationService.VerifyToJson(new SignatureVerificationRequest(
                documentBase64,
                ocrLayoutJson,
                referenceSignaturesJson,
                optionsJson,
                _pdfRenderer));
        }
        catch (Exception ex)
        {
            _logger.Error("unknown", "WrapperVerify", ex, "UNHANDLED_EXCEPTION");
            return StaticSignatureVerificationEngine.ToJson(ResultJsonBuilder.CreateError(
                "1.0.0",
                "UNHANDLED_EXCEPTION",
                "An unexpected wrapper error occurred while verifying signatures."));
        }
    }

    /// <summary>
    /// Verifies signatures with explicit threshold parameters merged into the options JSON.
    /// </summary>
    public string VerifySignaturesWithThresholds(
        string documentBase64,
        string ocrLayoutJson,
        string referenceSignaturesJson,
        string optionsJson,
        string matchedThreshold,
        string probableMatchThreshold,
        string reviewRequiredThreshold)
    {
        try
        {
            var mergedOptions = MergeThresholds(optionsJson, matchedThreshold, probableMatchThreshold, reviewRequiredThreshold);
            return VerifySignatures(documentBase64, ocrLayoutJson, referenceSignaturesJson, mergedOptions);
        }
        catch (Exception ex)
        {
            _logger.Error("unknown", "WrapperThresholds", ex, "OPTIONS_JSON_INVALID");
            return StaticSignatureVerificationEngine.ToJson(ResultJsonBuilder.CreateError(
                "1.0.0",
                "OPTIONS_JSON_INVALID",
                "Threshold parameters could not be parsed."));
        }
    }

    /// <summary>
    /// Verifies signatures with an explicit expected signature role mode merged into the options JSON.
    /// </summary>
    public string VerifySignaturesWithSignatureRoleMode(
        string documentBase64,
        string ocrLayoutJson,
        string referenceSignaturesJson,
        string optionsJson,
        string signatureRoleMode)
    {
        try
        {
            var mergedOptions = MergeSignatureRoleMode(optionsJson, signatureRoleMode);
            return VerifySignatures(documentBase64, ocrLayoutJson, referenceSignaturesJson, mergedOptions);
        }
        catch (Exception ex)
        {
            _logger.Error("unknown", "WrapperSignatureRoleMode", ex, "OPTIONS_JSON_INVALID");
            return StaticSignatureVerificationEngine.ToJson(ResultJsonBuilder.CreateError(
                "1.0.0",
                "OPTIONS_JSON_INVALID",
                "Signature role mode could not be parsed."));
        }
    }

    /// <summary>
    /// Convenience overload for one expected signature and up to two reference signature images.
    /// </summary>
    public string VerifySignature(
        string documentBase64,
        string referenceSignature1Base64,
        string referenceSignature2Base64)
    {
        return VerifySignature(documentBase64, referenceSignature1Base64, referenceSignature2Base64, string.Empty);
    }

    /// <summary>
    /// Convenience overload for one expected signature with caller-supplied options JSON.
    /// </summary>
    public string VerifySignature(
        string documentBase64,
        string referenceSignature1Base64,
        string referenceSignature2Base64,
        string optionsJson)
    {
        try
        {
            var references = new
            {
                referenceSets = new[]
                {
                    new
                    {
                        signatureId = "signature",
                        displayName = "Signature",
                        expectedLabels = new[] { "Signature", "Signed By" },
                        referenceImages = new[]
                        {
                            new { referenceId = "reference_1", imageBase64 = referenceSignature1Base64 },
                            new { referenceId = "reference_2", imageBase64 = referenceSignature2Base64 }
                        }.Where(r => !string.IsNullOrWhiteSpace(r.imageBase64)).ToArray()
                    }
                }
            };

            var mergedOptions = MergeSimpleSignatureDefaults(optionsJson);

            return VerifySignatures(
                documentBase64,
                string.Empty,
                JsonSerializer.Serialize(references, CreateWebJsonOptions()),
                mergedOptions);
        }
        catch (Exception ex)
        {
            _logger.Error("unknown", "WrapperSimpleSignature", ex, "UNHANDLED_EXCEPTION");
            return StaticSignatureVerificationEngine.ToJson(ResultJsonBuilder.CreateError(
                "1.0.0",
                "UNHANDLED_EXCEPTION",
                "An unexpected wrapper error occurred while verifying the signature."));
        }
    }

    /// <summary>
    /// Convenience overload for one expected signature with explicit threshold string parameters.
    /// </summary>
    public string VerifySignatureWithThresholds(
        string documentBase64,
        string referenceSignature1Base64,
        string referenceSignature2Base64,
        string matchedThreshold,
        string probableMatchThreshold,
        string reviewRequiredThreshold)
    {
        var optionsJson = MergeThresholds(string.Empty, matchedThreshold, probableMatchThreshold, reviewRequiredThreshold);
        return VerifySignature(documentBase64, referenceSignature1Base64, referenceSignature2Base64, optionsJson);
    }

    private static string MergeSimpleSignatureDefaults(string optionsJson)
    {
        var root = string.IsNullOrWhiteSpace(optionsJson)
            ? new JsonObject()
            : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();

        root["inputDocumentType"] ??= "Auto";
        var detection = EnsureObject(root, "detection");
        detection["useKnownZones"] ??= false;
        detection["useOcrAnchors"] ??= true;
        detection["useBoxDetection"] ??= true;
        detection["useInkRegionDetection"] ??= true;
        return root.ToJsonString(CreateWebJsonOptions());
    }

    private static string MergeThresholds(string optionsJson, string matchedThreshold, string probableMatchThreshold, string reviewRequiredThreshold)
    {
        var root = string.IsNullOrWhiteSpace(optionsJson)
            ? new JsonObject()
            : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();

        var thresholds = EnsureObject(root, "thresholds");
        thresholds["matched"] = ParseThreshold(matchedThreshold, 85);
        thresholds["probableMatch"] = ParseThreshold(probableMatchThreshold, 70);
        thresholds["reviewRequired"] = ParseThreshold(reviewRequiredThreshold, 55);
        return root.ToJsonString(CreateWebJsonOptions());
    }

    private static string MergeStructuredRequestOptions(
        string? optionsJson,
        string? correlationId,
        string? matchedThreshold,
        string? probableMatchThreshold,
        string? reviewRequiredThreshold,
        string? signatureRoleMode)
    {
        var root = string.IsNullOrWhiteSpace(optionsJson)
            ? new JsonObject()
            : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            root["correlationId"] = correlationId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(matchedThreshold) ||
            !string.IsNullOrWhiteSpace(probableMatchThreshold) ||
            !string.IsNullOrWhiteSpace(reviewRequiredThreshold))
        {
            var thresholds = EnsureObject(root, "thresholds");
            SetThresholdIfProvided(thresholds, "matched", matchedThreshold);
            SetThresholdIfProvided(thresholds, "probableMatch", probableMatchThreshold);
            SetThresholdIfProvided(thresholds, "reviewRequired", reviewRequiredThreshold);
        }

        if (!string.IsNullOrWhiteSpace(signatureRoleMode))
        {
            EnsureObject(root, "signatureRoles")["mode"] = signatureRoleMode.Trim();
        }

        return root.ToJsonString(CreateWebJsonOptions());
    }

    private static string MergeSignatureRoleMode(string optionsJson, string signatureRoleMode)
    {
        var root = string.IsNullOrWhiteSpace(optionsJson)
            ? new JsonObject()
            : JsonNode.Parse(optionsJson)?.AsObject() ?? new JsonObject();

        var signatureRoles = EnsureObject(root, "signatureRoles");
        signatureRoles["mode"] = string.IsNullOrWhiteSpace(signatureRoleMode) ? "Auto" : signatureRoleMode.Trim();
        return root.ToJsonString(CreateWebJsonOptions());
    }

    private static string MergeSignatureMappingsIntoReferenceJson(string referenceSignaturesJson, JsonElement requestRoot)
    {
        JsonNode? mappingsNode = null;
        var mappingsJson = TryGetString(requestRoot, "signatureMappingsJson");
        if (!string.IsNullOrWhiteSpace(mappingsJson))
        {
            mappingsNode = JsonNode.Parse(mappingsJson);
        }
        else if (requestRoot.TryGetProperty("signatureMappings", out var mappings) &&
                 mappings.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            mappingsNode = JsonNode.Parse(mappings.GetRawText());
        }

        if (mappingsNode is null)
        {
            return referenceSignaturesJson;
        }

        var root = JsonNode.Parse(referenceSignaturesJson)?.AsObject() ?? new JsonObject();
        root["signatureMappings"] = mappingsNode;
        return root.ToJsonString(CreateWebJsonOptions());
    }

    private static JsonSerializerOptions CreateWebJsonOptions() => SignatureJsonOptions.Compact;

    private static JsonObject EnsureObject(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static double ParseThreshold(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static void SetThresholdIfProvided(JsonObject thresholds, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException($"Threshold '{propertyName}' could not be parsed.");
        }

        thresholds[propertyName] = parsed;
    }

    private static string? TryGetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string CreateWrapperError(string code, string message, DateTimeOffset startedUtc, string? correlationId)
    {
        var result = ResultJsonBuilder.CreateError(EngineVersion, code, message);
        var now = DateTimeOffset.UtcNow;
        result.OperationalAudit = new OperationalAudit
        {
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? result.DocumentResultId : correlationId.Trim(),
            StartedUtc = startedUtc,
            FinishedUtc = now,
            DurationMs = Math.Round((now - startedUtc).TotalMilliseconds, 2),
            Status = "Error",
            ErrorCode = code,
            ErrorsCount = result.Errors.Count
        };

        return StaticSignatureVerificationEngine.ToJson(result);
    }
}
