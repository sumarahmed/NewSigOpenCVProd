using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using StaticSignatureVerification.Core;
using StaticSignatureVerification.TotalAgilityWrapper;

var outputRoot = args.FirstOrDefault(arg => arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
                 ?? Path.Combine(".verification", "regression-pack");
Directory.CreateDirectory(outputRoot);
var assetRoot = Path.Combine(outputRoot, "assets");
Directory.CreateDirectory(assetRoot);

var runner = new RegressionRunner(outputRoot, assetRoot);
var cases = runner.CreateCases();
var results = new List<RegressionCaseResult>();

foreach (var testCase in cases)
{
    var started = Stopwatch.GetTimestamp();
    try
    {
        var actual = testCase.Execute();
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var failures = testCase.Expected.Evaluate(actual);
        results.Add(new RegressionCaseResult(
            testCase.Id,
            testCase.Name,
            testCase.InputDocument,
            testCase.ReferenceImage,
            testCase.Expected.DecisionLabel,
            testCase.Expected.ConfidenceRangeLabel,
            testCase.Expected.ExpectedSignatureCountLabel,
            testCase.Expected.ExpectedPageLabel,
            testCase.Expected.ExpectedWarningOrErrorLabel,
            actual.ActualDecision,
            actual.ActualSignatureDecisions,
            actual.ActualConfidence,
            actual.ActualSignatureCount,
            actual.ActualPages,
            actual.ActualWarningsAndErrors,
            elapsedMs,
            failures.Count == 0 ? "PASS" : "FAIL",
            string.Join("; ", failures)));
    }
    catch (Exception ex)
    {
        results.Add(new RegressionCaseResult(
            testCase.Id,
            testCase.Name,
            testCase.InputDocument,
            testCase.ReferenceImage,
            testCase.Expected.DecisionLabel,
            testCase.Expected.ConfidenceRangeLabel,
            testCase.Expected.ExpectedSignatureCountLabel,
            testCase.Expected.ExpectedPageLabel,
            testCase.Expected.ExpectedWarningOrErrorLabel,
            "HarnessError",
            "",
            0,
            0,
            "",
            ex.GetType().Name + ": " + ex.Message,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            "FAIL",
            ex.Message));
    }
}

WriteReports(outputRoot, results);

foreach (var result in results)
{
    Console.WriteLine($"{result.Result} {result.Id}_{result.Name} decision={result.ActualDecision} confidence={result.ActualConfidence:0.00} signatures={result.ActualNumberOfSignatures} pages={result.ActualPageNumber}");
    if (result.Result == "FAIL")
    {
        Console.WriteLine("  " + result.FailureReason);
    }
}

Console.WriteLine();
Console.WriteLine($"Regression report: {Path.GetFullPath(Path.Combine(outputRoot, "regression-report.md"))}");
Console.WriteLine($"Passed {results.Count(r => r.Result == "PASS")} / {results.Count}");
Environment.ExitCode = results.Any(r => r.Result == "FAIL") ? 1 : 0;

static void WriteReports(string outputRoot, IReadOnlyList<RegressionCaseResult> results)
{
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(outputRoot, "regression-results.json"), JsonSerializer.Serialize(results, jsonOptions));

    var csv = new StringBuilder();
    csv.AppendLine("Id,Name,InputDocument,ReferenceImage,ExpectedDecision,ExpectedConfidenceRange,ExpectedNumberOfSignatures,ExpectedPageNumber,ExpectedErrorWarning,ActualDecision,ActualSignatureDecisions,ActualConfidence,ActualNumberOfSignatures,ActualPageNumber,ActualErrorWarning,DurationMs,PassFail,FailureReason");
    foreach (var row in results)
    {
        csv.AppendLine(string.Join(",", new[]
        {
            row.Id,
            row.Name,
            row.InputDocument,
            row.ReferenceImage,
            row.ExpectedDecision,
            row.ExpectedConfidenceRange,
            row.ExpectedNumberOfSignatures,
            row.ExpectedPageNumber,
            row.ExpectedErrorWarning,
            row.ActualDecision,
            row.ActualSignatureDecisions,
            row.ActualConfidence.ToString("0.00"),
            row.ActualNumberOfSignatures.ToString(),
            row.ActualPageNumber,
            row.ActualErrorWarning,
            row.DurationMs.ToString("0.00"),
            row.Result,
            row.FailureReason
        }.Select(Csv)));
    }

    File.WriteAllText(Path.Combine(outputRoot, "regression-results.csv"), csv.ToString());

    var md = new StringBuilder();
    md.AppendLine("# Signature Verification Core Regression Pack");
    md.AppendLine();
    md.AppendLine($"Generated UTC: {DateTimeOffset.UtcNow:O}");
    md.AppendLine();
    md.AppendLine($"Summary: {results.Count(r => r.Result == "PASS")} passed / {results.Count} total.");
    md.AppendLine();
    md.AppendLine("| Test case | Input document | Reference image | Expected decision | Expected confidence range | Expected number of signatures | Expected page number | Expected error/warning | Actual decision | Actual confidence | Actual signatures | Actual page | Pass/fail result | Notes |");
    md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | --- | --- | --- |");
    foreach (var result in results)
    {
        md.AppendLine($"| `{result.Id}_{result.Name}` | `{result.InputDocument}` | `{result.ReferenceImage}` | {EscapeMd(result.ExpectedDecision)} | {EscapeMd(result.ExpectedConfidenceRange)} | {EscapeMd(result.ExpectedNumberOfSignatures)} | {EscapeMd(result.ExpectedPageNumber)} | {EscapeMd(result.ExpectedErrorWarning)} | {EscapeMd(result.ActualDecision)} / {EscapeMd(result.ActualSignatureDecisions)} | {result.ActualConfidence:0.00} | {result.ActualNumberOfSignatures} | {EscapeMd(result.ActualPageNumber)} | **{result.Result}** | {EscapeMd(result.FailureReason)} |");
    }

    File.WriteAllText(Path.Combine(outputRoot, "regression-report.md"), md.ToString());
}

