using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCvSharp;

namespace StaticSignatureVerification.Core;

/// <summary>
/// Coordinates document decoding, signature detection, preprocessing, comparison, and audit result generation.
/// </summary>
public sealed class StaticSignatureVerificationEngine
{
    private const string EngineVersion = "1.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly DocumentInputDetector _inputDetector = new();
    private readonly OcrLayoutParser _ocrParser = new();
    private readonly SignatureDetector _detector = new();
    private readonly SignaturePreprocessor _preprocessor = new();
    private readonly FeatureExtractor _featureExtractor = new();
    private readonly SimilarityScorer _scorer = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly ReasoningBuilder _reasoningBuilder = new();
    private readonly DebugImageWriter _debugImageWriter = new();

    /// <summary>
    /// Runs visual signature comparison for one document and one or more reference signature sets.
    /// </summary>
    public VerificationResult Verify(
        string documentBase64,
        string? ocrLayoutJson,
        string referenceSignaturesJson,
        string? optionsJson,
        IPdfPageRenderer? pdfRenderer = null)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = ResultJsonBuilder.CreateEmpty(EngineVersion);
        SignatureVerificationOptions? options = null;
        ReferenceSignatureInput? references = null;

        try
        {
            options = SignatureVerificationOptions.FromJson(optionsJson);
            options.ValidateAndNormalize(result.Warnings);
        }
        catch (JsonException)
        {
            return CompleteOperationalAudit(
                ResultJsonBuilder.CreateError(EngineVersion, "OPTIONS_JSON_INVALID", "The options JSON could not be parsed."),
                startedUtc,
                stopwatch,
                options,
                references,
                null);
        }

        try
        {
            references = ReferenceSignatureInput.FromJson(referenceSignaturesJson);
            references = options.ApplySignatureRoleConfiguration(references);
        }
        catch (JsonException)
        {
            return CompleteOperationalAudit(
                ResultJsonBuilder.CreateError(EngineVersion, "REFERENCE_SIGNATURE_MISSING", "The reference signatures JSON could not be parsed."),
                startedUtc,
                stopwatch,
                options,
                references,
                null);
        }

        if (references.ReferenceSets.Count == 0 || references.ReferenceSets.All(r => r.ReferenceImages.Count == 0))
        {
            return CompleteOperationalAudit(
                ResultJsonBuilder.CreateError(EngineVersion, "REFERENCE_SIGNATURE_MISSING", "At least one reference signature image is required."),
                startedUtc,
                stopwatch,
                options,
                references,
                null);
        }

        byte[] documentBytes;
        try
        {
            documentBytes = Convert.FromBase64String(documentBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return CompleteOperationalAudit(
                ResultJsonBuilder.CreateError(EngineVersion, "INVALID_BASE64_DOCUMENT", "The documentBase64 value is not valid Base64."),
                startedUtc,
                stopwatch,
                options,
                references,
                null);
        }

        result.DocumentResultId = Guid.NewGuid().ToString("N");
        result.SignatureCountExpected = references.ReferenceSets.Count;

        IReadOnlyList<PageImage>? pages = null;
        IReadOnlyDictionary<string, List<ReferenceFeature>>? referenceFeatures = null;
        try
        {
            var inputType = _inputDetector.Detect(documentBytes, options.InputDocumentType);
            result.InputDocumentType = inputType.ToString();
            pages = LoadPages(documentBytes, inputType, options, pdfRenderer, result);
            if (result.Errors.Count > 0)
            {
                result.OverallDecision = "Error";
                return CompleteOperationalAudit(result, startedUtc, stopwatch, options, references, pages);
            }

            var ocr = _ocrParser.Parse(ocrLayoutJson, pages, result.Warnings);
            referenceFeatures = PreprocessReferences(references, options, result.Warnings);

            foreach (var signatureResult in VerifyExpectedSignaturesGlobal(references.ReferenceSets, referenceFeatures, pages, ocr, options))
            {
                result.SignatureResults.Add(signatureResult);
            }

            result.SignatureCountDetected = result.SignatureResults.Count(r => r.SignatureDetected);
            result.OverallDecision = _decisionEngine.GetOverallDecision(result.SignatureResults);
            result.OverallConfidence = result.SignatureResults.Count == 0
                ? 0
                : Math.Round(result.SignatureResults.Average(r => r.Confidence), 2);

            if (result.OverallDecision == "ReviewRequired")
            {
                result.Warnings.Add("At least one signature requires human review.");
            }

            return CompleteOperationalAudit(result, startedUtc, stopwatch, options, references, pages);
        }
        catch (Exception)
        {
            return CompleteOperationalAudit(
                ResultJsonBuilder.CreateError(EngineVersion, "UNHANDLED_EXCEPTION", "An unexpected error occurred while verifying signatures."),
                startedUtc,
                stopwatch,
                options,
                references,
                pages);
        }
        finally
        {
            if (referenceFeatures is not null)
            {
                DisposeReferenceFeatures(referenceFeatures);
            }

            if (pages is not null)
            {
                DisposePages(pages);
            }
        }
    }

    /// <summary>
    /// Serializes a verification result using the public JSON contract.
    /// </summary>
    public static string ToJson(VerificationResult result) => JsonSerializer.Serialize(result, JsonOptions);

    private static VerificationResult CompleteOperationalAudit(
        VerificationResult result,
        DateTimeOffset startedUtc,
        Stopwatch stopwatch,
        SignatureVerificationOptions? options,
        ReferenceSignatureInput? references,
        IReadOnlyList<PageImage>? pages)
    {
        stopwatch.Stop();
        var finishedUtc = DateTimeOffset.UtcNow;
        var firstError = result.Errors.FirstOrDefault();
        result.OperationalAudit = new OperationalAudit
        {
            CorrelationId = NormalizeCorrelationId(options?.CorrelationId) ?? result.DocumentResultId,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            Status = firstError is null && result.OverallDecision != "Error" ? "Completed" : "Error",
            ErrorCode = firstError?.Code,
            InputDocumentType = result.InputDocumentType,
            PagesLoaded = pages?.Count ?? result.PdfRenderInfo?.PagesRendered ?? 0,
            ReferenceSetCount = references?.ReferenceSets.Count ?? result.SignatureCountExpected,
            ReferenceImageCount = references?.ReferenceSets.Sum(set => set.ReferenceImages.Count) ?? 0,
            SignatureCountExpected = result.SignatureCountExpected,
            SignatureCountDetected = result.SignatureCountDetected,
            WarningsCount = result.Warnings.Count + result.SignatureResults.Sum(signature => signature.Warnings.Count),
            ErrorsCount = result.Errors.Count,
            DebugImagesSaved = result.SignatureResults.Sum(signature => signature.Audit?.DebugImages?.Count ?? 0),
            DebugOutputEnabled = options?.SaveDebugImages == true
        };

        return result;
    }

    private static string? NormalizeCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private static void DisposeReferenceFeatures(IReadOnlyDictionary<string, List<ReferenceFeature>> referenceFeatures)
    {
        foreach (var reference in referenceFeatures.Values.SelectMany(v => v))
        {
            reference.Processed.Dispose();
        }
    }

    private static void DisposePages(IEnumerable<PageImage> pages)
    {
        foreach (var page in pages)
        {
            page.Image.Dispose();
        }
    }

    private IReadOnlyList<PageImage> LoadPages(byte[] documentBytes, InputDocumentType inputType, SignatureVerificationOptions options, IPdfPageRenderer? pdfRenderer, VerificationResult result)
    {
        if (inputType == InputDocumentType.PDF)
        {
            if (pdfRenderer is null)
            {
                result.Errors.Add(new ErrorDetail("PDF_RENDERING_FAILED", "PDF input requires a configured PDF page renderer."));
                return Array.Empty<PageImage>();
            }

            try
            {
                var pdfOptions = options.ToPdfRenderOptions();
                var rendered = pdfRenderer.RenderPdfToImages(documentBytes, pdfOptions);
                result.PdfRenderInfo = new PdfRenderInfo
                {
                    Renderer = pdfOptions.Renderer,
                    Dpi = pdfOptions.Dpi,
                    PagesRendered = rendered.Count,
                    PageIndexesRendered = rendered.Select(p => p.PageIndex).ToList(),
                    Warnings = new List<string>()
                };

                return rendered.Select(p =>
                {
                    var mat = DocumentInputDetector.DecodeImageBytes(p.ImageBytes, ImreadModes.Grayscale);
                    return new PageImage(p.PageIndex, mat, p.ImageBytes, p.Dpi);
                }).ToList();
            }
            catch (SignatureVerificationException ex)
            {
                result.Errors.Add(new ErrorDetail(ex.Code, ex.Message));
                return Array.Empty<PageImage>();
            }
            catch (Exception)
            {
                result.Errors.Add(new ErrorDetail("PDF_RENDERING_FAILED", "The PDF could not be rendered to images."));
                return Array.Empty<PageImage>();
            }
        }

        try
        {
            var mat = DocumentInputDetector.DecodeImageBytes(documentBytes, ImreadModes.Grayscale);
            return new[] { new PageImage(0, mat, documentBytes, options.Dpi) };
        }
        catch (Exception)
        {
            result.Errors.Add(new ErrorDetail("UNSUPPORTED_IMAGE_FORMAT", "The image could not be decoded by OpenCV."));
            return Array.Empty<PageImage>();
        }
    }

    private Dictionary<string, List<ReferenceFeature>> PreprocessReferences(ReferenceSignatureInput references, SignatureVerificationOptions options, List<string> warnings)
    {
        var output = new Dictionary<string, List<ReferenceFeature>>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in references.ReferenceSets)
        {
            var list = new List<ReferenceFeature>();
            foreach (var reference in set.ReferenceImages)
            {
                try
                {
                    var bytes = Convert.FromBase64String(reference.ImageBase64);
                    using var image = DocumentInputDetector.DecodeImageBytes(bytes, ImreadModes.Grayscale);
                    var processed = _preprocessor.Preprocess(image, options, reference.ReferenceId);
                    var metrics = _featureExtractor.Extract(processed, processed.CleanBinary, 100);
                    list.Add(new ReferenceFeature(reference.ReferenceId, reference.SourceFileName, reference.SourceFilePath, processed, metrics));
                }
                catch (Exception)
                {
                    warnings.Add($"Reference image '{reference.ReferenceId}' could not be decoded and was skipped.");
                }
            }

            output[set.SignatureId] = list;
        }

