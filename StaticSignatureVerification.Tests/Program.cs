using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using StaticSignatureVerification.Core;
using StaticSignatureVerification.Storage;
using StaticSignatureVerification.TotalAgilityWrapper;

var tests = new List<(string Name, Action Test)>
{
    ("DocumentInputDetector detects PDF header", TestPdfDetection),
    ("OCR parser extracts layout labels", TestOcrParsing),
    ("OCR parser maps pageNumber to rendered page index", TestOcrPageNumberMapping),
    ("Preprocessor extracts ink metrics", TestPreprocessing),
    ("Wrapper returns safe JSON for invalid Base64", TestSafeErrorJson),
    ("End-to-end known-zone match returns result shape", TestKnownZoneEndToEnd),
    ("Wrapper accepts custom threshold parameters", TestCustomThresholdParameters),
    ("Wrapper parses decimal thresholds invariantly", TestDecimalThresholdParameters),
    ("Role configuration filters expected signatures", TestRoleConfigurationFiltersExpectedSignatures),
    ("Role configuration reports missing configured references", TestRoleConfigurationReportsMissingConfiguredReferences),
    ("Role configuration normalizes known zone IDs", TestRoleConfigurationNormalizesKnownZoneIds),
    ("Invalid options are normalized with warnings", TestInvalidOptionsAreNormalized),
    ("Multiple signatures use separate detected regions", TestMultipleSignaturesUseSeparateDetectedRegions),
    ("Duplicate candidate regions are not reused", TestDuplicateCandidateRegionsAreNotReused),
    ("Operational audit is returned on success and error", TestOperationalAudit),
    ("Storage mapper preserves result audit data", TestStorageMapperPreservesResultAuditData),
    ("Structured wrapper request validates TotalAgility contract", TestStructuredWrapperRequest),
    ("Wrapper delegates to application service seam", TestWrapperDelegatesToApplicationService),
    ("Application service returns stable JSON", TestApplicationServiceReturnsStableJson),
    ("Engine logs sanitized internal errors", TestEngineLogsSanitizedInternalErrors),
    ("Detection profile controls candidate scoring", TestDetectionProfileControlsCandidateScoring),
    ("Shared JSON options use stable camelCase contract", TestSharedJsonOptionsStableContract),
    ("Wrapper supports concurrent verification calls", TestConcurrentWrapperCalls),
    ("Wrapper performance smoke test stays within budget", TestPerformanceSmokeBudget)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Test();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine("  " + failure);
    }

    Environment.ExitCode = 1;
}

static void TestPdfDetection()
{
    var detector = new DocumentInputDetector();
    AssertEqual(InputDocumentType.PDF, detector.Detect("%PDF-1.7"u8.ToArray(), "Auto"));
    AssertEqual(InputDocumentType.Image, detector.Detect(new byte[] { 1, 2, 3 }, "Auto"));
}

static void TestOcrParsing()
{
    using var page = new Mat(new Size(1000, 1600), MatType.CV_8UC1, Scalar.White);
    var layoutJson = """
    {
      "analyzeResult": {
        "pages": [
          {
            "width": 8.5,
            "height": 11,
            "unit": "inch",
            "lines": [
              { "content": "Applicant Signature", "polygon": [1, 7, 3, 7, 3, 7.3, 1, 7.3] }
            ]
          }
        ]
      }
    }
    """;

    var warnings = new List<string>();
    var parser = new OcrLayoutParser();
    var parsed = parser.Parse(layoutJson, new[] { new PageImage(0, page, Array.Empty<byte>(), 300) }, warnings);
    AssertTrue(parsed.Labels.Count == 1, "Expected one OCR label.");
    AssertTrue(parsed.Labels[0].Rect.Width > 100, "Expected OCR coordinates to scale to pixels.");
}