static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
static string EscapeMd(string value) => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

internal sealed class RegressionRunner
{
    private readonly string _outputRoot;
    private readonly string _assetRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public RegressionRunner(string outputRoot, string assetRoot)
    {
        _outputRoot = outputRoot;
        _assetRoot = assetRoot;
    }

    public IReadOnlyList<RegressionCase> CreateCases()
    {
        return new[]
        {
            ImageCase("TC001", "GenuineSignature_Match", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Matched", 85, 100, 1, "0", "")),
            ImageCase("TC002", "DifferentSigner_NotMatch", CreatePage(("applicant_signature", 120, 250, SignatureKind.B, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("NotMatched", 0, 84.99, 1, "0", "")),
            ImageCase("TC003", "EmptySignatureBox_NoSignatureFound", CreatePage(), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("ReviewRequired", 0, 10, 0, "", "NoSignatureDetected")),
            ImageCase("TC004", "MultipleSignatures_BothMatched", CreatePage(("applicant_signature", 120, 230, SignatureKind.A, 0, false, 1.0), ("witness_signature", 120, 470, SignatureKind.B, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A), ("witness_signature", SignatureKind.B)), Zones(("applicant_signature", 0, 100, 200, 520, 220), ("witness_signature", 0, 100, 440, 520, 220)), Expect("Matched", 85, 100, 2, "0,0", "")),
            ImageCase("TC005", "MultipleSignatures_OneMatchedOneRejected", CreatePage(("applicant_signature", 120, 230, SignatureKind.A, 0, false, 1.0), ("witness_signature", 120, 470, SignatureKind.C, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A), ("witness_signature", SignatureKind.B)), Zones(("applicant_signature", 0, 100, 200, 520, 220), ("witness_signature", 0, 100, 440, 520, 220)), Expect("NotMatched", 0, 100, 2, "0,0", "NotMatched")),
            ImageCase("TC006", "RotatedPage_DetectedAndMatched", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 8, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 80, 200, 600, 280)), Expect("Matched", 75, 100, 1, "0", "")),
            ImageCase("TC007", "SkewedPage_DetectedAndMatched", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, -7, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 80, 200, 600, 280)), Expect("Matched", 75, 100, 1, "0", "")),
            ImageCase("TC008", "StampNearSignature_NotDetectedAsSignature", CreatePageWithOptions(stamp: true), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("ReviewRequired", 0, 10, 0, "", "NoSignatureDetected")),
            ImageCase("TC009", "HandwritingNearSignature_NotFalsePositive", CreatePageWithOptions(handwriting: true), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("ReviewRequired", 0, 10, 0, "", "NoSignatureDetected")),
            ImageCase("TC010", "LowResolution_Review", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 0, false, 0.38)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("ReviewRequired", 55, 99.99, 1, "0", "ReviewRequired"), OptionsMode.ReviewTight),
            ImageCase("TC011", "CroppedSignature_ReviewOrReject", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 0, true, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 360, 180)), Expect("ReviewRequired|NotMatched", 0, 99.99, 1, "0", "")),
            RawCase("TC012", "InvalidBase64_ReturnsErrorJson", "not-base64", Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Error", 0, 0, 0, "", "INVALID_BASE64_DOCUMENT")),
            PdfCase("TC013", "CorruptPdf_ReturnsErrorJson", "%PDF-corrupt"u8.ToArray(), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Error", 0, 0, 0, "", "PDF_RENDERING_FAILED"), PdfMode.Corrupt),
            PdfCase("TC014", "LargePdf_WithinLimits", "%PDF-large"u8.ToArray(), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 14, 100, 220, 520, 240)), Expect("Matched", 85, 100, 1, "14", ""), PdfMode.Large),
            RawCase("TC015", "NoReference_ReturnsErrorJson", Encode(CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 0, false, 1.0))), EmptyRefs(), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Error", 0, 0, 0, "", "REFERENCE_SIGNATURE_MISSING")),
            ImageCase("TC016", "TwoReferencesCorrectMapping", CreatePage(("applicant_signature", 120, 230, SignatureKind.A, 0, false, 1.0), ("witness_signature", 120, 470, SignatureKind.B, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A), ("witness_signature", SignatureKind.B)), Zones(("applicant_signature", 0, 100, 200, 520, 220), ("witness_signature", 0, 100, 440, 520, 220)), Expect("Matched", 85, 100, 2, "0,0", "")),
            ImageCase("TC017", "WrongReference_NotMatched", CreatePage(("applicant_signature", 120, 250, SignatureKind.B, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("NotMatched", 0, 84.99, 1, "0", "")),
            ImageCase("TC018", "ReusedCopiedSignature_HighRiskFlag", CreatePage(("applicant_signature", 120, 230, SignatureKind.A, 0, false, 1.0), ("witness_signature", 120, 470, SignatureKind.A, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A), ("witness_signature", SignatureKind.B)), Zones(("applicant_signature", 0, 100, 200, 520, 220), ("witness_signature", 0, 100, 440, 520, 220)), Expect("ReviewRequired|NotMatched", 0, 100, 2, "0,0", "REUSED_COPIED_SIGNATURE_HIGH_RISK")),
            ImageCase("TC019", "BoxBorderRemovedCorrectly", CreatePageWithOptions(new[] { ("applicant_signature", 130, 260, SignatureKind.A, 0.0, false, 1.0) }, box: (100, 220, 520, 240)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Matched", 75, 100, 1, "0", "boxBorderRemovalApplied")),
            TotalAgilityCase("TC020", "TotalAgilityMethod_ReturnsValidJsonString", CreatePage(("applicant_signature", 120, 250, SignatureKind.A, 0, false, 1.0)), Refs(("applicant_signature", SignatureKind.A)), Zones(("applicant_signature", 0, 100, 220, 520, 240)), Expect("Matched", 85, 100, 1, "0", "validJson"))
        };
    }

    private RegressionCase ImageCase(string id, string name, Mat document, string refsJson, string optionsJson, ExpectedOutcome expected, OptionsMode mode = OptionsMode.Normal)
    {
        var inputFile = SaveImage(id, "input-document.png", document);
        var referenceFile = SaveReferenceImages(id, refsJson);
        return new RegressionCase(id, name, inputFile, referenceFile, expected, () => RunImage(document, refsJson, ApplyMode(optionsJson, mode)));
    }

    private RegressionCase RawCase(string id, string name, string documentBase64, string refsJson, string optionsJson, ExpectedOutcome expected)
    {
        var inputFile = Path.Combine(_assetRoot, id + "_input.txt");
        File.WriteAllText(inputFile, documentBase64);
        var referenceFile = SaveReferenceImages(id, refsJson);
        return new RegressionCase(id, name, inputFile, referenceFile, expected, () => RunBase64(documentBase64, refsJson, optionsJson));
    }

    private RegressionCase PdfCase(string id, string name, byte[] pdfBytes, string refsJson, string optionsJson, ExpectedOutcome expected, PdfMode mode)
    {
        var inputFile = Path.Combine(_assetRoot, id + "_input.pdf");
        File.WriteAllBytes(inputFile, pdfBytes);
        var referenceFile = SaveReferenceImages(id, refsJson);
        return new RegressionCase(id, name, inputFile, referenceFile, expected, () =>
        {
            var engine = new StaticSignatureVerificationEngine();
            var result = engine.Verify(Convert.ToBase64String(pdfBytes), string.Empty, refsJson, ApplyMode(optionsJson, OptionsMode.Normal, "PDF", 20), new RegressionPdfRenderer(mode));
            return ActualOutcome.From(result);
        });
    }

    private RegressionCase TotalAgilityCase(string id, string name, Mat document, string refsJson, string optionsJson, ExpectedOutcome expected)
    {
        var inputFile = SaveImage(id, "input-document.png", document);
        var referenceFile = SaveReferenceImages(id, refsJson);
        return new RegressionCase(id, name, inputFile, referenceFile, expected, () =>
        {
            var request = JsonSerializer.Serialize(new
            {
                correlationId = id,
                documentBase64 = Encode(document),
                ocrLayoutJson = "",
                referenceSignaturesJson = refsJson,
                optionsJson
            });
            var json = new SignatureVerificationWrapper().VerifySignaturesRequest(request);
            using var _ = JsonDocument.Parse(json);
            var result = JsonSerializer.Deserialize<VerificationResult>(json, _jsonOptions) ?? throw new InvalidOperationException("Invalid wrapper result JSON.");
            var actual = ActualOutcome.From(result);
            return actual with { ActualWarningsAndErrors = Append(actual.ActualWarningsAndErrors, "validJson") };
        });
    }

    private ActualOutcome RunImage(Mat document, string refsJson, string optionsJson) =>
        RunBase64(Encode(document), refsJson, optionsJson);

    private ActualOutcome RunBase64(string documentBase64, string refsJson, string optionsJson)
    {
        var json = new SignatureVerificationWrapper().VerifySignatures(documentBase64, string.Empty, refsJson, optionsJson);
        var result = JsonSerializer.Deserialize<VerificationResult>(json, _jsonOptions) ?? throw new InvalidOperationException("Invalid result JSON.");
        return ActualOutcome.From(result);
    }

    private string SaveImage(string id, string name, Mat image)
    {
        var path = Path.Combine(_assetRoot, id + "_" + name);
        Cv2.ImWrite(path, image);
        return path;
    }

    private string SaveReferenceImages(string id, string refsJson)
    {
        var path = Path.Combine(_assetRoot, id + "_references.json");
        File.WriteAllText(path, refsJson);
        return path;
    }

    private static ExpectedOutcome Expect(string decisions, double minConfidence, double maxConfidence, int signatures, string page, string warning) =>
        new(decisions.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), minConfidence, maxConfidence, signatures, page, warning);

    private static string Encode(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string EmptyRefs() => """{"referenceSets":[]}""";

    private static string Refs(params (string Role, SignatureKind Kind)[] refs)
    {
        var sets = refs.Select(r => new
        {
            signatureId = r.Role,
            displayName = r.Role,
            referenceSetId = r.Role + "_set",
            referenceImages = new[]
            {
                new
                {
                    referenceId = r.Role + "_ref",
                    fileName = r.Role + "_reference.png",
                    imageBase64 = Encode(CreateSignature(r.Kind, 420, 160, 0, false, 1.0))
                }
            }
        });
        return JsonSerializer.Serialize(new { referenceSets = sets });
    }

    private static string Zones(params (string Role, int Page, int X, int Y, int Width, int Height)[] zones)
    {
        var knownZones = zones.Select(z => new
        {
            signatureId = z.Role,
            pageIndex = z.Page,
            x = z.X,
            y = z.Y,
            width = z.Width,
            height = z.Height,
            coordinateSystem = "pixels"
        });
        return JsonSerializer.Serialize(new
        {
            inputDocumentType = "Image",
            knownZones,
            detection = new
            {
                useKnownZones = true,
                useOcrAnchors = false,
                useBoxDetection = false,
                useInkRegionDetection = false,
                minimumInkDensityPercent = 0.15,
                maximumInkDensityPercent = 35
            },
            thresholds = new
            {
                matched = 85,
                probableMatch = 78,
                reviewRequired = 55
            }
        });
    }

    private static string ApplyMode(string optionsJson, OptionsMode mode, string inputType = "Image", int maxPages = 5)
    {
        using var doc = JsonDocument.Parse(optionsJson);
        var root = JsonSerializer.Deserialize<Dictionary<string, object?>>(doc.RootElement.GetRawText()) ?? new();
        root["inputDocumentType"] = inputType;
        root["pdfRendering"] = new { maxPages };
        if (mode == OptionsMode.ReviewTight)
        {
            root["thresholds"] = new { matched = 99.5, probableMatch = 98.0, reviewRequired = 40.0 };
        }

        return JsonSerializer.Serialize(root);
    }

    private static Mat CreatePage(params (string Role, int X, int Y, SignatureKind Kind, double Angle, bool Cropped, double Scale)[] signatures)
    {
        return CreatePageWithOptions(signatures, stamp: false, handwriting: false, box: null);
    }

    private static Mat CreatePageWithOptions(
        (string Role, int X, int Y, SignatureKind Kind, double Angle, bool Cropped, double Scale)[]? signatures = null,
        bool stamp = false,
        bool handwriting = false,
        (int X, int Y, int Width, int Height)? box = null)
    {
        var page = new Mat(new Size(1000, 760), MatType.CV_8UC1, Scalar.White);
        if (box.HasValue)
        {
            Cv2.Rectangle(page, new Rect(box.Value.X, box.Value.Y, box.Value.Width, box.Value.Height), Scalar.Black, 3);
        }

        if (stamp)
        {
            Cv2.Circle(page, new Point(710, 325), 70, Scalar.Black, 4);
            Cv2.PutText(page, "RECEIVED", new Point(640, 330), HersheyFonts.HersheySimplex, 0.8, Scalar.Black, 2);
        }

        if (handwriting)
        {
            Cv2.PutText(page, "call me after review", new Point(625, 320), HersheyFonts.HersheyScriptSimplex, 1.2, Scalar.Black, 2);
        }

        foreach (var sig in signatures ?? Array.Empty<(string, int, int, SignatureKind, double, bool, double)>())
        {
            using var signature = CreateSignature(sig.Kind, 420, 160, sig.Angle, sig.Cropped, sig.Scale);
            var x = Math.Clamp(sig.X, 0, page.Width - signature.Width);
            var y = Math.Clamp(sig.Y, 0, page.Height - signature.Height);
            using var roi = new Mat(page, new Rect(x, y, signature.Width, signature.Height));
            signature.CopyTo(roi);
        }

        return page;
    }

    private static Mat CreateSignature(SignatureKind kind, int width, int height, double angle, bool cropped, double scale)
    {
        var canvas = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.White);
        if (kind == SignatureKind.A)
        {
            Cv2.Polylines(canvas, new[]
            {
                new[]
                {
                    new Point(20, 100), new Point(70, 55), new Point(115, 112), new Point(165, 45),
                    new Point(215, 105), new Point(270, 76), new Point(330, 98), new Point(400, 70)
                }
            }, false, Scalar.Black, 5, LineTypes.AntiAlias);
            Cv2.Ellipse(canvas, new Point(120, 88), new Size(38, 26), -12, 0, 335, Scalar.Black, 3);
            Cv2.Line(canvas, new Point(42, 120), new Point(390, 120), Scalar.Black, 2, LineTypes.AntiAlias);
        }
        else if (kind == SignatureKind.B)
        {
            Cv2.Polylines(canvas, new[]
            {
                new[]
                {
                    new Point(28, 70), new Point(68, 115), new Point(110, 62), new Point(158, 122),
                    new Point(210, 56), new Point(252, 114), new Point(318, 60), new Point(392, 118)
                }
            }, false, Scalar.Black, 5, LineTypes.AntiAlias);
            Cv2.Ellipse(canvas, new Point(250, 83), new Size(48, 20), 18, 0, 360, Scalar.Black, 3);
        }
        else
        {
            Cv2.Line(canvas, new Point(40, 65), new Point(390, 65), Scalar.Black, 5, LineTypes.AntiAlias);
            Cv2.Line(canvas, new Point(60, 112), new Point(365, 112), Scalar.Black, 4, LineTypes.AntiAlias);
            Cv2.Circle(canvas, new Point(150, 90), 24, Scalar.Black, 4, LineTypes.AntiAlias);
        }

        var transformed = canvas;
        if (Math.Abs(angle) > 0.01)
        {
            var center = new Point2f(width / 2f, height / 2f);
            using var rotation = Cv2.GetRotationMatrix2D(center, angle, 1.0);
            transformed = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.White);
            Cv2.WarpAffine(canvas, transformed, rotation, transformed.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
            canvas.Dispose();
        }

        if (cropped)
        {
            using var crop = new Mat(transformed, new Rect(0, 0, (int)(transformed.Width * 0.58), transformed.Height));
            var output = crop.Clone();
            transformed.Dispose();
            transformed = output;
        }

        if (scale < 0.99)
        {
            var low = new Mat();
            Cv2.Resize(transformed, low, new Size(Math.Max(1, (int)(transformed.Width * scale)), Math.Max(1, (int)(transformed.Height * scale))), 0, 0, InterpolationFlags.Area);
            var up = new Mat();
            Cv2.Resize(low, up, transformed.Size(), 0, 0, InterpolationFlags.Nearest);
            low.Dispose();
            transformed.Dispose();
            transformed = up;
        }

        return transformed;
    }

    private static string Append(string source, string item) => string.IsNullOrWhiteSpace(source) ? item : source + "; " + item;
}