        return output;
    }

    private List<SignatureResult> VerifyExpectedSignaturesGlobal(
        IReadOnlyList<ReferenceSignatureSet> referenceSets,
        IReadOnlyDictionary<string, List<ReferenceFeature>> referenceFeatures,
        IReadOnlyList<PageImage> pages,
        OcrLayout ocr,
        SignatureVerificationOptions options)
    {
        var plans = new List<SignaturePlan>();
        var resultsBySignatureId = new Dictionary<string, SignatureResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var referenceSet in referenceSets)
        {
            var signatureResult = CreateInitialSignatureResult(referenceSet);
            resultsBySignatureId[referenceSet.SignatureId] = signatureResult;

            if (!referenceFeatures.TryGetValue(referenceSet.SignatureId, out var featureSet) || featureSet.Count == 0)
            {
                signatureResult.Warnings.Add("REFERENCE_SIGNATURE_MISSING");
                signatureResult.Audit = AuditResult.Empty(options);
                continue;
            }

            var candidates = _detector.DetectCandidates(pages, referenceSet, ocr, options);
            signatureResult.CandidateRegions = candidates.Select(c => c.Region).ToList();
            if (candidates.Count == 0)
            {
                signatureResult.Warnings.Add("NO_SIGNATURE_DETECTED");
                signatureResult.Audit = AuditResult.Empty(options);
                continue;
            }

            var evaluations = EvaluateCandidates(candidates, featureSet, Array.Empty<DetectedRegion>(), options);
            plans.Add(new SignaturePlan(referenceSet, featureSet, candidates, evaluations));
        }

        try
        {
            var assignments = AssignCandidatesGlobally(plans);
            foreach (var plan in plans)
            {
                if (!assignments.TryGetValue(plan.ReferenceSet.SignatureId, out var selected))
                {
                    var unresolvedResult = resultsBySignatureId[plan.ReferenceSet.SignatureId];
                    unresolvedResult.Warnings.Add("NO_UNIQUE_SIGNATURE_REGION");
                    unresolvedResult.Audit = AuditResult.Empty(options);
                    continue;
                }

                resultsBySignatureId[plan.ReferenceSet.SignatureId] = BuildSignatureResultFromEvaluation(plan.ReferenceSet, plan.Candidates, selected, options, pages);
            }

            return referenceSets.Select(set => resultsBySignatureId[set.SignatureId]).ToList();
        }
        finally
        {
            DisposeCandidatePlans(plans);
        }
    }

    private static void DisposeCandidatePlans(IEnumerable<SignaturePlan> plans)
    {
        foreach (var plan in plans)
        {
            foreach (var evaluation in plan.Evaluations)
            {
                evaluation.ProcessedQuery.Dispose();
            }

            foreach (var candidate in plan.Candidates)
            {
                candidate.Crop.Dispose();
            }
        }
    }

    private Dictionary<string, CandidateEvaluation> AssignCandidatesGlobally(IReadOnlyList<SignaturePlan> plans)
    {
        var assignments = new Dictionary<string, CandidateEvaluation>(StringComparer.OrdinalIgnoreCase);
        var usedRegions = new List<DetectedRegion>();
        var remaining = plans.Where(p => p.Evaluations.Count > 0).ToList();

        while (remaining.Count > 0)
        {
            var best = remaining
                .SelectMany(plan => plan.Evaluations
                    .Where(e => !OverlapsAnyUsedRegion(e.Candidate.Region, usedRegions))
                    .Select(e => new { Plan = plan, Evaluation = e }))
                .OrderByDescending(x => x.Evaluation.Rank)
                .FirstOrDefault();

            if (best is null)
            {
                break;
            }

            assignments[best.Plan.ReferenceSet.SignatureId] = best.Evaluation;
            usedRegions.Add(best.Evaluation.Candidate.Region);
            remaining.Remove(best.Plan);
        }

        return assignments;
    }

    private SignatureResult BuildSignatureResultFromEvaluation(
        ReferenceSignatureSet referenceSet,
        IReadOnlyList<DetectedCandidate> candidates,
        CandidateEvaluation selected,
        SignatureVerificationOptions options,
        IReadOnlyList<PageImage> pages)
    {
        var signatureResult = CreateInitialSignatureResult(referenceSet);
        signatureResult.CandidateRegions = candidates.Select(c => c.Region).ToList();

        var bestCandidate = selected.Candidate;
        var processedQuery = selected.ProcessedQuery;
        var queryMetrics = selected.QueryMetrics;

        if (LooksLikeEmptySignatureRegion(queryMetrics))
        {
            signatureResult.SignatureDetected = false;
            signatureResult.DetectedRegion = bestCandidate.Region;
            signatureResult.SignatureQuality = "Missing";
            signatureResult.Decision = "NoSignatureDetected";
            signatureResult.IsMatched = false;
            signatureResult.ReviewRequired = true;
            signatureResult.Confidence = 0;
            signatureResult.Reasoning = _reasoningBuilder.Build("NoSignatureDetected", null);
            signatureResult.Warnings.Add("NO_SIGNATURE_DETECTED");
            signatureResult.Audit = new AuditResult
            {
                FinalScore = 0,
                QuerySignatureMetrics = queryMetrics,
                ThresholdsUsed = options.Thresholds,
                WeightsUsed = options.Weights,
                Preprocessing = processedQuery.Audit,
                DebugImages = _debugImageWriter.TryWrite(options, pages, referenceSet.SignatureId, bestCandidate, processedQuery, signatureResult.CandidateRegions, signatureResult.Warnings)
            };
            return signatureResult;
        }

        signatureResult.SignatureDetected = true;
        signatureResult.DetectedRegion = bestCandidate.Region;
        signatureResult.SignatureQuality = queryMetrics.TooBlank || queryMetrics.TooSmall ? "Poor" : queryMetrics.NoiseEstimate > 30 ? "Fair" : "Good";

        var comparisons = selected.Comparisons;
        var best = comparisons.OrderByDescending(c => c.QualityAdjustedScore).First();
        foreach (var comparison in comparisons)
        {
            comparison.IsBestMatch = comparison.ReferenceId == best.ReferenceId;
        }

        var decision = _decisionEngine.GetSignatureDecision(best.QualityAdjustedScore, queryMetrics, options);
        signatureResult.BestReferenceId = best.ReferenceId;
        signatureResult.Confidence = Math.Round(best.QualityAdjustedScore, 2);
        signatureResult.Decision = decision.Decision;
        signatureResult.IsMatched = decision.IsMatched;
        signatureResult.ReviewRequired = decision.ReviewRequired;
        signatureResult.Reasoning = _reasoningBuilder.Build(decision.Decision, best);

        if (decision.Decision == "InsufficientQuality")
        {
            signatureResult.Warnings.Add("INSUFFICIENT_SIGNATURE_QUALITY");
        }

        signatureResult.Audit = new AuditResult
        {
            FinalScore = signatureResult.Confidence,
            ScoreBreakdown = best.ToScoreBreakdown(),
            QuerySignatureMetrics = queryMetrics,
            ReferenceComparisons = comparisons.OrderByDescending(c => c.Confidence).ToList(),
            ThresholdsUsed = options.Thresholds,
            WeightsUsed = options.Weights,
            Preprocessing = processedQuery.Audit,
            DebugImages = _debugImageWriter.TryWrite(options, pages, referenceSet.SignatureId, bestCandidate, processedQuery, signatureResult.CandidateRegions, signatureResult.Warnings)
        };

        return signatureResult;
    }

    private SignatureResult CreateInitialSignatureResult(ReferenceSignatureSet referenceSet) => new()
    {
        SignatureId = referenceSet.SignatureId,
        DisplayName = referenceSet.DisplayName ?? referenceSet.SignatureId,
        Mapping = referenceSet.Mapping ?? SignatureMapping.FromReferenceSet(referenceSet),
        SignatureDetected = false,
        Decision = "NoSignatureDetected",
        IsMatched = false,
        ReviewRequired = true,
        Confidence = 0,
        SignatureQuality = "Missing",
        Reasoning = _reasoningBuilder.Build("NoSignatureDetected", null),
        Warnings = new List<string>()
    };

    private List<CandidateEvaluation> EvaluateCandidates(
        IReadOnlyList<DetectedCandidate> candidates,
        IReadOnlyList<ReferenceFeature> featureSet,
        IReadOnlyList<DetectedRegion> usedRegions,
        SignatureVerificationOptions options)
    {
        var evaluations = new List<CandidateEvaluation>();
        foreach (var candidate in candidates)
        {
            ProcessedSignature? processed = null;
            try
            {
                processed = _preprocessor.Preprocess(candidate.Crop, options, candidate.Region.Source);
                var metrics = _featureExtractor.Extract(processed, processed.CleanBinary, candidate.Region.RegionConfidence);
                var comparisons = featureSet
                    .Select(reference => _scorer.Compare(metrics, processed, reference.Metrics, reference, options))
                    .OrderByDescending(c => c.QualityAdjustedScore)
                    .ToList();
                var bestScore = comparisons.FirstOrDefault()?.QualityAdjustedScore ?? 0;
                var reusePenalty = OverlapsAnyUsedRegion(candidate.Region, usedRegions) ? 25 : 0;
                var rank = bestScore + candidate.Region.RegionConfidence * 0.05 - reusePenalty;
                evaluations.Add(new CandidateEvaluation(candidate, processed, metrics, comparisons, rank));
                processed = null;
            }
            finally
            {
                processed?.Dispose();
            }
        }

        return evaluations.OrderByDescending(e => e.Rank).ToList();
    }

    private static bool OverlapsAnyUsedRegion(DetectedRegion candidate, IReadOnlyList<DetectedRegion> usedRegions) =>
        usedRegions.Any(used => SamePageOverlap(candidate, used) >= 0.25);

    private static double SamePageOverlap(DetectedRegion a, DetectedRegion b)
    {
        if (a.PageIndex != b.PageIndex)
        {
            return 0;
        }

        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (intersection <= 0)
        {
            return 0;
        }

        var smallerArea = Math.Min(Math.Max(1, a.Width * a.Height), Math.Max(1, b.Width * b.Height));
        return intersection / (double)smallerArea;
    }

    private static bool LooksLikeEmptySignatureRegion(SignatureMetrics metrics) =>
        metrics.TooBlank ||
        metrics.TooSmall ||
        metrics.LineOrBoxOverlap ||
        (metrics.BoundingBoxHeight < 45 &&
         metrics.FragmentedStrokeIndicator >= 50 &&
         metrics.LargestComponentRatio <= 0.30 &&
         metrics.StrokeThicknessEstimate <= 1.25);
}

public enum InputDocumentType
{
    Unknown,
    PDF,
    Image
}

public sealed record PageImage(int PageIndex, Mat Image, byte[] OriginalBytes, int Dpi);

public interface IPdfPageRenderer
{
    IReadOnlyList<RenderedPage> RenderPdfToImages(byte[] pdfBytes, PdfRenderOptions options);
}

public sealed class RenderedPage
{
    public int PageIndex { get; set; }
    public int WidthPixels { get; set; }
    public int HeightPixels { get; set; }
    public int Dpi { get; set; }
    public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
    public string ImageFormat { get; set; } = "png";
}

public sealed class PdfRenderOptions
{
    public int Dpi { get; set; } = 300;
    public int? PageIndex { get; set; }
    public int MaxPages { get; set; } = 5;
    public bool RenderGrayscale { get; set; } = true;
    public string Renderer { get; set; } = "GhostscriptCommandLine";
    public string? GhostscriptExecutablePath { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public string? TempFolder { get; set; }
    public bool KeepTempFiles { get; set; }
}

public sealed class DocumentInputDetector
{
    public InputDocumentType Detect(byte[] bytes, string? requestedType)
    {
        if (string.Equals(requestedType, "PDF", StringComparison.OrdinalIgnoreCase))
        {
            return InputDocumentType.PDF;
        }

        if (string.Equals(requestedType, "Image", StringComparison.OrdinalIgnoreCase))
        {
            return InputDocumentType.Image;
        }

        return bytes.Length >= 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F'
            ? InputDocumentType.PDF
            : InputDocumentType.Image;
    }