static void TestOcrPageNumberMapping()
{
    using var page = new Mat(new Size(1000, 1600), MatType.CV_8UC1, Scalar.White);
    var layoutJson = """
    {
      "analyzeResult": {
        "pages": [
          {
            "pageNumber": 2,
            "width": 8.5,
            "height": 11,
            "unit": "inch",
            "lines": [
              { "content": "Witness Signature", "polygon": [1, 7, 3, 7, 3, 7.3, 1, 7.3] }
            ]
          }
        ]
      }
    }
    """;

    var warnings = new List<string>();
    var parser = new OcrLayoutParser();
    var parsed = parser.Parse(layoutJson, new[] { new PageImage(1, page, Array.Empty<byte>(), 300) }, warnings);

    AssertTrue(parsed.Labels.Count == 1, "Expected one OCR label.");
    AssertEqual(1, parsed.Labels[0].PageIndex);
}

static void TestPreprocessing()
{
    using var signature = CreateSignatureImage(500, 180);
    var preprocessor = new SignaturePreprocessor();
    using var processed = preprocessor.Preprocess(signature, new SignatureVerificationOptions(), "test");
    var extractor = new FeatureExtractor();
    var metrics = extractor.Extract(processed, processed.CleanBinary, 90);
    AssertTrue(metrics.InkPixelCount > 100, "Expected ink pixels.");
    AssertTrue(metrics.BoundingBoxWidth > metrics.BoundingBoxHeight, "Expected signature-like aspect ratio.");
}