internal enum SignatureKind { A, B, C }
internal enum OptionsMode { Normal, ReviewTight }
internal enum PdfMode { Corrupt, Large }

internal sealed record RegressionCase(
    string Id,
    string Name,
    string InputDocument,
    string ReferenceImage,
    ExpectedOutcome Expected,
    Func<ActualOutcome> Execute);

internal sealed record ExpectedOutcome(
    IReadOnlyList<string> Decisions,
    double MinConfidence,
    double MaxConfidence,
    int ExpectedSignatureCount,
    string ExpectedPage,
    string ExpectedWarningOrError)
{
    public string DecisionLabel => string.Join("|", Decisions);
    public string ConfidenceRangeLabel => $"{MinConfidence:0.##}-{MaxConfidence:0.##}";
    public string ExpectedSignatureCountLabel => ExpectedSignatureCount.ToString();
    public string ExpectedPageLabel => string.IsNullOrWhiteSpace(ExpectedPage) ? "n/a" : ExpectedPage;
    public string ExpectedWarningOrErrorLabel => string.IsNullOrWhiteSpace(ExpectedWarningOrError) ? "none" : ExpectedWarningOrError;

    public List<string> Evaluate(ActualOutcome actual)
    {
        var failures = new List<string>();
        if (!Decisions.Contains(actual.ActualDecision, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"decision expected {DecisionLabel}, actual {actual.ActualDecision}");
        }

        if (actual.ActualConfidence < MinConfidence || actual.ActualConfidence > MaxConfidence)
        {
            failures.Add($"confidence expected {ConfidenceRangeLabel}, actual {actual.ActualConfidence:0.00}");
        }

        if (actual.ActualSignatureCount != ExpectedSignatureCount)
        {
            failures.Add($"signatures expected {ExpectedSignatureCount}, actual {actual.ActualSignatureCount}");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedPage) && !string.Equals(actual.ActualPages, ExpectedPage, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"page expected {ExpectedPage}, actual {actual.ActualPages}");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedWarningOrError) &&
            !actual.ActualWarningsAndErrors.Contains(ExpectedWarningOrError, StringComparison.OrdinalIgnoreCase) &&
            !actual.ActualSignatureDecisions.Contains(ExpectedWarningOrError, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"warning/error expected {ExpectedWarningOrError}, actual {actual.ActualWarningsAndErrors}");
        }

        return failures;
    }
}