    public static Mat DecodeImageBytes(byte[] bytes, ImreadModes mode)
    {
        var mat = Cv2.ImDecode(bytes, mode);
        if (mat.Empty())
        {
            throw new SignatureVerificationException("UNSUPPORTED_IMAGE_FORMAT", "OpenCV could not decode the supplied image.");
        }

        return mat;
    }
}

public sealed class SignatureDetector
{
    public List<DetectedCandidate> DetectCandidates(IReadOnlyList<PageImage> pages, ReferenceSignatureSet set, OcrLayout ocr, SignatureVerificationOptions options)
    {
        var candidates = new List<DetectedCandidate>();
        var hasConfiguredKnownZone = false;

        if (options.Detection.UseKnownZones)
        {
            foreach (var zone in options.KnownZones.Where(z => string.Equals(z.SignatureId, set.SignatureId, StringComparison.OrdinalIgnoreCase)))
            {
                hasConfiguredKnownZone = true;
                var page = pages.FirstOrDefault(p => p.PageIndex == zone.PageIndex);
                if (page is null)
                {
                    continue;
                }

                var rect = ConvertKnownZoneToPixels(zone, page);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                var cropRect = InsetKnownZoneRect(rect);
                var crop = new Mat(page.Image, cropRect).Clone();
                var density = EstimateInkDensity(crop);
                var confidence = ScoreCandidate(100, density, cropRect);
                candidates.Add(new DetectedCandidate(new DetectedRegion
                {
                    PageIndex = page.PageIndex,
                    X = cropRect.X,
                    Y = cropRect.Y,
                    Width = cropRect.Width,
                    Height = cropRect.Height,
                    CoordinateSystem = "pixels",
                    Source = "KnownZone",
                    AnchorText = set.DisplayName,
                    RegionConfidence = confidence,
                    InkDensityPercent = Math.Round(density, 2),
                    CandidateReason = density >= options.Detection.MinimumInkDensityPercent
                        ? "Known zone contained sufficient signature-like ink."
                        : "Known zone was evaluated but contained low ink density."
                }, crop));
            }
        }

        if (!hasConfiguredKnownZone && options.Detection.UseOcrAnchors && ocr.Labels.Count > 0)
        {
            foreach (var label in ocr.Labels.Where(l => set.ExpectedLabels.Any(e => TextMatches(l.Text, e))))
            {
                var page = pages.FirstOrDefault(p => p.PageIndex == label.PageIndex);
                if (page is null)
                {
                    continue;
                }

                var searchRects = BuildOcrSearchRects(label.Rect, page.Image.Size(), options);
                foreach (var searchRect in searchRects)
                {
                    var rect = FindInkBounds(page.Image, searchRect);
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        rect = searchRect;
                    }

                    rect = ClampRect(rect, page.Image.Width, page.Image.Height);
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        continue;
                    }

                    var crop = new Mat(page.Image, rect).Clone();
                    var density = EstimateInkDensity(crop);
                    var sourceScore = options.Detection.SearchBelowLabel && searchRect.Y >= label.Rect.Bottom ? 85 : 78;
                    candidates.Add(new DetectedCandidate(new DetectedRegion
                    {
                        PageIndex = page.PageIndex,
                        X = rect.X,
                        Y = rect.Y,
                        Width = rect.Width,
                        Height = rect.Height,
                        CoordinateSystem = "pixels",
                        Source = "OcrAnchor",
                        AnchorText = label.Text,
                        RegionConfidence = ScoreCandidate(sourceScore, density, rect),
                        InkDensityPercent = Math.Round(density, 2),
                        CandidateReason = "OCR label matched expected signature text and nearby ink was evaluated."
                    }, crop));
                }
            }
        }

        if (!hasConfiguredKnownZone && options.Detection.UseBoxDetection)
        {
            foreach (var page in pages)
            {
                candidates.AddRange(FindBoxes(page));
            }
        }

        if (!hasConfiguredKnownZone && options.Detection.UseInkRegionDetection)
        {
            foreach (var page in pages)
            {
                candidates.AddRange(FindInkRegions(page));
            }
        }

        var candidateLimit = Math.Max(
            Math.Max(1, options.Detection.MaxCandidateRegionsPerSignature),
            Math.Max(1, options.Detection.MaxCandidateRegionsPerSignature) * Math.Max(1, options.Detection.CandidatePoolMultiplier));

        var eligibleCandidates = candidates
            .Where(c => c.Region.InkDensityPercent >= options.Detection.MinimumInkDensityPercent &&
                        c.Region.InkDensityPercent <= options.Detection.MaximumInkDensityPercent)
            .OrderByDescending(c => CandidateRank(c, options))
            .ToList();

        var selectedCandidates = new List<DetectedCandidate>();
        foreach (var candidate in eligibleCandidates)
        {
            if (selectedCandidates.Count < candidateLimit &&
                !selectedCandidates.Any(selected => CandidateOverlap(candidate.Region, selected.Region) >= 0.85))
            {
                selectedCandidates.Add(candidate);
                continue;
            }

            candidate.Crop.Dispose();
        }

        foreach (var rejected in candidates.Where(candidate => !eligibleCandidates.Any(eligible => ReferenceEquals(eligible, candidate))))
        {
            rejected.Crop.Dispose();
        }