static void TestSafeErrorJson()
{
    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignatures("not base64", string.Empty, SampleReferences(CreateSignatureBase64()), "{}");
    using var doc = JsonDocument.Parse(json);
    AssertEqual("Error", doc.RootElement.GetProperty("overallDecision").GetString());
    AssertEqual("INVALID_BASE64_DOCUMENT", doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
}

static void TestKnownZoneEndToEnd()
{
    using var page = new Mat(new Size(1000, 700), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var documentBase64 = Convert.ToBase64String(pageBytes);
    var referencesJson = SampleReferences(CreateSignatureBase64());
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "knownZones": [
        {
          "signatureId": "applicant_signature",
          "pageIndex": 0,
          "x": 100,
          "y": 220,
          "width": 500,
          "height": 220,
          "coordinateSystem": "pixels"
        }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false,
        "minimumInkDensityPercent": 0.2,
        "maximumInkDensityPercent": 35
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignatures(documentBase64, string.Empty, referencesJson, optionsJson);
    using var doc = JsonDocument.Parse(json);
    AssertTrue(doc.RootElement.GetProperty("signatureResults").GetArrayLength() == 1, "Expected one signature result.");
    AssertTrue(doc.RootElement.GetProperty("signatureCountDetected").GetInt32() == 1, "Expected detected signature.");
    AssertTrue(doc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").TryGetProperty("referenceComparisons", out _), "Expected comparison audit.");
    AssertEqual("synthetic_reference.png", doc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").GetProperty("referenceComparisons")[0].GetProperty("referenceFileName").GetString());
}

static void TestCustomThresholdParameters()
{
    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignaturesWithThresholds(
        "not base64",
        string.Empty,
        SampleReferences(CreateSignatureBase64()),
        "{}",
        "91",
        "81",
        "61");

    using var doc = JsonDocument.Parse(json);
    AssertEqual("Error", doc.RootElement.GetProperty("overallDecision").GetString());

    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "thresholds": {
        "matched": 91,
        "probableMatch": 81,
        "reviewRequired": 61
      }
    }
    """;

    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var result = wrapper.VerifySignature(Convert.ToBase64String(pageBytes), CreateSignatureBase64(), string.Empty, optionsJson);
    using var resultDoc = JsonDocument.Parse(result);
    var thresholds = resultDoc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").GetProperty("thresholdsUsed");
    AssertEqual(91, thresholds.GetProperty("matched").GetDouble());
    AssertEqual(81, thresholds.GetProperty("probableMatch").GetDouble());
    AssertEqual(61, thresholds.GetProperty("reviewRequired").GetDouble());
}

static void TestDecimalThresholdParameters()
{
    var wrapper = new SignatureVerificationWrapper();
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var result = wrapper.VerifySignatureWithThresholds(
        Convert.ToBase64String(pageBytes),
        CreateSignatureBase64(),
        string.Empty,
        "91.5",
        "81.25",
        "61.75");

    using var resultDoc = JsonDocument.Parse(result);
    var thresholds = resultDoc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").GetProperty("thresholdsUsed");
    AssertEqual(91.5, thresholds.GetProperty("matched").GetDouble());
    AssertEqual(81.25, thresholds.GetProperty("probableMatch").GetDouble());
    AssertEqual(61.75, thresholds.GetProperty("reviewRequired").GetDouble());
}

static void TestRoleConfigurationFiltersExpectedSignatures()
{
    using var page = new Mat(new Size(1000, 700), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "knownZones": [
        { "signatureId": "applicant_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" },
        { "signatureId": "witness_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" },
        { "signatureId": "authorised_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignaturesWithSignatureRoleMode(Convert.ToBase64String(pageBytes), string.Empty, SampleReferencesForRoles(CreateSignatureBase64(), "applicant_signature", "witness_signature", "authorised_signature"), optionsJson, "ApplicantOnly");
    using var doc = JsonDocument.Parse(json);

    AssertEqual(1, doc.RootElement.GetProperty("signatureCountExpected").GetInt32());
    AssertEqual(1, doc.RootElement.GetProperty("signatureResults").GetArrayLength());
    AssertEqual("applicant_signature", doc.RootElement.GetProperty("signatureResults")[0].GetProperty("signatureId").GetString());
}

static void TestRoleConfigurationReportsMissingConfiguredReferences()
{
    using var page = new Mat(new Size(1000, 700), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "knownZones": [
        { "signatureId": "applicant_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" },
        { "signatureId": "witness_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignaturesWithSignatureRoleMode(Convert.ToBase64String(pageBytes), string.Empty, SampleReferencesForRoles(CreateSignatureBase64(), "applicant_signature", "witness_signature"), optionsJson, "ApplicantWitnessAuthorised");
    using var doc = JsonDocument.Parse(json);
    var results = doc.RootElement.GetProperty("signatureResults").EnumerateArray().ToList();
    var authorised = results.Single(r => r.GetProperty("signatureId").GetString() == "authorised_signature");

    AssertTrue(doc.RootElement.GetProperty("overallDecision").GetString() != "Error", "Expected configured missing reference to be reported per signature.");
    AssertEqual(3, doc.RootElement.GetProperty("signatureCountExpected").GetInt32());
    AssertTrue(authorised.GetProperty("warnings").EnumerateArray().Any(w => w.GetString() == "REFERENCE_SIGNATURE_MISSING"), "Expected missing authorised reference warning.");
}

static void TestRoleConfigurationNormalizesKnownZoneIds()
{
    using var page = new Mat(new Size(1000, 700), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "knownZones": [
        { "signatureId": "Applicant", "pageIndex": 0, "x": 10, "y": 31, "width": 55, "height": 40, "coordinateSystem": "percent" }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false,
        "minimumInkDensityPercent": 0.1,
        "maximumInkDensityPercent": 60
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignaturesWithSignatureRoleMode(Convert.ToBase64String(pageBytes), string.Empty, SampleReferencesForRoles(CreateSignatureBase64(), "Applicant"), optionsJson, "ApplicantOnly");
    using var doc = JsonDocument.Parse(json);
    var result = doc.RootElement.GetProperty("signatureResults")[0];

    AssertEqual("applicant_signature", result.GetProperty("signatureId").GetString());
    AssertTrue(result.GetProperty("signatureDetected").GetBoolean(), "Expected normalized known zone to detect the applicant signature.");
}

static void TestInvalidOptionsAreNormalized()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "thresholds": {
        "matched": 40,
        "probableMatch": 90,
        "reviewRequired": 150
      },
      "weights": {
        "geometryScore": -1,
        "inkDensityScore": 0,
        "densityGridScore": 0,
        "contourScore": 0,
        "skeletonScore": 0,
        "structuralSimilarityScore": 0,
        "connectedComponentScore": 0
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignature(Convert.ToBase64String(pageBytes), CreateSignatureBase64(), string.Empty, optionsJson);
    using var doc = JsonDocument.Parse(json);

    var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? string.Empty).ToList();
    AssertTrue(warnings.Any(w => w.Contains("Thresholds were invalid", StringComparison.OrdinalIgnoreCase)), "Expected threshold normalization warning.");
    AssertTrue(warnings.Any(w => w.Contains("Score weights were invalid", StringComparison.OrdinalIgnoreCase)), "Expected weight normalization warning.");
    var thresholds = doc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").GetProperty("thresholdsUsed");
    AssertEqual(85, thresholds.GetProperty("matched").GetDouble());
    AssertEqual(70, thresholds.GetProperty("probableMatch").GetDouble());
    AssertEqual(55, thresholds.GetProperty("reviewRequired").GetDouble());
}

static void TestMultipleSignaturesUseSeparateDetectedRegions()
{
    using var page = new Mat(new Size(1300, 650), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var left = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(left);
    }

    using (var right = new Mat(page, new Rect(760, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(right);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "detection": {
        "useKnownZones": false,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": true,
        "maxCandidateRegionsPerSignature": 4,
        "candidatePoolMultiplier": 4,
        "minimumInkDensityPercent": 0.1,
        "maximumInkDensityPercent": 60
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignatures(Convert.ToBase64String(pageBytes), string.Empty, SampleReferencesForRoles(CreateSignatureBase64(), "applicant_signature", "witness_signature"), optionsJson);
    using var doc = JsonDocument.Parse(json);
    var results = doc.RootElement.GetProperty("signatureResults").EnumerateArray().ToList();

    AssertEqual(2, results.Count);
    AssertEqual(2, doc.RootElement.GetProperty("signatureCountDetected").GetInt32());
    var first = ReadRegion(results[0].GetProperty("detectedRegion"));
    var second = ReadRegion(results[1].GetProperty("detectedRegion"));
    AssertTrue(!RegionsOverlap(first, second), "Expected applicant and witness to use separate detected regions.");
}

static void TestDuplicateCandidateRegionsAreNotReused()
{
    using var page = new Mat(new Size(1000, 700), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(420, 160);
    using (var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "knownZones": [
        { "signatureId": "applicant_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" },
        { "signatureId": "witness_signature", "pageIndex": 0, "x": 100, "y": 220, "width": 500, "height": 220, "coordinateSystem": "pixels" }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignatures(Convert.ToBase64String(pageBytes), string.Empty, SampleReferencesForRoles(CreateSignatureBase64(), "applicant_signature", "witness_signature"), optionsJson);
    using var doc = JsonDocument.Parse(json);
    var results = doc.RootElement.GetProperty("signatureResults").EnumerateArray().ToList();
    var unresolved = results.Single(result => !result.GetProperty("signatureDetected").GetBoolean());

    AssertEqual(1, doc.RootElement.GetProperty("signatureCountDetected").GetInt32());
    AssertTrue(unresolved.GetProperty("warnings").EnumerateArray().Any(w => w.GetString() == "NO_UNIQUE_SIGNATURE_REGION"), "Expected duplicate region reuse to be rejected.");
}

static void TestOperationalAudit()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var wrapper = new SignatureVerificationWrapper();
    var optionsJson = """
    {
      "correlationId": "audit-success-1",
      "inputDocumentType": "Image",
      "detection": {
        "useKnownZones": false,
        "useOcrAnchors": false,
        "useBoxDetection": true,
        "useInkRegionDetection": true
      }
    }
    """;

    var json = wrapper.VerifySignatures(Convert.ToBase64String(pageBytes), string.Empty, SampleReferences(CreateSignatureBase64()), optionsJson);
    using var doc = JsonDocument.Parse(json);
    var audit = doc.RootElement.GetProperty("operationalAudit");
    AssertEqual("audit-success-1", audit.GetProperty("correlationId").GetString());
    AssertEqual("Completed", audit.GetProperty("status").GetString());
    AssertTrue(audit.GetProperty("durationMs").GetDouble() >= 0, "Expected non-negative duration.");
    AssertEqual(1, audit.GetProperty("referenceSetCount").GetInt32());
    AssertEqual(1, audit.GetProperty("referenceImageCount").GetInt32());

    var errorJson = wrapper.VerifySignatures("not base64", string.Empty, SampleReferences(CreateSignatureBase64()), "{\"correlationId\":\"audit-error-1\"}");
    using var errorDoc = JsonDocument.Parse(errorJson);
    var errorAudit = errorDoc.RootElement.GetProperty("operationalAudit");
    AssertEqual("audit-error-1", errorAudit.GetProperty("correlationId").GetString());
    AssertEqual("Error", errorAudit.GetProperty("status").GetString());
    AssertEqual("INVALID_BASE64_DOCUMENT", errorAudit.GetProperty("errorCode").GetString());
}

static void TestStructuredWrapperRequest()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var wrapper = new SignatureVerificationWrapper();
    var request = JsonSerializer.Serialize(new
    {
        correlationId = "ta-request-1",
        documentBase64 = Convert.ToBase64String(pageBytes),
        ocrLayoutJson = string.Empty,
        referenceSignaturesJson = SampleReferences(CreateSignatureBase64()),
        optionsJson = """
        {
          "inputDocumentType": "Image",
          "detection": {
            "useKnownZones": false,
            "useOcrAnchors": false,
            "useBoxDetection": true,
            "useInkRegionDetection": true
          }
        }
        """,
        matchedThreshold = "79",
        probableMatchThreshold = "69",
        reviewRequiredThreshold = "50",
        signatureRoleMode = "ApplicantOnly",
        signatureMappings = new[]
        {
            new
            {
                signatureId = "applicant_signature",
                partyId = "0032",
                partyName = "Synthetic Applicant",
                referenceSetId = "SIGNER_0032_ENGLISH",
                expectedSignerId = "SIGNER_0032_ENGLISH",
                source = "TotalAgilityRequest"
            }
        }
    });

    var json = wrapper.VerifySignaturesRequest(request);
    using var doc = JsonDocument.Parse(json);
    AssertTrue(doc.RootElement.GetProperty("overallDecision").GetString() != "Error", "Expected structured request to run.");
    AssertEqual("ta-request-1", doc.RootElement.GetProperty("operationalAudit").GetProperty("correlationId").GetString());
    var thresholds = doc.RootElement.GetProperty("signatureResults")[0].GetProperty("audit").GetProperty("thresholdsUsed");
    AssertEqual(79, thresholds.GetProperty("matched").GetDouble());
    AssertEqual(69, thresholds.GetProperty("probableMatch").GetDouble());
    AssertEqual(50, thresholds.GetProperty("reviewRequired").GetDouble());
    var mapping = doc.RootElement.GetProperty("signatureResults")[0].GetProperty("mapping");
    AssertEqual("0032", mapping.GetProperty("partyId").GetString());
    AssertEqual("SIGNER_0032_ENGLISH", mapping.GetProperty("referenceSetId").GetString());
    AssertEqual("TotalAgilityRequest", mapping.GetProperty("source").GetString());

    var invalid = wrapper.VerifySignaturesRequest("{\"correlationId\":\"ta-bad\"}");
    using var invalidDoc = JsonDocument.Parse(invalid);
    AssertEqual("Error", invalidDoc.RootElement.GetProperty("overallDecision").GetString());
    AssertEqual("REQUEST_INVALID", invalidDoc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    AssertEqual("ta-bad", invalidDoc.RootElement.GetProperty("operationalAudit").GetProperty("correlationId").GetString());
}

static void TestStorageMapperPreservesResultAuditData()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var optionsJson = """
    {
      "correlationId": "db-map-1",
      "inputDocumentType": "Image",
      "saveDebugImages": true,
      "debugOutputFolder": "C:\\Temp\\SignatureVerification\\MapperTest\\debug",
      "knownZones": [
        {
          "signatureId": "applicant_signature",
          "pageIndex": 0,
          "x": 0,
          "y": 0,
          "width": 420,
          "height": 160,
          "coordinateSystem": "pixels"
        }
      ],
      "detection": {
        "useKnownZones": true,
        "useOcrAnchors": false,
        "useBoxDetection": false,
        "useInkRegionDetection": false
      }
    }
    """;

    var wrapper = new SignatureVerificationWrapper();
    var json = wrapper.VerifySignatures(Convert.ToBase64String(pageBytes), string.Empty, SampleReferences(CreateSignatureBase64()), optionsJson);
    var record = JsonVerificationResultMapper.FromJson(
        json,
        documentName: "MapperTest",
        sourceDocumentPath: @"C:\Temp\SignatureVerification\Input\MapperTest.png",
        resultPath: @"C:\Temp\SignatureVerification\Output\MapperTest\result.json");

    AssertEqual("db-map-1", record.CorrelationId);
    AssertEqual("MapperTest", record.DocumentName);
    AssertEqual(1, record.SignatureCases.Count);
    AssertEqual(record.DocumentResultId, record.SignatureCases[0].DocumentResultId);
    AssertTrue(record.ResultJson.Length > 1000, "Expected full result JSON.");
    AssertTrue(record.SignatureCases[0].SignatureResultJson.Length > 100, "Expected signature result JSON.");
    AssertTrue(record.SignatureCases[0].ReferenceComparisons.Count == 1, "Expected one reference comparison.");
    AssertEqual("synthetic_reference.png", record.SignatureCases[0].ReferenceComparisons[0].ReferenceFileName);
}

static void TestWrapperDelegatesToApplicationService()
{
    var fake = new FakeApplicationService();
    var wrapper = new SignatureVerificationWrapper(fake, new FakePdfRenderer());
    var resultJson = wrapper.VerifySignatures("doc", "", SampleReferences(CreateSignatureBase64()), "{}");

    AssertTrue(fake.VerifyToJsonCalled, "Expected wrapper to delegate to application service.");
    using var doc = JsonDocument.Parse(resultJson);
    AssertEqual("Matched", doc.RootElement.GetProperty("overallDecision").GetString());
}

static void TestApplicationServiceReturnsStableJson()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);

    var service = new VerificationApplicationService();
    var json = service.VerifyToJson(new SignatureVerificationRequest(
        Convert.ToBase64String(pageBytes),
        string.Empty,
        SampleReferences(CreateSignatureBase64()),
        """{"inputDocumentType":"Image"}"""));

    using var doc = JsonDocument.Parse(json);
    AssertTrue(doc.RootElement.TryGetProperty("documentResultId", out _), "Expected camelCase public result contract.");
    AssertTrue(doc.RootElement.TryGetProperty("operationalAudit", out _), "Expected operational audit in JSON.");
}

static void TestEngineLogsSanitizedInternalErrors()
{
    var logger = new InMemoryVerificationLogger();
    var service = new VerificationApplicationService(logger: logger);
    var imageBytes = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });

    var result = service.Verify(new SignatureVerificationRequest(
        imageBytes,
        string.Empty,
        SampleReferences(CreateSignatureBase64()),
        """{"inputDocumentType":"Image"}"""));

    AssertEqual("Error", result.OverallDecision);
    AssertTrue(logger.Entries.Any(e => e.Level == "Error" && e.Code == "UNSUPPORTED_IMAGE_FORMAT"), "Expected internal logger to capture sanitized decode failure.");
}

static void TestDetectionProfileControlsCandidateScoring()
{
    using var page = new Mat(new Size(600, 300), MatType.CV_8UC1, Scalar.White);
    using var signature = CreateSignatureImage(260, 100);
    using (var roi = new Mat(page, new Rect(100, 90, signature.Width, signature.Height)))
    {
        signature.CopyTo(roi);
    }

    var referenceSet = new ReferenceSignatureSet
    {
        SignatureId = "applicant_signature",
        ExpectedLabels = new List<string> { "Applicant Signature" }
    };
    var options = new SignatureVerificationOptions
    {
        KnownZones = new List<KnownSignatureZone>
        {
            new()
            {
                SignatureId = "applicant_signature",
                PageIndex = 0,
                X = 80,
                Y = 70,
                Width = 320,
                Height = 140,
                CoordinateSystem = "pixels"
            }
        }
    };

    var defaultCandidate = new SignatureDetector().DetectCandidates(new[] { new PageImage(0, page, Array.Empty<byte>(), 300) }, referenceSet, new OcrLayout(), options).Single();
    var lowSourceCandidate = new SignatureDetector(new DetectionProfile { KnownZoneSourceScore = 10 }).DetectCandidates(new[] { new PageImage(0, page, Array.Empty<byte>(), 300) }, referenceSet, new OcrLayout(), options).Single();

    AssertTrue(defaultCandidate.Region.RegionConfidence > lowSourceCandidate.Region.RegionConfidence, "Expected profile source score to influence candidate confidence.");
    defaultCandidate.Crop.Dispose();
    lowSourceCandidate.Crop.Dispose();
}

static void TestSharedJsonOptionsStableContract()
{
    var result = ResultJsonBuilder.CreateError("test", "CODE", "message");
    var json = JsonSerializer.Serialize(result, SignatureJsonOptions.Compact);
    using var doc = JsonDocument.Parse(json);

    AssertTrue(doc.RootElement.TryGetProperty("documentResultId", out _), "Expected camelCase documentResultId.");
    AssertTrue(!doc.RootElement.TryGetProperty("DocumentResultId", out _), "Did not expect PascalCase JSON.");
}

static void TestConcurrentWrapperCalls()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var documentBase64 = Convert.ToBase64String(pageBytes);
    var references = SampleReferences(CreateSignatureBase64());
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "detection": {
        "useKnownZones": false,
        "useOcrAnchors": false,
        "useBoxDetection": true,
        "useInkRegionDetection": true
      }
    }
    """;
    var wrapper = new SignatureVerificationWrapper();
    var failures = new ConcurrentBag<string>();

    Parallel.For(0, 12, index =>
    {
        try
        {
            var json = wrapper.VerifySignatures(documentBase64, string.Empty, references, optionsJson);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("overallDecision").GetString() == "Error")
            {
                failures.Add("Concurrent call returned Error.");
            }

            if (!doc.RootElement.TryGetProperty("operationalAudit", out _))
            {
                failures.Add("Concurrent call did not include operationalAudit.");
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
    });

    AssertTrue(failures.IsEmpty, string.Join("; ", failures));
}

static void TestPerformanceSmokeBudget()
{
    using var page = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", page, out var pageBytes);
    var documentBase64 = Convert.ToBase64String(pageBytes);
    var references = SampleReferences(CreateSignatureBase64());
    var optionsJson = """
    {
      "inputDocumentType": "Image",
      "detection": {
        "useKnownZones": false,
        "useOcrAnchors": false,
        "useBoxDetection": true,
        "useInkRegionDetection": true
      }
    }
    """;
    var wrapper = new SignatureVerificationWrapper();
    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < 5; i++)
    {
        var json = wrapper.VerifySignatures(documentBase64, string.Empty, references, optionsJson);
        using var doc = JsonDocument.Parse(json);
        AssertTrue(doc.RootElement.GetProperty("overallDecision").GetString() != "Error", "Expected performance smoke call to succeed.");
        AssertTrue(doc.RootElement.GetProperty("operationalAudit").GetProperty("durationMs").GetDouble() >= 0, "Expected duration metric.");
    }

    stopwatch.Stop();
    AssertTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Performance smoke exceeded budget: {stopwatch.Elapsed}.");
}

static Mat CreateSignatureImage(int width, int height)
{
    var image = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.White);
    var points = new[]
    {
        new Point(25, 110),
        new Point(80, 55),
        new Point(135, 125),
        new Point(195, 65),
        new Point(260, 116),
        new Point(335, 82),
        new Point(395, 120)
    };
    Cv2.Polylines(image, new[] { points }, false, Scalar.Black, 5, LineTypes.AntiAlias);
    Cv2.Line(image, new Point(35, 130), new Point(width - 40, 132), Scalar.Black, 2, LineTypes.AntiAlias);
    Cv2.Ellipse(image, new Point(150, 98), new Size(42, 22), 15, 0, 340, Scalar.Black, 3);
    return image;
}

static string CreateSignatureBase64()
{
    using var image = CreateSignatureImage(420, 160);
    Cv2.ImEncode(".png", image, out var bytes);
    return Convert.ToBase64String(bytes);
}

static string SampleReferences(string signatureBase64) => $$"""
{
  "referenceSets": [
    {
      "signatureId": "applicant_signature",
      "displayName": "Applicant Signature",
      "expectedLabels": ["Applicant Signature", "Signature", "Signed By"],
      "referenceImages": [
        {
          "referenceId": "applicant_ref_1",
          "sourceFileName": "synthetic_reference.png",
          "imageBase64": "{{signatureBase64}}"
        }
      ]
    }
  ]
}
""";

static string SampleReferencesForRoles(string signatureBase64, params string[] roleIds)
{
    var sets = roleIds.Select((role, index) => $$"""
    {
      "signatureId": "{{role}}",
      "displayName": "{{role}}",
      "expectedLabels": ["{{role}}"],
      "referenceImages": [
        {
          "referenceId": "{{role}}_ref_{{index + 1}}",
          "sourceFileName": "{{role}}.png",
          "imageBase64": "{{signatureBase64}}"
        }
      ]
    }
    """);

    return $$"""
    {
      "referenceSets": [
    {{string.Join(",\n", sets)}}
      ]
    }
    """;
}

static Rect ReadRegion(JsonElement region) => new(
    region.GetProperty("x").GetInt32(),
    region.GetProperty("y").GetInt32(),
    region.GetProperty("width").GetInt32(),
    region.GetProperty("height").GetInt32());

static bool RegionsOverlap(Rect first, Rect second) =>
    first.X < second.X + second.Width &&
    first.X + first.Width > second.X &&
    first.Y < second.Y + second.Height &&
    first.Y + first.Height > second.Y;

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
    }
}

internal sealed class FakeApplicationService : IVerificationApplicationService
{
    public bool VerifyToJsonCalled { get; private set; }

    public VerificationResult Verify(SignatureVerificationRequest request) => new()
    {
        DocumentResultId = "fake-result",
        EngineVersion = "test",
        InputDocumentType = "Image",
        OverallDecision = "Matched",
        OverallConfidence = 100,
        SignatureCountExpected = 0,
        SignatureCountDetected = 0
    };

    public string VerifyToJson(SignatureVerificationRequest request)
    {
        VerifyToJsonCalled = true;
        return StaticSignatureVerificationEngine.ToJson(Verify(request));
    }
}

internal sealed class FakePdfRenderer : IPdfPageRenderer
{
    public IReadOnlyList<RenderedPage> RenderPdfToImages(byte[] pdfBytes, PdfRenderOptions options) =>
        Array.Empty<RenderedPage>();
}