internal sealed record ActualOutcome(
    string ActualDecision,
    string ActualSignatureDecisions,
    double ActualConfidence,
    int ActualSignatureCount,
    string ActualPages,
    string ActualWarningsAndErrors)
{
    public static ActualOutcome From(VerificationResult result)
    {
        var decisions = string.Join(",", result.SignatureResults.Select(r => r.Decision));
        var pages = string.Join(",", result.SignatureResults
            .Where(r => r.SignatureDetected && r.DetectedRegion is not null)
            .Select(r => r.DetectedRegion!.PageIndex.ToString())
            .DefaultIfEmpty(""));
        var warnings = new List<string>();
        warnings.AddRange(result.Warnings);
        warnings.AddRange(result.Errors.Select(e => e.Code));
        warnings.AddRange(result.SignatureResults.SelectMany(r => r.Warnings));
        warnings.AddRange(result.SignatureResults.Select(r => r.Decision));
        warnings.AddRange(result.SignatureResults
            .Where(r => r.Audit?.Preprocessing.BoxBorderRemovalApplied == true)
            .Select(_ => "boxBorderRemovalApplied"));
        return new ActualOutcome(
            result.OverallDecision,
            decisions,
            result.OverallConfidence,
            result.SignatureCountDetected,
            pages,
            string.Join("; ", warnings.Distinct()));
    }
}