        return selectedCandidates;
    }

    private static double CandidateRank(DetectedCandidate candidate, SignatureVerificationOptions options)
    {
        var pageBonus = options.Detection.PreferLaterPages
            ? Math.Min(candidate.Region.PageIndex, 40) * options.Detection.LaterPagePreferenceWeight
            : 0;
        return candidate.Region.RegionConfidence + pageBonus;
    }

    private static double CandidateOverlap(DetectedRegion a, DetectedRegion b)
    {
        if (a.PageIndex != b.PageIndex)
        {
            return 0;
        }

        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (intersection <= 0)
        {
            return 0;
        }

        var smallerArea = Math.Min(Math.Max(1, a.Width * a.Height), Math.Max(1, b.Width * b.Height));
        return intersection / (double)smallerArea;
    }

    private static bool TextMatches(string actual, string expected)
    {
        var a = NormalizeText(actual);
        var e = NormalizeText(expected);
        if (a.Length < 4 || e.Length < 4)
        {
            return string.Equals(a, e, StringComparison.OrdinalIgnoreCase);
        }

        return a.Contains(e, StringComparison.OrdinalIgnoreCase) || e.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<Rect> BuildOcrSearchRects(Rect label, Size pageSize, SignatureVerificationOptions options)
    {
        var height = Math.Max(label.Height * 4, pageSize.Height / 14);
        var width = Math.Max(label.Width * 4, pageSize.Width / 4);
        if (options.Detection.SearchBelowLabel)
        {
            yield return ClampRect(new Rect(Math.Max(0, label.X - label.Width / 2), label.Bottom + 5, width, height), pageSize.Width, pageSize.Height);
        }

        if (options.Detection.SearchRightOfLabel)
        {
            yield return ClampRect(new Rect(label.Right + 10, Math.Max(0, label.Y - label.Height), width, height), pageSize.Width, pageSize.Height);
        }
    }

    private static IEnumerable<DetectedCandidate> FindBoxes(PageImage page)
    {
        using var binary = ThresholdInk(page.Image);
        using var edges = new Mat();
        Cv2.Canny(page.Image, edges, 60, 180);
        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width < page.Image.Width / 8 || rect.Height < 30 || rect.Height > page.Image.Height / 3)
            {
                continue;
            }

            var cropRect = ClampRect(new Rect(rect.X + 3, rect.Y + 3, rect.Width - 6, rect.Height - 6), page.Image.Width, page.Image.Height);
            if (cropRect.Width <= 0 || cropRect.Height <= 0)
            {
                continue;
            }

            var crop = new Mat(page.Image, cropRect).Clone();
            var density = EstimateInkDensity(crop);
            yield return new DetectedCandidate(new DetectedRegion
            {
                PageIndex = page.PageIndex,
                X = cropRect.X,
                Y = cropRect.Y,
                Width = cropRect.Width,
                Height = cropRect.Height,
                CoordinateSystem = "pixels",
                Source = "BoxDetection",
                RegionConfidence = ScoreCandidate(70, density, cropRect),
                InkDensityPercent = Math.Round(density, 2),
                CandidateReason = "A rectangular signature-like box was detected and evaluated."
            }, crop);
        }
    }

    private static IEnumerable<DetectedCandidate> FindInkRegions(PageImage page)
    {
        using var binary = ThresholdInk(page.Image);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(18, 8));
        using var joined = new Mat();
        Cv2.MorphologyEx(binary, joined, MorphTypes.Close, kernel);
        Cv2.FindContours(joined, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            var aspect = rect.Width / (double)Math.Max(1, rect.Height);
            if (rect.Width < 80 || rect.Height < 20 || aspect < 1.8 || aspect > 12 || rect.Height > page.Image.Height / 4)
            {
                continue;
            }

            rect = ExpandRect(rect, page.Image.Width, page.Image.Height, 15);
            var crop = new Mat(page.Image, rect).Clone();
            var density = EstimateInkDensity(crop);
            yield return new DetectedCandidate(new DetectedRegion
            {
                PageIndex = page.PageIndex,
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                CoordinateSystem = "pixels",
                Source = "InkRegion",
                RegionConfidence = ScoreCandidate(55, density, rect),
                InkDensityPercent = Math.Round(density, 2),
                CandidateReason = "Fallback handwriting-like ink region was detected."
            }, crop);
        }
    }

    private static Rect FindInkBounds(Mat page, Rect searchRect)
    {
        using var crop = new Mat(page, ClampRect(searchRect, page.Width, page.Height));
        using var binary = ThresholdInk(crop);
        var points = SignatureDetector.FindNonZeroPoints(binary);
        if (points is null || points.Length == 0)
        {
            return new Rect();
        }

        var rect = Cv2.BoundingRect(points);
        return ExpandRect(new Rect(searchRect.X + rect.X, searchRect.Y + rect.Y, rect.Width, rect.Height), page.Width, page.Height, 12);
    }

    private static double EstimateInkDensity(Mat gray)
    {
        using var binary = ThresholdInk(gray);
        return 100.0 * Cv2.CountNonZero(binary) / Math.Max(1, binary.Width * binary.Height);
    }

    internal static Mat ThresholdInk(Mat gray)
    {
        var source = gray.Channels() == 1 ? gray : gray.CvtColor(ColorConversionCodes.BGR2GRAY);
        var blurred = new Mat();
        Cv2.GaussianBlur(source, blurred, new Size(3, 3), 0);
        var binary = new Mat();
        Cv2.Threshold(blurred, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        blurred.Dispose();
        if (!ReferenceEquals(source, gray))
        {
            source.Dispose();
        }

        return binary;
    }

    internal static Point[] FindNonZeroPoints(Mat binary)
    {
        var points = new List<Point>();
        for (var y = 0; y < binary.Height; y++)
        {
            for (var x = 0; x < binary.Width; x++)
            {
                if (binary.At<byte>(y, x) > 0)
                {
                    points.Add(new Point(x, y));
                }
            }
        }

        return points.ToArray();
    }

    internal static Rect ClampRect(Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, 0, width);
        var bottom = Math.Clamp(rect.Bottom, 0, height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Rect ExpandRect(Rect rect, int width, int height, int padding) =>
        ClampRect(new Rect(rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2), width, height);

    private static Rect InsetKnownZoneRect(Rect rect)
    {
        var padX = Math.Clamp((int)Math.Round(rect.Width * 0.035), 6, 18);
        var padY = Math.Clamp((int)Math.Round(rect.Height * 0.08), 6, 18);
        return rect.Width <= padX * 3 || rect.Height <= padY * 3
            ? rect
            : new Rect(rect.X + padX, rect.Y + padY, rect.Width - padX * 2, rect.Height - padY * 2);
    }

    private static Rect ConvertKnownZoneToPixels(KnownSignatureZone zone, PageImage page)
    {
        var coordinateSystem = (zone.CoordinateSystem ?? "pixels").Trim();
        var scaleX = 1.0;
        var scaleY = 1.0;

        if (coordinateSystem.Equals("normalized", StringComparison.OrdinalIgnoreCase) ||
            coordinateSystem.Equals("relative", StringComparison.OrdinalIgnoreCase))
        {
            scaleX = page.Image.Width;
            scaleY = page.Image.Height;
        }
        else if (coordinateSystem.Equals("percent", StringComparison.OrdinalIgnoreCase) ||
                 coordinateSystem.Equals("percentage", StringComparison.OrdinalIgnoreCase))
        {
            scaleX = page.Image.Width / 100.0;
            scaleY = page.Image.Height / 100.0;
        }
        else if (coordinateSystem.Equals("inch", StringComparison.OrdinalIgnoreCase) ||
                 coordinateSystem.Equals("inches", StringComparison.OrdinalIgnoreCase))
        {
            scaleX = page.Dpi;
            scaleY = page.Dpi;
        }
        else if (coordinateSystem.Equals("point", StringComparison.OrdinalIgnoreCase) ||
                 coordinateSystem.Equals("points", StringComparison.OrdinalIgnoreCase))
        {
            scaleX = page.Dpi / 72.0;
            scaleY = page.Dpi / 72.0;
        }

        var rect = new Rect(
            (int)Math.Round(zone.X * scaleX),
            (int)Math.Round(zone.Y * scaleY),
            (int)Math.Round(zone.Width * scaleX),
            (int)Math.Round(zone.Height * scaleY));

        return ClampRect(rect, page.Image.Width, page.Image.Height);
    }

    private static double ScoreCandidate(double sourceScore, double density, Rect rect)
    {
        var aspect = rect.Width / (double)Math.Max(1, rect.Height);
        var densityScore = density < 0.5 ? density * 20 : density > 35 ? Math.Max(0, 100 - (density - 35) * 2) : 100;
        var aspectScore = aspect is >= 2.0 and <= 8.0 ? 100 : Math.Max(0, 100 - Math.Abs(aspect - 4.0) * 18);
        return Math.Round(sourceScore * 0.5 + densityScore * 0.3 + aspectScore * 0.2, 2);
    }
}

public sealed record DetectedCandidate(DetectedRegion Region, Mat Crop);
public sealed record SignaturePlan(
    ReferenceSignatureSet ReferenceSet,
    IReadOnlyList<ReferenceFeature> ReferenceFeatures,
    IReadOnlyList<DetectedCandidate> Candidates,
    List<CandidateEvaluation> Evaluations);
public sealed record CandidateEvaluation(
    DetectedCandidate Candidate,
    ProcessedSignature ProcessedQuery,
    SignatureMetrics QueryMetrics,
    List<ReferenceComparison> Comparisons,
    double Rank);
public sealed record ReferenceFeature(string ReferenceId, string? SourceFileName, string? SourceFilePath, ProcessedSignature Processed, SignatureMetrics Metrics);

public sealed class SignaturePreprocessor
{
    public ProcessedSignature Preprocess(Mat gray, SignatureVerificationOptions options, string debugName)
    {
        var audit = new PreprocessingAudit
        {
            DenoiseApplied = true,
            BinarizationMethod = "Otsu",
            LineRemovalApplied = true,
            BoxBorderRemovalApplied = true,
            WhitespaceCropApplied = true,
            NormalizedCanvasWidth = options.NormalizedCanvasWidth,
            NormalizedCanvasHeight = options.NormalizedCanvasHeight,
            SkeletonizationApplied = true
        };

        using var sourceGray = gray.Channels() == 1 ? gray.Clone() : gray.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var denoised = new Mat();
        Cv2.MedianBlur(sourceGray, denoised, 3);
        var binary = new Mat();
        Cv2.Threshold(denoised, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        RemoveLines(binary);
        var deskewAngle = EstimateDeskewAngle(binary);
        audit.DeskewAngleDegrees = Math.Round(deskewAngle, 2);
        if (Math.Abs(deskewAngle) >= 0.5 && Math.Abs(deskewAngle) <= 10)
        {
            binary = Rotate(binary, deskewAngle);
            audit.DeskewApplied = true;
        }

        var cropped = CropToInk(binary, 12);
        var normalized = NormalizeCanvas(cropped, options.NormalizedCanvasWidth, options.NormalizedCanvasHeight);
        var skeleton = Skeletonize(normalized);

        return new ProcessedSignature(debugName, sourceGray.Clone(), binary, cropped, normalized, skeleton, audit);
    }

    private static void RemoveLines(Mat binary)
    {
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(60, binary.Width / 3), 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(80, binary.Height * 2 / 3)));
        using var horizontal = new Mat();
        using var vertical = new Mat();
        Cv2.MorphologyEx(binary, horizontal, MorphTypes.Open, horizontalKernel);
        Cv2.MorphologyEx(binary, vertical, MorphTypes.Open, verticalKernel);
        Cv2.Subtract(binary, horizontal, binary);
        Cv2.Subtract(binary, vertical, binary);
    }

    private static double EstimateDeskewAngle(Mat binary)
    {
        var points = SignatureDetector.FindNonZeroPoints(binary);
        if (points is null || points.Length < 20)
        {
            return 0;
        }

        var rect = Cv2.MinAreaRect(points);
        var angle = rect.Angle;
        if (angle < -45)
        {
            angle += 90;
        }

        return Math.Clamp(angle, -15, 15);
    }

    private static Mat Rotate(Mat source, double angle)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, source.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
        source.Dispose();
        return rotated;
    }

    private static Mat CropToInk(Mat binary, int padding)
    {
        var points = SignatureDetector.FindNonZeroPoints(binary);
        if (points is null || points.Length == 0)
        {
            return binary.Clone();
        }

        var rect = Cv2.BoundingRect(points);
        rect = SignatureDetector.ClampRect(new Rect(rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2), binary.Width, binary.Height);
        return new Mat(binary, rect).Clone();
    }

    private static Mat NormalizeCanvas(Mat crop, int canvasWidth, int canvasHeight)
    {
        var canvas = new Mat(new Size(canvasWidth, canvasHeight), MatType.CV_8UC1, Scalar.Black);
        if (crop.Empty() || Cv2.CountNonZero(crop) == 0)
        {
            return canvas;
        }

        var scale = Math.Min((canvasWidth * 0.9) / crop.Width, (canvasHeight * 0.85) / crop.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(crop.Height * scale));
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Area);
        var x = (canvasWidth - resizedWidth) / 2;
        var y = (canvasHeight - resizedHeight) / 2;
        using var roi = new Mat(canvas, new Rect(x, y, resizedWidth, resizedHeight));
        resized.CopyTo(roi);
        return canvas;
    }

    private static Mat Skeletonize(Mat binary)
    {
        var skeleton = new Mat(binary.Size(), MatType.CV_8UC1, Scalar.Black);
        using var element = Cv2.GetStructuringElement(MorphShapes.Cross, new Size(3, 3));
        using var temp = new Mat();
        using var eroded = new Mat();
        using var current = binary.Clone();

        while (Cv2.CountNonZero(current) > 0)
        {
            Cv2.Erode(current, eroded, element);
            Cv2.Dilate(eroded, temp, element);
            Cv2.Subtract(current, temp, temp);
            Cv2.BitwiseOr(skeleton, temp, skeleton);
            eroded.CopyTo(current);
        }

        return skeleton;
    }
}

public sealed record ProcessedSignature(
    string DebugName,
    Mat OriginalGray,
    Mat CleanBinary,
    Mat CroppedBinary,
    Mat NormalizedBinary,
    Mat SkeletonBinary,
    PreprocessingAudit Audit) : IDisposable
{
    public void Dispose()
    {
        OriginalGray.Dispose();
        CleanBinary.Dispose();
        CroppedBinary.Dispose();
        NormalizedBinary.Dispose();
        SkeletonBinary.Dispose();
    }
}

public sealed class FeatureExtractor
{
    public SignatureMetrics Extract(ProcessedSignature processed, Mat binary, double cropConfidence)
    {
        var metrics = new SignatureMetrics
        {
            InkPixelCount = Cv2.CountNonZero(binary),
            InkDensityPercent = Percent(Cv2.CountNonZero(binary), binary.Width * binary.Height),
            BlankAreaPercent = 100 - Percent(Cv2.CountNonZero(binary), binary.Width * binary.Height),
            CropConfidence = Math.Round(cropConfidence, 2),
            DensityGrid8x4 = BuildGrid(binary, 8, 4),
            DensityGrid16x8 = BuildGrid(binary, 16, 8),
            QuadrantInkDistribution = BuildGrid(binary, 2, 2),
            SkeletonPixelCount = Cv2.CountNonZero(processed.SkeletonBinary),
            SkeletonDensity = Percent(Cv2.CountNonZero(processed.SkeletonBinary), processed.SkeletonBinary.Width * processed.SkeletonBinary.Height),
            ImageSharpness = EstimateSharpness(processed.OriginalGray),
            NoiseEstimate = EstimateNoise(processed.OriginalGray),
            BaselineAngleDegrees = EstimateBaselineAngle(binary)
        };

        var points = SignatureDetector.FindNonZeroPoints(binary);
        if (points is not null && points.Length > 0)
        {
            var rect = Cv2.BoundingRect(points);
            metrics.BoundingBoxWidth = rect.Width;
            metrics.BoundingBoxHeight = rect.Height;
            metrics.AspectRatio = Math.Round(rect.Width / (double)Math.Max(1, rect.Height), 3);
            metrics.NormalizedArea = Math.Round(rect.Width * rect.Height / (double)Math.Max(1, binary.Width * binary.Height), 4);
        }

        var moments = Cv2.Moments(binary, true);
        if (Math.Abs(moments.M00) > double.Epsilon)
        {
            metrics.CenterOfMassX = Math.Round((moments.M10 / moments.M00) / binary.Width, 4);
            metrics.CenterOfMassY = Math.Round((moments.M01 / moments.M00) / binary.Height, 4);
        }

        Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        metrics.ContourCount = contours.Length;
        var contourAreas = contours.Select(c => Cv2.ContourArea(c)).Where(a => a > 0).ToList();
        metrics.TotalContourArea = Math.Round(contourAreas.Sum(), 2);
        metrics.LargestContourAreaRatio = contourAreas.Count == 0 ? 0 : Math.Round(contourAreas.Max() / Math.Max(1.0, contourAreas.Sum()), 4);
        metrics.HuMoments = BuildHuMoments(moments);

        ExtractConnectedComponents(binary, metrics);
        metrics.StrokeThicknessEstimate = EstimateStrokeThickness(binary, processed.SkeletonBinary);
        metrics.TooBlank = metrics.InkDensityPercent < 0.5;
        metrics.TooDark = metrics.InkDensityPercent > 40;
        metrics.TooNoisy = metrics.NoiseEstimate > 35;
        metrics.TooSmall = metrics.BoundingBoxWidth < 60 || metrics.BoundingBoxHeight < 18;
        metrics.LineOrBoxOverlap =
            metrics.FragmentedStrokeIndicator >= 55 &&
            metrics.LargestComponentRatio <= 0.25 &&
            metrics.StrokeThicknessEstimate <= 1.2 &&
            metrics.BoundingBoxHeight < 55;

        return metrics;
    }

    private static void ExtractConnectedComponents(Mat binary, SignatureMetrics metrics)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);
        var areas = new List<int>();
        for (var i = 1; i < count; i++)
        {
            areas.Add(stats.At<int>(i, (int)ConnectedComponentsTypes.Area));
        }

        metrics.ConnectedComponentCount = areas.Count;
        metrics.LargestComponentRatio = areas.Count == 0 ? 0 : Math.Round(areas.Max() / (double)Math.Max(1, areas.Sum()), 4);
        metrics.ComponentSizeDistribution = areas.OrderByDescending(a => a).Take(12).Select(a => Math.Round(a / (double)Math.Max(1, areas.Sum()), 4)).ToList();
        metrics.FragmentedStrokeIndicator = Math.Round(Math.Min(100, areas.Count * 2.5), 2);
    }

    private static double[] BuildGrid(Mat binary, int columns, int rows)
    {
        var values = new double[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = column * binary.Width / columns;
                var y = row * binary.Height / rows;
                var w = (column + 1) * binary.Width / columns - x;
                var h = (row + 1) * binary.Height / rows - y;
                using var cell = new Mat(binary, new Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
                values[index++] = Math.Round(Percent(Cv2.CountNonZero(cell), cell.Width * cell.Height), 4);
            }
        }

        return values;
    }

    private static double[] BuildHuMoments(Moments moments)
    {
        var nu20 = moments.Nu20;
        var nu02 = moments.Nu02;
        var nu11 = moments.Nu11;
        var nu30 = moments.Nu30;
        var nu12 = moments.Nu12;
        var nu21 = moments.Nu21;
        var nu03 = moments.Nu03;
        var hu = new[]
        {
            nu20 + nu02,
            Math.Pow(nu20 - nu02, 2) + 4 * Math.Pow(nu11, 2),
            Math.Pow(nu30 - 3 * nu12, 2) + Math.Pow(3 * nu21 - nu03, 2),
            Math.Pow(nu30 + nu12, 2) + Math.Pow(nu21 + nu03, 2),
            (nu30 - 3 * nu12) * (nu30 + nu12) * (Math.Pow(nu30 + nu12, 2) - 3 * Math.Pow(nu21 + nu03, 2)) +
            (3 * nu21 - nu03) * (nu21 + nu03) * (3 * Math.Pow(nu30 + nu12, 2) - Math.Pow(nu21 + nu03, 2)),
            (nu20 - nu02) * (Math.Pow(nu30 + nu12, 2) - Math.Pow(nu21 + nu03, 2)) +
            4 * nu11 * (nu30 + nu12) * (nu21 + nu03),
            (3 * nu21 - nu03) * (nu30 + nu12) * (Math.Pow(nu30 + nu12, 2) - 3 * Math.Pow(nu21 + nu03, 2)) -
            (nu30 - 3 * nu12) * (nu21 + nu03) * (3 * Math.Pow(nu30 + nu12, 2) - Math.Pow(nu21 + nu03, 2))
        };
        return hu.Select(v => Math.Round(-Math.Sign(v) * Math.Log10(Math.Abs(v) + 1e-12), 6)).ToArray();
    }

    private static double EstimateSharpness(Mat gray)
    {
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var stddev);
        return Math.Round(Math.Min(100, stddev.Val0 * stddev.Val0 / 10.0), 2);
    }

    private static double EstimateNoise(Mat gray)
    {
        using var blur = new Mat();
        using var diff = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);
        Cv2.Absdiff(gray, blur, diff);
        return Math.Round(Math.Min(100, Cv2.Mean(diff).Val0), 2);
    }

    private static double EstimateBaselineAngle(Mat binary)
    {
        var points = SignatureDetector.FindNonZeroPoints(binary);
        if (points is null || points.Length < 20)
        {
            return 0;
        }

        var rect = Cv2.MinAreaRect(points);
        var angle = rect.Angle;
        if (angle < -45)
        {
            angle += 90;
        }

        return Math.Round(Math.Clamp(angle, -45, 45), 2);
    }

    private static double EstimateStrokeThickness(Mat binary, Mat skeleton)
    {
        var skeletonCount = Cv2.CountNonZero(skeleton);
        if (skeletonCount == 0)
        {
            return 0;
        }

        return Math.Round(Cv2.CountNonZero(binary) / (double)skeletonCount, 2);
    }

    private static double Percent(int count, int total) => Math.Round(100.0 * count / Math.Max(1, total), 4);
}

public sealed class SimilarityScorer
{
    public ReferenceComparison Compare(SignatureMetrics query, ProcessedSignature queryImage, SignatureMetrics reference, ReferenceFeature referenceImage, SignatureVerificationOptions options)
    {
        var comparison = new ReferenceComparison
        {
            ReferenceId = referenceImage.ReferenceId,
            ReferenceFileName = referenceImage.SourceFileName,
            ReferenceFilePath = referenceImage.SourceFilePath,
            GeometryScore = Average(
                RatioScore(query.AspectRatio, reference.AspectRatio, 0.6),
                RatioScore(query.NormalizedArea, reference.NormalizedArea, 0.5),
                RatioScore(query.CenterOfMassX, reference.CenterOfMassX, 0.25),
                RatioScore(query.CenterOfMassY, reference.CenterOfMassY, 0.25)),
            InkDensityScore = RatioScore(query.InkDensityPercent, reference.InkDensityPercent, 0.7),
            DensityGridScore = GridScore(query.DensityGrid8x4, reference.DensityGrid8x4),
            ContourScore = Average(
                RatioScore(query.ContourCount, reference.ContourCount, 0.8),
                RatioScore(query.LargestContourAreaRatio, reference.LargestContourAreaRatio, 0.7),
                HuScore(query.HuMoments, reference.HuMoments)),
            SkeletonScore = Average(
                RatioScore(query.SkeletonDensity, reference.SkeletonDensity, 0.7),
                GridScore(BuildSkeletonGrid(queryImage.SkeletonBinary), BuildSkeletonGrid(referenceImage.Processed.SkeletonBinary))),
            StructuralSimilarityScore = PixelSimilarity(queryImage.NormalizedBinary, referenceImage.Processed.NormalizedBinary),
            ConnectedComponentScore = Average(
                RatioScore(query.ConnectedComponentCount, reference.ConnectedComponentCount, 1.0),
                RatioScore(query.LargestComponentRatio, reference.LargestComponentRatio, 0.7))
        };

        comparison.QualityScore = QualityScore(query);
        comparison.Confidence = WeightedScore(comparison, options.Weights);
        comparison.StructuralMismatchPenalty = StructuralMismatchPenalty(comparison);
        comparison.QualityAdjustedScore = Math.Round(Math.Max(0, comparison.Confidence * (comparison.QualityScore / 100.0) - comparison.StructuralMismatchPenalty), 2);
        return comparison;
    }

    private static double[] BuildSkeletonGrid(Mat skeleton) => FeatureExtractorForGrid(skeleton);

    private static double[] FeatureExtractorForGrid(Mat mat)
    {
        var columns = 8;
        var rows = 4;
        var values = new double[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var x = column * mat.Width / columns;
                var y = row * mat.Height / rows;
                var w = (column + 1) * mat.Width / columns - x;
                var h = (row + 1) * mat.Height / rows - y;
                using var cell = new Mat(mat, new Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
                values[index++] = 100.0 * Cv2.CountNonZero(cell) / Math.Max(1, cell.Width * cell.Height);
            }
        }

        return values;
    }

    private static double WeightedScore(ReferenceComparison c, ScoreWeights w) => Math.Round(
        w.GeometryScore * c.GeometryScore +
        w.InkDensityScore * c.InkDensityScore +
        w.DensityGridScore * c.DensityGridScore +
        w.ContourScore * c.ContourScore +
        w.SkeletonScore * c.SkeletonScore +
        w.StructuralSimilarityScore * c.StructuralSimilarityScore +
        w.ConnectedComponentScore * c.ConnectedComponentScore, 2);

    private static double StructuralMismatchPenalty(ReferenceComparison c)
    {
        // Dense grid/pixel similarity can be misleading across different writing styles.
        // Require the shape-family signals to agree before allowing a borderline review score.
        if (c.GeometryScore < 55 &&
            c.ContourScore < 62 &&
            c.ConnectedComponentScore < 65)
        {
            return 8;
        }

        return 0;
    }

    private static double QualityScore(SignatureMetrics metrics)
    {
        var score = 100.0;
        if (metrics.ImageSharpness < 15) score -= 12;
        if (metrics.TooBlank) score -= 30;
        if (metrics.TooDark) score -= 18;
        if (metrics.TooNoisy) score -= 15;
        if (metrics.TooSmall) score -= 20;
        if (metrics.LineOrBoxOverlap) score -= 8;
        if (metrics.CropConfidence < 60) score -= 12;
        return Math.Round(Math.Clamp(score, 0, 100), 2);
    }

    private static double RatioScore(double a, double b, double tolerance)
    {
        if (Math.Abs(a) < double.Epsilon && Math.Abs(b) < double.Epsilon)
        {
            return 100;
        }

        var diff = Math.Abs(a - b) / Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1e-6);
        return Math.Round(Math.Clamp(100 * (1 - diff / Math.Max(tolerance, 1e-6)), 0, 100), 2);
    }

    private static double GridScore(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var count = Math.Min(a.Count, b.Count);
        if (count == 0)
        {
            return 0;
        }

        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return Math.Round(Math.Clamp(100 - total / count * 6, 0, 100), 2);
    }

    private static double HuScore(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var count = Math.Min(a.Count, b.Count);
        if (count == 0)
        {
            return 0;
        }

        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return Math.Round(Math.Clamp(100 - total / count * 12, 0, 100), 2);
    }

    private static double PixelSimilarity(Mat a, Mat b)
    {
        using var resized = new Mat();
        if (a.Size() != b.Size())
        {
            Cv2.Resize(a, resized, b.Size());
        }
        else
        {
            a.CopyTo(resized);
        }

        var best = RawPixelSimilarity(resized, b);
        var offsets = new[] { -12, -6, 0, 6, 12 };
        foreach (var dx in offsets)
        {
            foreach (var dy in offsets)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                best = Math.Max(best, ShiftedPixelSimilarity(resized, b, dx, dy));
            }
        }

        return Math.Round(best, 2);
    }

    private static double ShiftedPixelSimilarity(Mat source, Mat target, int dx, int dy)
    {
        var sourceX = Math.Max(0, dx);
        var sourceY = Math.Max(0, dy);
        var targetX = Math.Max(0, -dx);
        var targetY = Math.Max(0, -dy);
        var width = Math.Min(source.Width - sourceX, target.Width - targetX);
        var height = Math.Min(source.Height - sourceY, target.Height - targetY);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var overlapRatio = width * height / (double)(target.Width * target.Height);
        if (overlapRatio < 0.75)
        {
            return 0;
        }

        using var sourceRoi = new Mat(source, new Rect(sourceX, sourceY, width, height));
        using var targetRoi = new Mat(target, new Rect(targetX, targetY, width, height));
        return RawPixelSimilarity(sourceRoi, targetRoi) * overlapRatio;
    }

    private static double RawPixelSimilarity(Mat a, Mat b)
    {
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        return Math.Clamp(100 - Cv2.Mean(diff).Val0 / 255.0 * 100, 0, 100);
    }

    private static double Average(params double[] values) => Math.Round(values.Length == 0 ? 0 : values.Average(), 2);
}