internal sealed record RegressionCaseResult(
    string Id,
    string Name,
    string InputDocument,
    string ReferenceImage,
    string ExpectedDecision,
    string ExpectedConfidenceRange,
    string ExpectedNumberOfSignatures,
    string ExpectedPageNumber,
    string ExpectedErrorWarning,
    string ActualDecision,
    string ActualSignatureDecisions,
    double ActualConfidence,
    int ActualNumberOfSignatures,
    string ActualPageNumber,
    string ActualErrorWarning,
    double DurationMs,
    string Result,
    string FailureReason);

internal sealed class RegressionPdfRenderer : IPdfPageRenderer
{
    private readonly PdfMode _mode;

    public RegressionPdfRenderer(PdfMode mode)
    {
        _mode = mode;
    }

    public IReadOnlyList<RenderedPage> RenderPdfToImages(byte[] pdfBytes, PdfRenderOptions options)
    {
        if (_mode == PdfMode.Corrupt)
        {
            throw new SignatureVerificationException("PDF_RENDERING_FAILED", "Synthetic corrupt PDF could not be rendered.");
        }

        var pages = new List<RenderedPage>();
        for (var pageIndex = 0; pageIndex < 20; pageIndex++)
        {
            using var page = new Mat(new Size(1000, 760), MatType.CV_8UC1, Scalar.White);
            if (pageIndex == 14)
            {
                using var signature = CreateLargePdfSignature();
                using var roi = new Mat(page, new Rect(120, 250, signature.Width, signature.Height));
                signature.CopyTo(roi);
            }

            Cv2.ImEncode(".png", page, out var bytes);
            pages.Add(new RenderedPage
            {
                PageIndex = pageIndex,
                WidthPixels = page.Width,
                HeightPixels = page.Height,
                Dpi = options.Dpi,
                ImageBytes = bytes,
                ImageFormat = "png"
            });
        }

        return pages.Take(Math.Min(options.MaxPages, pages.Count)).ToArray();
    }

    private static Mat CreateLargePdfSignature()
    {
        var canvas = new Mat(new Size(420, 160), MatType.CV_8UC1, Scalar.White);
        Cv2.Polylines(canvas, new[]
        {
            new[]
            {
                new Point(20, 100), new Point(70, 55), new Point(115, 112), new Point(165, 45),
                new Point(215, 105), new Point(270, 76), new Point(330, 98), new Point(400, 70)
            }
        }, false, Scalar.Black, 5, LineTypes.AntiAlias);
        Cv2.Ellipse(canvas, new Point(120, 88), new Size(38, 26), -12, 0, 335, Scalar.Black, 3);
        Cv2.Line(canvas, new Point(42, 120), new Point(390, 120), Scalar.Black, 2, LineTypes.AntiAlias);
        return canvas;
    }
}