public sealed class DecisionEngine
{
    public SignatureDecision GetSignatureDecision(double confidence, SignatureMetrics metrics, SignatureVerificationOptions options)
    {
        if (metrics.TooBlank || metrics.TooSmall || metrics.ImageSharpness < 4)
        {
            return new SignatureDecision("InsufficientQuality", false, true);
        }

        if (confidence >= options.Thresholds.Matched)
        {
            return new SignatureDecision("Matched", true, false);
        }

        if (confidence >= options.Thresholds.ProbableMatch)
        {
            return new SignatureDecision("ProbableMatch", true, true);
        }

        if (confidence >= options.Thresholds.ReviewRequired)
        {
            return new SignatureDecision("ReviewRequired", false, true);
        }

        return new SignatureDecision("NotMatched", false, false);
    }

    public string GetOverallDecision(IReadOnlyList<SignatureResult> results)
    {
        if (results.Count == 0)
        {
            return "Error";
        }

        if (results.All(r => r.Decision == "Matched"))
        {
            return "Matched";
        }

        if (results.Any(r => r.Decision is "ProbableMatch" or "ReviewRequired" or "NoSignatureDetected" or "InsufficientQuality"))
        {
            return "ReviewRequired";
        }

        if (results.Any(r => r.Decision == "NotMatched"))
        {
            return "NotMatched";
        }

        return "ReviewRequired";
    }
}

public sealed record SignatureDecision(string Decision, bool IsMatched, bool ReviewRequired);

public sealed class ReasoningBuilder
{
    public string Build(string decision, ReferenceComparison? comparison)
    {
        return decision switch
        {
            "Matched" => "The extracted signature is visually consistent with the reference signatures. Aspect ratio, ink density distribution, contour shape, and skeleton structure are within match thresholds.",
            "ProbableMatch" => "The extracted signature is broadly consistent with the reference signature, but one or more comparison metrics are below the full match threshold. Human review is recommended.",
            "ReviewRequired" => BuildReviewText(comparison),
            "NotMatched" => "The extracted signature is visually inconsistent with the reference signature. Contour structure, density distribution, and skeleton structure are below the match threshold.",
            "InsufficientQuality" => "A signature-like region was detected, but image quality is insufficient for reliable comparison due to low sharpness, low ink density, excessive noise, or an incomplete crop.",
            _ => "No valid signature-like ink region was detected for the expected signature area."
        };
    }

    private static string BuildReviewText(ReferenceComparison? comparison)
    {
        if (comparison is null)
        {
            return "The extracted signature has acceptable image quality but requires human review.";
        }

        var weak = new[]
        {
            ("contour structure", comparison.ContourScore),
            ("density distribution", comparison.DensityGridScore),
            ("skeleton structure", comparison.SkeletonScore),
            ("geometry", comparison.GeometryScore)
        }.OrderBy(x => x.Item2).Take(2).Select(x => x.Item1);

        return $"The extracted signature has acceptable image quality but differs from the reference in {string.Join(" and ", weak)}. Human review is recommended.";
    }
}

public sealed class OcrLayoutParser
{
    public OcrLayout Parse(string? json, IReadOnlyList<PageImage> renderedPages, List<string> warnings)
    {
        var layout = new OcrLayout();
        if (string.IsNullOrWhiteSpace(json))
        {
            return layout;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("analyzeResult", out var analyzeResult))
            {
                root = analyzeResult;
            }

            if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("OCR_JSON_PARSE_WARNING: OCR JSON did not contain a pages array.");
                return layout;
            }

            var ordinalPageIndex = 0;
            foreach (var page in pages.EnumerateArray())
            {
                var ocrPageIndex = Math.Max(0, (GetInt(page, "pageNumber") ?? GetInt(page, "page") ?? (ordinalPageIndex + 1)) - 1);
                var imagePage = renderedPages.FirstOrDefault(p => p.PageIndex == ocrPageIndex)
                                ?? renderedPages.ElementAtOrDefault(ordinalPageIndex)
                                ?? renderedPages.FirstOrDefault();
                var outputPageIndex = imagePage?.PageIndex ?? ocrPageIndex;
                var pageWidth = GetDouble(page, "width") ?? imagePage?.Image.Width ?? 1;
                var pageHeight = GetDouble(page, "height") ?? imagePage?.Image.Height ?? 1;
                var unit = GetString(page, "unit") ?? "pixel";

                foreach (var line in ReadTextItems(page, "lines").Concat(ReadTextItems(page, "words")))
                {
                    var rect = ConvertPolygonToPixels(line.Polygon, pageWidth, pageHeight, imagePage?.Image.Width ?? (int)pageWidth, imagePage?.Image.Height ?? (int)pageHeight, unit);
                    if (rect.Width > 0 && rect.Height > 0 && !string.IsNullOrWhiteSpace(line.Text))
                    {
                        layout.Labels.Add(new OcrLabel(outputPageIndex, line.Text, rect));
                    }
                }

                ordinalPageIndex++;
            }
        }
        catch (Exception)
        {
            warnings.Add("OCR_JSON_PARSE_WARNING: OCR JSON could not be parsed. Detection continued without OCR anchors.");
        }

        return layout;
    }

    private static IEnumerable<(string Text, double[] Polygon)> ReadTextItems(JsonElement page, string propertyName)
    {
        if (!page.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in items.EnumerateArray())
        {
            var text = GetString(item, "content") ?? GetString(item, "text") ?? string.Empty;
            var polygon = GetPolygon(item);
            if (polygon.Length >= 4)
            {
                yield return (text, polygon);
            }
        }
    }

    private static double[] GetPolygon(JsonElement item)
    {
        foreach (var name in new[] { "boundingPolygon", "polygon" })
        {
            if (item.TryGetProperty(name, out var polygon) && polygon.ValueKind == JsonValueKind.Array)
            {
                var values = new List<double>();
                foreach (var value in polygon.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        values.Add(value.GetDouble());
                    }
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        if (value.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number)
                        {
                            values.Add(x.GetDouble());
                        }

                        if (value.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
                        {
                            values.Add(y.GetDouble());
                        }
                    }
                }

                return values.ToArray();
            }
        }

        return Array.Empty<double>();
    }

    private static Rect ConvertPolygonToPixels(double[] polygon, double pageWidth, double pageHeight, int imageWidth, int imageHeight, string unit)
    {
        var xs = polygon.Where((_, i) => i % 2 == 0).ToList();
        var ys = polygon.Where((_, i) => i % 2 == 1).ToList();
        if (xs.Count == 0 || ys.Count == 0)
        {
            return new Rect();
        }

        var maxX = xs.Max();
        var maxY = ys.Max();
        var normalized = maxX <= 1.5 && maxY <= 1.5;
        var inchLike = unit.Contains("inch", StringComparison.OrdinalIgnoreCase);
        var scaleX = normalized ? imageWidth : inchLike || pageWidth <= imageWidth / 2.0 ? imageWidth / Math.Max(1, pageWidth) : 1;
        var scaleY = normalized ? imageHeight : inchLike || pageHeight <= imageHeight / 2.0 ? imageHeight / Math.Max(1, pageHeight) : 1;
        var left = (int)Math.Round(xs.Min() * scaleX);
        var top = (int)Math.Round(ys.Min() * scaleY);
        var right = (int)Math.Round(xs.Max() * scaleX);
        var bottom = (int)Math.Round(ys.Max() * scaleY);
        return SignatureDetector.ClampRect(new Rect(left, top, right - left, bottom - top), imageWidth, imageHeight);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value) ? value : null;
}

public sealed class DebugImageWriter
{
    public Dictionary<string, string>? TryWrite(
        SignatureVerificationOptions options,
        IReadOnlyList<PageImage> pages,
        string signatureId,
        DetectedCandidate candidate,
        ProcessedSignature processed,
        IReadOnlyList<DetectedRegion> regions,
        List<string> warnings)
    {
        if (!options.SaveDebugImages || string.IsNullOrWhiteSpace(options.DebugOutputFolder))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(options.DebugOutputFolder);
            var paths = new Dictionary<string, string>();
            var page = pages.FirstOrDefault(p => p.PageIndex == candidate.Region.PageIndex);
            if (page is not null)
            {
                var pagePath = Path.Combine(options.DebugOutputFolder, $"page_{page.PageIndex}.png");
                Cv2.ImWrite(pagePath, page.Image);
                paths["renderedPagePath"] = pagePath;

                using var visualization = page.Image.CvtColor(ColorConversionCodes.GRAY2BGR);
                foreach (var region in regions)
                {
                    Cv2.Rectangle(visualization, new Rect(region.X, region.Y, region.Width, region.Height), Scalar.Red, 2);
                }

                var candidatePath = Path.Combine(options.DebugOutputFolder, $"page_{page.PageIndex}_{signatureId}_candidates.png");
                Cv2.ImWrite(candidatePath, visualization);
                paths["candidateVisualizationPath"] = candidatePath;
            }

            paths["rawCropPath"] = Write(Path.Combine(options.DebugOutputFolder, $"{signatureId}_raw.png"), candidate.Crop);
            paths["cleanedCropPath"] = Write(Path.Combine(options.DebugOutputFolder, $"{signatureId}_cleaned.png"), processed.CroppedBinary);
            paths["normalizedPath"] = Write(Path.Combine(options.DebugOutputFolder, $"{signatureId}_normalized.png"), processed.NormalizedBinary);
            paths["skeletonPath"] = Write(Path.Combine(options.DebugOutputFolder, $"{signatureId}_skeleton.png"), processed.SkeletonBinary);
            return paths;
        }
        catch (Exception)
        {
            warnings.Add("DEBUG_OUTPUT_WRITE_FAILED");
            return null;
        }
    }

    private static string Write(string path, Mat mat)
    {
        using var visible = new Mat();
        Cv2.BitwiseNot(mat, visible);
        Cv2.ImWrite(path, visible);
        return path;
    }
}

public sealed class ReferenceSignatureInput
{
    public List<ReferenceSignatureSet> ReferenceSets { get; set; } = new();
    public List<SignatureMapping> SignatureMappings { get; set; } = new();

    public static ReferenceSignatureInput FromJson(string json)
    {
        var input = JsonSerializer.Deserialize<ReferenceSignatureInput>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new ReferenceSignatureInput();
        input.ApplySignatureMappings();
        return input;
    }

    private void ApplySignatureMappings()
    {
        foreach (var set in ReferenceSets)
        {
            set.Mapping ??= SignatureMappings.FirstOrDefault(mapping =>
                string.Equals(mapping.SignatureId, set.SignatureId, StringComparison.OrdinalIgnoreCase));

            set.Mapping ??= SignatureMapping.FromReferenceSet(set);
            set.Mapping.SignatureId = string.IsNullOrWhiteSpace(set.Mapping.SignatureId) ? set.SignatureId : set.Mapping.SignatureId;
            set.Mapping.DisplayName ??= set.DisplayName;
            set.Mapping.ReferenceSetId ??= set.ReferenceImages.FirstOrDefault()?.SourceFilePath is { Length: > 0 } firstPath
                ? Path.GetFileName(Path.GetDirectoryName(firstPath))
                : set.SignatureId;
        }
    }
}

public sealed class ReferenceSignatureSet
{
    public string SignatureId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PartyId { get; set; }
    public string? PartyName { get; set; }
    public string? ReferenceSetId { get; set; }
    public string? ExpectedSignerId { get; set; }
    public string? ActualSignerId { get; set; }
    public string? ExpectedClass { get; set; }
    public SignatureMapping? Mapping { get; set; }
    public List<string> ExpectedLabels { get; set; } = new();
    public List<ReferenceSignatureImage> ReferenceImages { get; set; } = new();
}

public sealed class SignatureMapping
{
    public string SignatureId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PartyId { get; set; }
    public string? PartyName { get; set; }
    public string? ReferenceSetId { get; set; }
    public string? ExpectedSignerId { get; set; }
    public string? ActualSignerId { get; set; }
    public string? ExpectedClass { get; set; }
    public string? Source { get; set; }

    public static SignatureMapping FromReferenceSet(ReferenceSignatureSet set) => new()
    {
        SignatureId = set.SignatureId,
        DisplayName = set.DisplayName,
        PartyId = set.PartyId,
        PartyName = set.PartyName,
        ReferenceSetId = set.ReferenceSetId ?? set.ExpectedSignerId,
        ExpectedSignerId = set.ExpectedSignerId ?? set.ReferenceSetId,
        ActualSignerId = set.ActualSignerId,
        ExpectedClass = set.ExpectedClass,
        Source = "ReferenceSet"
    };
}

public sealed class ReferenceSignatureImage
{
    public string ReferenceId { get; set; } = string.Empty;
    public string? SourceFileName { get; set; }
    public string? SourceFilePath { get; set; }
    public string ImageBase64 { get; set; } = string.Empty;
}

public sealed class SignatureVerificationOptions
{
    public string? CorrelationId { get; set; }
    public string DocumentType { get; set; } = "Unknown";
    public string InputDocumentType { get; set; } = "Auto";
    public int? PageIndex { get; set; }
    public int Dpi { get; set; } = 300;
    public string? DebugOutputFolder { get; set; }
    public bool SaveDebugImages { get; set; }
    public PdfRenderingOptions PdfRendering { get; set; } = new();
    public ScoreThresholds Thresholds { get; set; } = new();
    public ScoreWeights Weights { get; set; } = new();
    public DetectionOptions Detection { get; set; } = new();
    public SignatureRoleOptions SignatureRoles { get; set; } = new();
    public List<KnownSignatureZone> KnownZones { get; set; } = new();
    public int NormalizedCanvasWidth { get; set; } = 512;
    public int NormalizedCanvasHeight { get; set; } = 256;

    public static SignatureVerificationOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SignatureVerificationOptions();
        }

        return JsonSerializer.Deserialize<SignatureVerificationOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new SignatureVerificationOptions();
    }

    public PdfRenderOptions ToPdfRenderOptions() => new()
    {
        Dpi = Dpi > 0 ? Dpi : PdfRendering.Dpi,
        PageIndex = PageIndex,
        MaxPages = PdfRendering.MaxPages,
        RenderGrayscale = PdfRendering.RenderGrayscale,
        Renderer = PdfRendering.Renderer,
        GhostscriptExecutablePath = PdfRendering.GhostscriptExecutablePath,
        TimeoutSeconds = PdfRendering.TimeoutSeconds,
        TempFolder = PdfRendering.TempFolder,
        KeepTempFiles = SaveDebugImages
    };

    public void ValidateAndNormalize(List<string> warnings)
    {
        PdfRendering ??= new PdfRenderingOptions();
        Thresholds ??= new ScoreThresholds();
        Weights ??= new ScoreWeights();
        Detection ??= new DetectionOptions();
        SignatureRoles ??= new SignatureRoleOptions();
        KnownZones ??= new List<KnownSignatureZone>();

        Dpi = Math.Clamp(Dpi, 72, 600);
        PdfRendering.Dpi = Math.Clamp(PdfRendering.Dpi <= 0 ? Dpi : PdfRendering.Dpi, 72, 600);
        PdfRendering.MaxPages = Math.Clamp(PdfRendering.MaxPages, 1, 500);
        PdfRendering.TimeoutSeconds = Math.Clamp(PdfRendering.TimeoutSeconds, 1, 300);

        if (Thresholds.Matched < Thresholds.ProbableMatch ||
            Thresholds.ProbableMatch < Thresholds.ReviewRequired ||
            Thresholds.ReviewRequired < 0 ||
            Thresholds.Matched > 100)
        {
            warnings.Add("OPTIONS_JSON_INVALID: Thresholds were invalid and reset to defaults.");
            Thresholds = new ScoreThresholds();
        }

        Thresholds.Matched = Math.Clamp(Thresholds.Matched, 0, 100);
        Thresholds.ProbableMatch = Math.Clamp(Thresholds.ProbableMatch, 0, 100);
        Thresholds.ReviewRequired = Math.Clamp(Thresholds.ReviewRequired, 0, 100);

        NormalizeWeights(warnings);

        NormalizedCanvasWidth = Math.Clamp(NormalizedCanvasWidth, 64, 2048);
        NormalizedCanvasHeight = Math.Clamp(NormalizedCanvasHeight, 32, 2048);

        Detection.MaxCandidateRegionsPerSignature = Math.Clamp(Detection.MaxCandidateRegionsPerSignature, 1, 100);
        Detection.CandidatePoolMultiplier = Math.Clamp(Detection.CandidatePoolMultiplier, 1, 20);
        Detection.MinimumInkDensityPercent = Math.Clamp(Detection.MinimumInkDensityPercent, 0, 100);
        Detection.MaximumInkDensityPercent = Math.Clamp(Detection.MaximumInkDensityPercent, 0, 100);
        if (Detection.MinimumInkDensityPercent > Detection.MaximumInkDensityPercent)
        {
            warnings.Add("OPTIONS_JSON_INVALID: Detection ink density bounds were invalid and reset to defaults.");
            Detection.MinimumInkDensityPercent = 0.5;
            Detection.MaximumInkDensityPercent = 35.0;
        }

        foreach (var zone in KnownZones)
        {
            if (!KnownSignatureZone.IsSupportedCoordinateSystem(zone.CoordinateSystem))
            {
                warnings.Add($"OPTIONS_JSON_INVALID: Known zone coordinateSystem '{zone.CoordinateSystem}' is not supported; pixels will be assumed.");
                zone.CoordinateSystem = "pixels";
            }
        }
    }

    public ReferenceSignatureInput ApplySignatureRoleConfiguration(ReferenceSignatureInput references)
    {
        var expectedRoles = SignatureRoles.GetExpectedRoleIds();
        if (expectedRoles.Count == 0)
        {
            return references;
        }

        var selected = new List<ReferenceSignatureSet>();
        foreach (var expectedRole in expectedRoles)
        {
            var existing = references.ReferenceSets
                .FirstOrDefault(set => string.Equals(SignatureRoleOptions.NormalizeRoleId(set.SignatureId), expectedRole, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                selected.Add(new ReferenceSignatureSet
                {
                    SignatureId = expectedRole,
                    DisplayName = existing.DisplayName ?? SignatureRoleOptions.GetDisplayName(expectedRole),
                    PartyId = existing.PartyId,
                    PartyName = existing.PartyName,
                    ReferenceSetId = existing.ReferenceSetId,
                    ExpectedSignerId = existing.ExpectedSignerId,
                    ActualSignerId = existing.ActualSignerId,
                    ExpectedClass = existing.ExpectedClass,
                    Mapping = existing.Mapping,
                    ExpectedLabels = SignatureRoleOptions.MergeExpectedLabels(expectedRole, existing.ExpectedLabels),
                    ReferenceImages = existing.ReferenceImages
                });
                continue;
            }

            selected.Add(new ReferenceSignatureSet
            {
                SignatureId = expectedRole,
                DisplayName = SignatureRoleOptions.GetDisplayName(expectedRole),
                ExpectedLabels = SignatureRoleOptions.GetExpectedLabels(expectedRole)
            });
        }

        KnownZones = KnownZones
            .Where(zone => expectedRoles.Contains(SignatureRoleOptions.NormalizeRoleId(zone.SignatureId), StringComparer.OrdinalIgnoreCase))
            .Select(zone => new KnownSignatureZone
            {
                SignatureId = SignatureRoleOptions.NormalizeRoleId(zone.SignatureId),
                PageIndex = zone.PageIndex,
                X = zone.X,
                Y = zone.Y,
                Width = zone.Width,
                Height = zone.Height,
                CoordinateSystem = zone.CoordinateSystem
            })
            .ToList();

        return new ReferenceSignatureInput
        {
            ReferenceSets = selected,
            SignatureMappings = references.SignatureMappings
        };
    }

    private void NormalizeWeights(List<string> warnings)
    {
        var values = new[]
        {
            Math.Max(0, Weights.GeometryScore),
            Math.Max(0, Weights.InkDensityScore),
            Math.Max(0, Weights.DensityGridScore),
            Math.Max(0, Weights.ContourScore),
            Math.Max(0, Weights.SkeletonScore),
            Math.Max(0, Weights.StructuralSimilarityScore),
            Math.Max(0, Weights.ConnectedComponentScore)
        };
        var sum = values.Sum();
        if (sum <= 0)
        {
            warnings.Add("OPTIONS_JSON_INVALID: Score weights were invalid and reset to defaults.");
            Weights = new ScoreWeights();
            return;
        }

        if (Math.Abs(sum - 1.0) > 0.001 || values.Any(v => v == 0))
        {
            warnings.Add("OPTIONS_JSON_INVALID: Score weights were clamped and normalized.");
        }

        Weights.GeometryScore = values[0] / sum;
        Weights.InkDensityScore = values[1] / sum;
        Weights.DensityGridScore = values[2] / sum;
        Weights.ContourScore = values[3] / sum;
        Weights.SkeletonScore = values[4] / sum;
        Weights.StructuralSimilarityScore = values[5] / sum;
        Weights.ConnectedComponentScore = values[6] / sum;
    }
}

public sealed class PdfRenderingOptions
{
    public int Dpi { get; set; } = 300;
    public string Renderer { get; set; } = "GhostscriptCommandLine";
    public string? GhostscriptExecutablePath { get; set; }
    public int MaxPages { get; set; } = 5;
    public bool RenderGrayscale { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 60;
    public string? TempFolder { get; set; }
}

public sealed class ScoreThresholds
{
    public double Matched { get; set; } = 85;
    public double ProbableMatch { get; set; } = 70;
    public double ReviewRequired { get; set; } = 55;
}

public sealed class ScoreWeights
{
    public double GeometryScore { get; set; } = 0.15;
    public double InkDensityScore { get; set; } = 0.20;
    public double DensityGridScore { get; set; } = 0.20;
    public double ContourScore { get; set; } = 0.15;
    public double SkeletonScore { get; set; } = 0.15;
    public double StructuralSimilarityScore { get; set; } = 0.10;
    public double ConnectedComponentScore { get; set; } = 0.05;
}

public sealed class DetectionOptions
{
    public bool UseKnownZones { get; set; } = true;
    public bool UseOcrAnchors { get; set; } = true;
    public bool UseBoxDetection { get; set; } = true;
    public bool UseInkRegionDetection { get; set; } = true;
    public bool SearchBelowLabel { get; set; } = true;
    public bool SearchRightOfLabel { get; set; } = true;
    public int MaxCandidateRegionsPerSignature { get; set; } = 5;
    public int CandidatePoolMultiplier { get; set; } = 6;
    public double MinimumInkDensityPercent { get; set; } = 0.5;
    public double MaximumInkDensityPercent { get; set; } = 35.0;
    public bool PreferLaterPages { get; set; } = true;
    public double LaterPagePreferenceWeight { get; set; } = 2.5;
}

public sealed class SignatureRoleOptions
{
    public string Mode { get; set; } = "Auto";
    public List<string> ExpectedRoles { get; set; } = new();

    public List<string> GetExpectedRoleIds()
    {
        var mode = (Mode ?? "Auto").Trim();
        if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        if (mode.Equals("Configured", StringComparison.OrdinalIgnoreCase))
        {
            return ExpectedRoles
                .Select(NormalizeRoleId)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var roles = mode.ToLowerInvariant() switch
        {
            "1" or "applicant" or "applicantonly" or "applicant_signature" => new[] { "applicant_signature" },
            "2" or "witness" or "applicantwitness" or "applicant_witness" => new[] { "applicant_signature", "witness_signature" },
            "3" or "authorized" or "authorised" or "applicantwitnessauthorized" or "applicantwitnessauthorised" or "applicant_witness_authorized" or "applicant_witness_authorised" => new[] { "applicant_signature", "witness_signature", "authorised_signature" },
            _ => Array.Empty<string>()
        };

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string NormalizeRoleId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (normalized.Contains("applicant", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("customer", StringComparison.OrdinalIgnoreCase) ||
            normalized == "signature")
        {
            return "applicant_signature";
        }

        if (normalized.Contains("witness", StringComparison.OrdinalIgnoreCase))
        {
            return "witness_signature";
        }

        if (normalized.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorised", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorisation", StringComparison.OrdinalIgnoreCase))
        {
            return "authorised_signature";
        }

        return value.Trim();
    }

    public static string GetDisplayName(string roleId) => roleId switch
    {
        "applicant_signature" => "Applicant Signature",
        "witness_signature" => "Witness Signature",
        "authorised_signature" => "Authorised Signature",
        _ => roleId
    };

    public static List<string> GetExpectedLabels(string roleId) => roleId switch
    {
        "applicant_signature" => new List<string> { "applicant signature", "signed by", "customer signature" },
        "witness_signature" => new List<string> { "witness signature", "witness" },
        "authorised_signature" => new List<string> { "authorised signature", "authorized signature", "authorised by", "authorized by" },
        _ => new List<string>()
    };

    public static List<string> MergeExpectedLabels(string roleId, IEnumerable<string> configuredLabels)
    {
        return GetExpectedLabels(roleId)
            .Concat(configuredLabels ?? Array.Empty<string>())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class KnownSignatureZone
{
    public string SignatureId { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string CoordinateSystem { get; set; } = "pixels";

    public static bool IsSupportedCoordinateSystem(string? coordinateSystem)
    {
        var value = (coordinateSystem ?? "pixels").Trim();
        return value.Equals("pixels", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("pixel", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("normalized", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("relative", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("percent", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("percentage", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("inch", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("inches", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("point", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("points", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VerificationResult
{
    public string DocumentResultId { get; set; } = Guid.NewGuid().ToString("N");
    public string EngineVersion { get; set; } = "1.0.0";
    public string InputDocumentType { get; set; } = "Unknown";
    public PdfRenderInfo? PdfRenderInfo { get; set; }
    public string OverallDecision { get; set; } = "ReviewRequired";
    public double OverallConfidence { get; set; }
    public int SignatureCountExpected { get; set; }
    public int SignatureCountDetected { get; set; }
    public List<SignatureResult> SignatureResults { get; set; } = new();
    public OperationalAudit? OperationalAudit { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<ErrorDetail> Errors { get; set; } = new();
}

public sealed class OperationalAudit
{
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public double DurationMs { get; set; }
    public string Status { get; set; } = "Completed";
    public string? ErrorCode { get; set; }
    public string InputDocumentType { get; set; } = "Unknown";
    public int PagesLoaded { get; set; }
    public int ReferenceSetCount { get; set; }
    public int ReferenceImageCount { get; set; }
    public int SignatureCountExpected { get; set; }
    public int SignatureCountDetected { get; set; }
    public int WarningsCount { get; set; }
    public int ErrorsCount { get; set; }
    public int DebugImagesSaved { get; set; }
    public bool DebugOutputEnabled { get; set; }
}

public sealed class PdfRenderInfo
{
    public string Renderer { get; set; } = "GhostscriptCommandLine";
    public int Dpi { get; set; }
    public int PagesRendered { get; set; }
    public List<int> PageIndexesRendered { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class SignatureResult
{
    public string SignatureId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public SignatureMapping? Mapping { get; set; }
    public bool IsMatched { get; set; }
    public string Decision { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public bool ReviewRequired { get; set; }
    public bool SignatureDetected { get; set; }
    public string SignatureQuality { get; set; } = "Unknown";
    public string? BestReferenceId { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public DetectedRegion? DetectedRegion { get; set; }
    public List<DetectedRegion> CandidateRegions { get; set; } = new();
    public AuditResult? Audit { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class DetectedRegion
{
    public int PageIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string CoordinateSystem { get; set; } = "pixels";
    public string Source { get; set; } = string.Empty;
    public string? AnchorText { get; set; }
    public double RegionConfidence { get; set; }
    public double InkDensityPercent { get; set; }
    public string CandidateReason { get; set; } = string.Empty;
}

public sealed class AuditResult
{
    public double FinalScore { get; set; }
    public ScoreBreakdown ScoreBreakdown { get; set; } = new();
    public SignatureMetrics QuerySignatureMetrics { get; set; } = new();
    public List<ReferenceComparison> ReferenceComparisons { get; set; } = new();
    public ScoreThresholds ThresholdsUsed { get; set; } = new();
    public ScoreWeights WeightsUsed { get; set; } = new();
    public PreprocessingAudit Preprocessing { get; set; } = new();
    public Dictionary<string, string>? DebugImages { get; set; }

    public static AuditResult Empty(SignatureVerificationOptions options) => new()
    {
        ThresholdsUsed = options.Thresholds,
        WeightsUsed = options.Weights
    };
}

public sealed class ScoreBreakdown
{
    public double GeometryScore { get; set; }
    public double InkDensityScore { get; set; }
    public double DensityGridScore { get; set; }
    public double ContourScore { get; set; }
    public double SkeletonScore { get; set; }
    public double StructuralSimilarityScore { get; set; }
    public double ConnectedComponentScore { get; set; }
    public double StructuralMismatchPenalty { get; set; }
    public double QualityScore { get; set; }
    public double QualityAdjustedScore { get; set; }
}

public sealed class ReferenceComparison
{
    public string ReferenceId { get; set; } = string.Empty;
    public string? ReferenceFileName { get; set; }
    public string? ReferenceFilePath { get; set; }
    public double Confidence { get; set; }
    public double GeometryScore { get; set; }
    public double InkDensityScore { get; set; }
    public double DensityGridScore { get; set; }
    public double ContourScore { get; set; }
    public double SkeletonScore { get; set; }
    public double StructuralSimilarityScore { get; set; }
    public double ConnectedComponentScore { get; set; }
    public double StructuralMismatchPenalty { get; set; }
    public double QualityScore { get; set; }
    public double QualityAdjustedScore { get; set; }
    public bool IsBestMatch { get; set; }

    public ScoreBreakdown ToScoreBreakdown() => new()
    {
        GeometryScore = GeometryScore,
        InkDensityScore = InkDensityScore,
        DensityGridScore = DensityGridScore,
        ContourScore = ContourScore,
        SkeletonScore = SkeletonScore,
        StructuralSimilarityScore = StructuralSimilarityScore,
        ConnectedComponentScore = ConnectedComponentScore,
        StructuralMismatchPenalty = StructuralMismatchPenalty,
        QualityScore = QualityScore,
        QualityAdjustedScore = QualityAdjustedScore
    };
}

public sealed class SignatureMetrics
{
    public int InkPixelCount { get; set; }
    public double InkDensityPercent { get; set; }
    public double BlankAreaPercent { get; set; }
    public int BoundingBoxWidth { get; set; }
    public int BoundingBoxHeight { get; set; }
    public double AspectRatio { get; set; }
    public double NormalizedArea { get; set; }
    public double CenterOfMassX { get; set; }
    public double CenterOfMassY { get; set; }
    public double BaselineAngleDegrees { get; set; }
    public double StrokeThicknessEstimate { get; set; }
    public int ConnectedComponentCount { get; set; }
    public double LargestComponentRatio { get; set; }
    public List<double> ComponentSizeDistribution { get; set; } = new();
    public double FragmentedStrokeIndicator { get; set; }
    public int SkeletonPixelCount { get; set; }
    public double SkeletonDensity { get; set; }
    public int ContourCount { get; set; }
    public double TotalContourArea { get; set; }
    public double LargestContourAreaRatio { get; set; }
    public double[] HuMoments { get; set; } = Array.Empty<double>();
    public double[] DensityGrid8x4 { get; set; } = Array.Empty<double>();
    public double[] DensityGrid16x8 { get; set; } = Array.Empty<double>();
    public double[] QuadrantInkDistribution { get; set; } = Array.Empty<double>();
    public double ImageSharpness { get; set; }
    public double NoiseEstimate { get; set; }
    public double CropConfidence { get; set; }
    public bool TooBlank { get; set; }
    public bool TooDark { get; set; }
    public bool TooNoisy { get; set; }
    public bool TooSmall { get; set; }
    public bool LineOrBoxOverlap { get; set; }
}

public sealed class PreprocessingAudit
{
    public bool DeskewApplied { get; set; }
    public double DeskewAngleDegrees { get; set; }
    public bool DenoiseApplied { get; set; }
    public string BinarizationMethod { get; set; } = "Otsu";
    public bool LineRemovalApplied { get; set; }
    public bool BoxBorderRemovalApplied { get; set; }
    public bool WhitespaceCropApplied { get; set; }
    public int NormalizedCanvasWidth { get; set; } = 512;
    public int NormalizedCanvasHeight { get; set; } = 256;
    public bool SkeletonizationApplied { get; set; }
}

public sealed class ErrorDetail
{
    public ErrorDetail()
    {
    }

    public ErrorDetail(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed record OcrLayout
{
    public List<OcrLabel> Labels { get; } = new();
}

public sealed record OcrLabel(int PageIndex, string Text, Rect Rect);

public sealed class SignatureVerificationException : Exception
{
    public SignatureVerificationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class ResultJsonBuilder
{
    public static VerificationResult CreateEmpty(string engineVersion) => new()
    {
        DocumentResultId = Guid.NewGuid().ToString("N"),
        EngineVersion = engineVersion,
        InputDocumentType = "Unknown",
        OverallDecision = "ReviewRequired"
    };

    public static VerificationResult CreateError(string engineVersion, string code, string message) => new()
    {
        DocumentResultId = Guid.NewGuid().ToString("N"),
        EngineVersion = engineVersion,
        InputDocumentType = "Unknown",
        PdfRenderInfo = null,
        OverallDecision = "Error",
        OverallConfidence = 0,
        SignatureCountExpected = 0,
        SignatureCountDetected = 0,
        SignatureResults = new List<SignatureResult>(),
        Warnings = new List<string>(),
        Errors = new List<ErrorDetail> { new(code, message) }
    };
}
