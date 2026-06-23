using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenCvSharp;
using StaticSignatureVerification.TotalAgilityWrapper;

const string DefaultRoot = @"C:\Temp\SignatureVerification\SyntheticBenchmark";

var cli = CliOptions.Parse(args);
var command = cli.Command;
var root = cli.Get("--root", DefaultRoot);
var documentCount = cli.GetInt("--documents", 500);
var signerCount = cli.GetInt("--signers", 50);
var referencesPerSigner = cli.GetInt("--references", 3);
var seed = cli.GetInt("--seed", 20260622);
var clean = cli.Has("--clean");

try
{
    if (command is "help" or "--help" or "-h")
    {
        PrintUsage();
        return;
    }

    if (command is "generate" or "generate-run")
    {
        var generator = new SyntheticDatasetGenerator(root, documentCount, signerCount, referencesPerSigner, seed, clean);
        var manifest = generator.Generate();
        Console.WriteLine($"Generated {manifest.Documents.Count} synthetic document(s) under {root}");
    }

    if (command is "run" or "generate-run")
    {
        var runner = new BenchmarkRunner(
            root,
            new BenchmarkThresholdOverrides(
                cli.GetDouble("--matched"),
                cli.GetDouble("--probable"),
                cli.GetDouble("--review")));
        var summary = runner.Run();
        Console.WriteLine($"Benchmark complete: {summary.TotalSignatures} signature case(s), dangerousFalseAcceptRate={summary.DangerousFalseAcceptRate:P2}, hardFalseRejectRate={summary.HardFalseRejectRate:P2}, report={summary.HtmlReportPath}");
    }

    if (command is not ("generate" or "run" or "generate-run"))
    {
        PrintUsage();
        Environment.ExitCode = 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static void PrintUsage()
{
    Console.WriteLine($"""
StaticSignatureVerification.Benchmark

Commands:
  generate-run   Generate a synthetic dataset and immediately run the verifier.
  generate       Generate only.
  run            Run an existing generated dataset.

Options:
  --root <path>        Dataset root. Default: {DefaultRoot}
  --documents <n>      Number of synthetic documents. Default: 500
  --signers <n>        Number of fictional signers. Default: 50
  --references <n>     Reference samples per signer. Default: 3
  --seed <n>           Deterministic random seed. Default: 20260622
  --clean              Delete the root folder before generation.
  --matched <n>        Override the auto-match threshold while running.
  --probable <n>       Override the probable-match threshold while running.
  --review <n>         Override the review-required threshold while running.

Examples:
  dotnet run --project .\StaticSignatureVerification.Benchmark -- generate-run --documents 500 --signers 50
  dotnet run --project .\StaticSignatureVerification.Benchmark -- run --root C:\Temp\SignatureVerification\SyntheticBenchmark
""");
}

internal sealed class SyntheticDatasetGenerator
{
    private const int PageWidth = 1700;
    private const int PageHeight = 2200;
    private const int Dpi = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _root;
    private readonly int _documentCount;
    private readonly int _signerCount;
    private readonly int _referencesPerSigner;
    private readonly Random _random;

    public SyntheticDatasetGenerator(string root, int documentCount, int signerCount, int referencesPerSigner, int seed, bool clean)
    {
        _root = Path.GetFullPath(root);
        _documentCount = Math.Clamp(documentCount, 1, 10000);
        _signerCount = Math.Clamp(signerCount, 3, 10000);
        _referencesPerSigner = Math.Clamp(referencesPerSigner, 1, 20);
        _random = new Random(seed);

        if (clean && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public BenchmarkManifest Generate()
    {
        var folders = BenchmarkFolders.Create(_root);
        Directory.CreateDirectory(folders.Input);
        Directory.CreateDirectory(folders.ReferenceSignatures);
        Directory.CreateDirectory(folders.GroundTruth);
        Directory.CreateDirectory(folders.OcrLayout);
        Directory.CreateDirectory(folders.Results);
        Directory.CreateDirectory(folders.Report);

        var signers = Enumerable.Range(1, _signerCount)
            .Select(i => SyntheticSigner.Create(i, i % 3 == 0 ? "Chinese" : "English", _random))
            .ToList();

        WriteSignerReferences(folders.ReferenceSignatures, signers);

        var manifest = new BenchmarkManifest
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Root = _root,
            DocumentsRequested = _documentCount,
            Signers = signers.Count,
            ReferencesPerSigner = _referencesPerSigner
        };

        for (var i = 1; i <= _documentCount; i++)
        {
            var doc = GenerateDocument(i, signers, folders);
            manifest.Documents.Add(doc);
            File.WriteAllText(Path.Combine(folders.GroundTruth, doc.DocumentId + ".json"), JsonSerializer.Serialize(doc, JsonOptions));
        }

        File.WriteAllText(Path.Combine(folders.GroundTruth, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    private void WriteSignerReferences(string referenceRoot, IReadOnlyList<SyntheticSigner> signers)
    {
        foreach (var signer in signers)
        {
            var signerFolder = Path.Combine(referenceRoot, signer.SignerId);
            Directory.CreateDirectory(signerFolder);
            for (var i = 1; i <= _referencesPerSigner; i++)
            {
                using var reference = signer.RenderSignature(_random, SignatureRenderProfile.Reference());
                var path = Path.Combine(signerFolder, $"ref_{i:00}.png");
                Cv2.ImWrite(path, reference);
            }
        }
    }

    private GroundTruthDocument GenerateDocument(int index, IReadOnlyList<SyntheticSigner> signers, BenchmarkFolders folders)
    {
        var documentId = $"SYN_{index:000000}";
        var roleCount = PickRoleCount(index);
        var roles = SignatureRoleCatalog.Roles.Take(roleCount).ToList();
        var scenario = PickScenario(index);
        var useKnownZones = index % 4 != 0;
        var expectedSigners = signers.OrderBy(_ => _random.Next()).Take(roleCount).ToList();

        using var page = CreateBlankForm(documentId, scenario.Name);
        var labels = new List<OcrLine>();
        var zones = BuildZones(roleCount);
        var expected = new List<GroundTruthSignature>();

        for (var roleIndex = 0; roleIndex < roles.Count; roleIndex++)
        {
            var role = roles[roleIndex];
            var zone = zones[roleIndex];
            DrawRoleBox(page, role, zone);
            labels.Add(new OcrLine(role.Label, new[] { zone.X, zone.Y - 48, zone.X + 360, zone.Y - 48, zone.X + 360, zone.Y - 15, zone.X, zone.Y - 15 }));

            var expectedSigner = expectedSigners[roleIndex];
            var classification = scenario.ClassForRole(roleIndex, roles.Count);
            var actualSigner = ResolveActualSigner(classification, expectedSigner, signers);

            if (classification != ExpectedClass.Missing)
            {
                var profile = SignatureRenderProfile.ForExpectedClass(classification, _random);
                using var signature = actualSigner.RenderSignature(_random, profile);
                PasteSignature(page, signature, zone, _random);
            }

            expected.Add(new GroundTruthSignature
            {
                SignatureId = role.SignatureId,
                DisplayName = role.DisplayName,
                ExpectedClass = classification.ToString(),
                ExpectedSignerId = expectedSigner.SignerId,
                ActualSignerId = classification == ExpectedClass.Missing ? null : actualSigner.SignerId,
                Script = expectedSigner.Script,
                ReferenceImagePaths = Enumerable.Range(1, _referencesPerSigner)
                    .Select(i => Path.Combine("ReferenceSignatures", expectedSigner.SignerId, $"ref_{i:00}.png"))
                    .ToList(),
                Zone = new GroundTruthZone
                {
                    PageIndex = 0,
                    X = zone.X,
                    Y = zone.Y,
                    Width = zone.Width,
                    Height = zone.Height,
                    CoordinateSystem = "pixels"
                }
            });
        }

        ApplyDocumentDegradation(page, scenario, _random);

        var documentPath = Path.Combine(folders.Input, documentId + ".png");
        Cv2.ImWrite(documentPath, page);

        var ocr = CreateOcrJson(labels);
        var ocrPath = Path.Combine(folders.OcrLayout, documentId + ".layout.json");
        File.WriteAllText(ocrPath, ocr);

        var optionsPath = Path.Combine(folders.GroundTruth, documentId + ".options.json");
        var optionsJson = CreateOptionsJson(roles, expected, useKnownZones);
        File.WriteAllText(optionsPath, optionsJson);

        return new GroundTruthDocument
        {
            DocumentId = documentId,
            Scenario = scenario.Name,
            FormRoleMode = SignatureRoleCatalog.ModeForRoleCount(roleCount),
            InputDocumentPath = Path.Combine("Input", documentId + ".png"),
            OcrLayoutPath = Path.Combine("OcrLayout", documentId + ".layout.json"),
            OptionsPath = Path.Combine("GroundTruth", documentId + ".options.json"),
            UsesKnownZones = useKnownZones,
            ExpectedSignatures = expected
        };
    }

    private static int PickRoleCount(int index) => (index % 5) switch
    {
        0 => 3,
        1 => 1,
        _ => 2
    };

    private SyntheticScenario PickScenario(int index)
    {
        return (index % 10) switch
        {
            0 => SyntheticScenario.DuplicateRole(),
            1 or 2 or 3 => SyntheticScenario.CleanMatch(),
            4 or 5 => SyntheticScenario.PoorQuality(),
            6 or 7 => SyntheticScenario.NegativeMismatch(),
            8 => SyntheticScenario.Missing(),
            _ => SyntheticScenario.WrongField()
        };
    }

    private SyntheticSigner ResolveActualSigner(ExpectedClass classification, SyntheticSigner expectedSigner, IReadOnlyList<SyntheticSigner> signers)
    {
        if (classification is ExpectedClass.Accept or ExpectedClass.Review)
        {
            return expectedSigner;
        }

        if (classification == ExpectedClass.Missing)
        {
            return expectedSigner;
        }

        return signers.Where(s => s.SignerId != expectedSigner.SignerId).OrderBy(_ => _random.Next()).First();
    }

    private static Mat CreateBlankForm(string documentId, string scenario)
    {
        var page = new Mat(new Size(PageWidth, PageHeight), MatType.CV_8UC1, Scalar.White);
        Cv2.PutText(page, "Synthetic Signature Verification Benchmark", new Point(90, 110), HersheyFonts.HersheySimplex, 1.35, Scalar.Black, 3, LineTypes.AntiAlias);
        Cv2.PutText(page, documentId, new Point(90, 170), HersheyFonts.HersheySimplex, 0.85, Scalar.Black, 2, LineTypes.AntiAlias);
        Cv2.PutText(page, "Scenario: " + scenario, new Point(90, 220), HersheyFonts.HersheySimplex, 0.75, new Scalar(40), 2, LineTypes.AntiAlias);
        for (var y = 330; y < 1550; y += 105)
        {
            Cv2.Line(page, new Point(90, y), new Point(PageWidth - 90, y), new Scalar(210), 1, LineTypes.AntiAlias);
        }

        return page;
    }

    private static List<GroundTruthZone> BuildZones(int roleCount)
    {
        return roleCount switch
        {
            1 => new List<GroundTruthZone>
            {
                new() { X = 360, Y = 1780, Width = 980, Height = 220, CoordinateSystem = "pixels" }
            },
            2 => new List<GroundTruthZone>
            {
                new() { X = 170, Y = 1780, Width = 610, Height = 210, CoordinateSystem = "pixels" },
                new() { X = 920, Y = 1780, Width = 610, Height = 210, CoordinateSystem = "pixels" }
            },
            _ => new List<GroundTruthZone>
            {
                new() { X = 120, Y = 1720, Width = 460, Height = 190, CoordinateSystem = "pixels" },
                new() { X = 620, Y = 1720, Width = 460, Height = 190, CoordinateSystem = "pixels" },
                new() { X = 1120, Y = 1720, Width = 460, Height = 190, CoordinateSystem = "pixels" }
            }
        };
    }

    private static void DrawRoleBox(Mat page, SignatureRole role, GroundTruthZone zone)
    {
        Cv2.PutText(page, role.Label, new Point((int)zone.X, (int)zone.Y - 22), HersheyFonts.HersheySimplex, 0.72, Scalar.Black, 2, LineTypes.AntiAlias);
        Cv2.Rectangle(page, new Rect((int)zone.X, (int)zone.Y, (int)zone.Width, (int)zone.Height), new Scalar(80), 2, LineTypes.AntiAlias);
    }

    private static void PasteSignature(Mat page, Mat signature, GroundTruthZone zone, Random random)
    {
        var maxW = Math.Max(1, (int)(zone.Width * 0.82));
        var maxH = Math.Max(1, (int)(zone.Height * 0.62));
        var scale = Math.Min(maxW / (double)signature.Width, maxH / (double)signature.Height);
        var width = Math.Max(1, (int)Math.Round(signature.Width * scale * (0.88 + random.NextDouble() * 0.2)));
        var height = Math.Max(1, (int)Math.Round(signature.Height * scale * (0.88 + random.NextDouble() * 0.2)));
        using var resized = new Mat();
        Cv2.Resize(signature, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);

        var x = (int)zone.X + Math.Max(6, ((int)zone.Width - width) / 2 + random.Next(-18, 19));
        var y = (int)zone.Y + Math.Max(6, ((int)zone.Height - height) / 2 + random.Next(-12, 13));
        x = Math.Clamp(x, 0, page.Width - width);
        y = Math.Clamp(y, 0, page.Height - height);
        using var roi = new Mat(page, new Rect(x, y, width, height));
        resized.CopyTo(roi);
    }

    private static void ApplyDocumentDegradation(Mat page, SyntheticScenario scenario, Random random)
    {
        if (scenario.Name.Contains("Poor", StringComparison.OrdinalIgnoreCase) || random.NextDouble() < 0.18)
        {
            Cv2.GaussianBlur(page, page, new Size(3, 3), 0);
        }

        if (random.NextDouble() < 0.35)
        {
            using var noise = new Mat(page.Size(), MatType.CV_8UC1);
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(random.Next(4, 16)));
            Cv2.Add(page, noise, page);
        }

        if (random.NextDouble() < 0.18)
        {
            using var encodedPage = EncodeDecodePng(page);
            encodedPage.CopyTo(page);
        }
    }

    private static Mat EncodeDecodePng(Mat page)
    {
        Cv2.ImEncode(".png", page, out var bytes);
        return Cv2.ImDecode(bytes, ImreadModes.Grayscale);
    }

    private static string CreateOcrJson(IReadOnlyList<OcrLine> lines)
    {
        var lineItems = lines.Select(line => new
        {
            content = line.Text,
            polygon = line.Polygon
        });

        var root = new
        {
            analyzeResult = new
            {
                pages = new[]
                {
                    new
                    {
                        pageNumber = 1,
                        width = PageWidth,
                        height = PageHeight,
                        unit = "pixel",
                        lines = lineItems
                    }
                }
            }
        };

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    private static string CreateOptionsJson(IReadOnlyList<SignatureRole> roles, IReadOnlyList<GroundTruthSignature> signatures, bool useKnownZones)
    {
        var options = new
        {
            inputDocumentType = "Image",
            signatureRoles = new
            {
                mode = SignatureRoleCatalog.ModeForRoleCount(roles.Count)
            },
            thresholds = new
            {
                matched = 85,
                probableMatch = 70,
                reviewRequired = 55
            },
            detection = new
            {
                useKnownZones,
                useOcrAnchors = !useKnownZones,
                useBoxDetection = true,
                useInkRegionDetection = true,
                maxCandidateRegionsPerSignature = 8,
                candidatePoolMultiplier = 8,
                minimumInkDensityPercent = 0.15,
                maximumInkDensityPercent = 60,
                preferLaterPages = false
            },
            knownZones = useKnownZones
                ? signatures.Select(sig => new
                {
                    signatureId = sig.SignatureId,
                    pageIndex = sig.Zone.PageIndex,
                    x = sig.Zone.X,
                    y = sig.Zone.Y,
                    width = sig.Zone.Width,
                    height = sig.Zone.Height,
                    coordinateSystem = sig.Zone.CoordinateSystem
                }).ToArray()
                : Array.Empty<object>()
        };

        return JsonSerializer.Serialize(options, JsonOptions);
    }
}

internal sealed class BenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _root;
    private readonly BenchmarkThresholdOverrides _thresholdOverrides;

    public BenchmarkRunner(string root, BenchmarkThresholdOverrides? thresholdOverrides = null)
    {
        _root = Path.GetFullPath(root);
        _thresholdOverrides = thresholdOverrides ?? new BenchmarkThresholdOverrides(null, null, null);
    }

    public BenchmarkSummary Run()
    {
        var folders = BenchmarkFolders.Create(_root);
        Directory.CreateDirectory(folders.Results);
        Directory.CreateDirectory(folders.Report);

        var groundTruthFiles = Directory.EnumerateFiles(folders.GroundTruth, "SYN_*.json", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Contains(".options.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groundTruthFiles.Count == 0)
        {
            throw new InvalidOperationException($"No generated ground-truth files were found in {folders.GroundTruth}.");
        }

        var wrapper = new SignatureVerificationWrapper();
        var rows = new List<BenchmarkRow>();

        foreach (var groundTruthFile in groundTruthFiles)
        {
            var groundTruth = JsonSerializer.Deserialize<GroundTruthDocument>(File.ReadAllText(groundTruthFile), JsonOptions)
                              ?? throw new InvalidOperationException($"Could not read {groundTruthFile}.");
            var documentPath = FullPath(groundTruth.InputDocumentPath);
            var ocrPath = FullPath(groundTruth.OcrLayoutPath);
            var optionsPath = FullPath(groundTruth.OptionsPath);

            var referencesJson = BuildReferenceJson(groundTruth);
            var optionsJson = ApplyThresholdOverrides(File.ReadAllText(optionsPath));
            var documentStopwatch = Stopwatch.StartNew();
            var resultJson = wrapper.VerifySignatures(
                Convert.ToBase64String(File.ReadAllBytes(documentPath)),
                File.ReadAllText(ocrPath),
                referencesJson,
                optionsJson);
            documentStopwatch.Stop();
            var documentProcessingMs = Math.Round(documentStopwatch.Elapsed.TotalMilliseconds, 2);

            var resultPath = Path.Combine(folders.Results, groundTruth.DocumentId + ".result.json");
            File.WriteAllText(resultPath, resultJson);
            using var resultDoc = JsonDocument.Parse(resultJson);

            foreach (var expected in groundTruth.ExpectedSignatures)
            {
                rows.Add(BuildRow(groundTruth, expected, resultDoc.RootElement, documentPath, resultPath, groundTruthFile, ocrPath, documentProcessingMs));
            }
        }

        var summary = BenchmarkSummary.FromRows(rows);
        summary.Root = _root;
        summary.GeneratedUtc = DateTimeOffset.UtcNow;
        summary.RunMatchedThresholdOverride = _thresholdOverrides.Matched;
        summary.RunProbableThresholdOverride = _thresholdOverrides.Probable;
        summary.RunReviewThresholdOverride = _thresholdOverrides.Review;
        summary.CsvReportPath = Path.Combine(folders.Report, "benchmark-results.csv");
        summary.JsonReportPath = Path.Combine(folders.Report, "benchmark-summary.json");
        summary.HtmlReportPath = Path.Combine(folders.Report, "benchmark-report.html");

        File.WriteAllText(summary.CsvReportPath, CsvReportWriter.Write(rows));
        File.WriteAllText(summary.JsonReportPath, JsonSerializer.Serialize(summary, JsonOptions));
        File.WriteAllText(summary.HtmlReportPath, HtmlReportWriter.Write(summary, rows));

        return summary;
    }

    private string FullPath(string relativeOrAbsolutePath) =>
        Path.IsPathRooted(relativeOrAbsolutePath) ? relativeOrAbsolutePath : Path.Combine(_root, relativeOrAbsolutePath);

    private string ApplyThresholdOverrides(string optionsJson)
    {
        if (!_thresholdOverrides.HasAny)
        {
            return optionsJson;
        }

        var rootNode = JsonNode.Parse(optionsJson)?.AsObject()
                       ?? throw new InvalidOperationException("Options JSON must be an object.");
        var thresholds = rootNode["thresholds"] as JsonObject;
        if (thresholds is null)
        {
            thresholds = new JsonObject();
            rootNode["thresholds"] = thresholds;
        }

        if (_thresholdOverrides.Matched.HasValue)
        {
            thresholds["matched"] = _thresholdOverrides.Matched.Value;
        }

        if (_thresholdOverrides.Probable.HasValue)
        {
            thresholds["probableMatch"] = _thresholdOverrides.Probable.Value;
        }

        if (_thresholdOverrides.Review.HasValue)
        {
            thresholds["reviewRequired"] = _thresholdOverrides.Review.Value;
        }

        return rootNode.ToJsonString(JsonOptions);
    }

    private string BuildReferenceJson(GroundTruthDocument groundTruth)
    {
        var sets = groundTruth.ExpectedSignatures.Select(expected => new
        {
            signatureId = expected.SignatureId,
            displayName = expected.DisplayName,
            partyId = expected.ExpectedSignerId,
            partyName = expected.DisplayName,
            referenceSetId = expected.ExpectedSignerId,
            expectedSignerId = expected.ExpectedSignerId,
            actualSignerId = expected.ActualSignerId,
            expectedClass = expected.ExpectedClass,
            mapping = new
            {
                signatureId = expected.SignatureId,
                displayName = expected.DisplayName,
                partyId = expected.ExpectedSignerId,
                partyName = expected.DisplayName,
                referenceSetId = expected.ExpectedSignerId,
                expectedSignerId = expected.ExpectedSignerId,
                actualSignerId = expected.ActualSignerId,
                expectedClass = expected.ExpectedClass,
                source = "SyntheticGroundTruth"
            },
            expectedLabels = SignatureRoleCatalog.LabelsFor(expected.SignatureId),
            referenceImages = expected.ReferenceImagePaths.Select((path, index) =>
            {
                var fullPath = FullPath(path);
                return new
                {
                    referenceId = $"{expected.SignatureId}_ref_{index + 1}",
                    sourceFileName = Path.GetFileName(fullPath),
                    sourceFilePath = fullPath,
                    imageBase64 = Convert.ToBase64String(File.ReadAllBytes(fullPath))
                };
            })
        });

        return JsonSerializer.Serialize(new { referenceSets = sets }, JsonOptions);
    }

    private static BenchmarkRow BuildRow(
        GroundTruthDocument groundTruth,
        GroundTruthSignature expected,
        JsonElement resultRoot,
        string documentPath,
        string resultPath,
        string groundTruthPath,
        string ocrLayoutPath,
        double documentProcessingMs)
    {
        var result = resultRoot.GetProperty("signatureResults")
            .EnumerateArray()
            .FirstOrDefault(item => string.Equals(item.GetProperty("signatureId").GetString(), expected.SignatureId, StringComparison.OrdinalIgnoreCase));

        var found = result.ValueKind != JsonValueKind.Undefined;
        var detected = found && result.TryGetProperty("signatureDetected", out var detectedNode) && detectedNode.GetBoolean();
        var matched = found && result.TryGetProperty("isMatched", out var matchedNode) && matchedNode.GetBoolean();
        var decision = found ? result.GetProperty("decision").GetString() ?? string.Empty : "MissingResult";
        var confidence = found && result.TryGetProperty("confidence", out var confidenceNode) ? confidenceNode.GetDouble() : 0;
        var bestReference = TryGetString(result, "bestReferenceId");
        var bestReferenceFile = TryGetBestReferenceFile(result);
        var warnings = found && result.TryGetProperty("warnings", out var warningsNode) && warningsNode.ValueKind == JsonValueKind.Array
            ? string.Join("|", warningsNode.EnumerateArray().Select(w => w.GetString()))
            : string.Empty;

        return new BenchmarkRow
        {
            DocumentId = groundTruth.DocumentId,
            DocumentPath = documentPath,
            ResultPath = resultPath,
            GroundTruthPath = groundTruthPath,
            OcrLayoutPath = ocrLayoutPath,
            DocumentProcessingMs = documentProcessingMs,
            Scenario = groundTruth.Scenario,
            UsesKnownZones = groundTruth.UsesKnownZones,
            SignatureId = expected.SignatureId,
            Script = expected.Script,
            ExpectedClass = expected.ExpectedClass,
            ExpectedSignerId = expected.ExpectedSignerId,
            ActualSignerId = expected.ActualSignerId,
            EngineDecision = decision,
            EngineDetected = detected,
            EngineMatched = matched,
            Confidence = Math.Round(confidence, 2),
            BestReferenceId = bestReference,
            BestReferenceFileName = bestReferenceFile,
            Warnings = warnings,
            Outcome = ClassifyOutcome(expected.ExpectedClass, detected, matched, decision)
        };
    }

    private static string ClassifyOutcome(string expectedClass, bool detected, bool matched, string decision)
    {
        var autoAccepted = IsAutoAcceptDecision(decision);
        var reviewDecision = IsReviewDecision(decision);

        if (expectedClass == ExpectedClass.Accept.ToString())
        {
            if (autoAccepted)
            {
                return "TrueAccept";
            }

            return reviewDecision ? "AcceptNeedsReview" : "FalseReject";
        }

        if (expectedClass == ExpectedClass.Reject.ToString())
        {
            if (autoAccepted)
            {
                return "DangerousFalseAccept";
            }

            return reviewDecision ? "RejectNeedsReview" : "TrueReject";
        }

        if (expectedClass == ExpectedClass.Missing.ToString())
        {
            if (!detected)
            {
                return "CorrectMissing";
            }

            return autoAccepted ? "DangerousFalseAccept" : "MissingNeedsReview";
        }

        if (expectedClass == ExpectedClass.Review.ToString())
        {
            return reviewDecision ? "CorrectReview" : autoAccepted ? "AcceptedReviewCase" : "RejectedReviewCase";
        }

        return "Unknown";
    }

    private static bool IsAutoAcceptDecision(string decision) =>
        decision == "Matched";

    private static bool IsReviewDecision(string decision) =>
        decision is "ReviewRequired" or "ProbableMatch" or "InsufficientQuality";

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind != JsonValueKind.Undefined &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryGetBestReferenceFile(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined ||
            !result.TryGetProperty("audit", out var audit) ||
            audit.ValueKind != JsonValueKind.Object ||
            !audit.TryGetProperty("referenceComparisons", out var comparisons) ||
            comparisons.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var best = comparisons.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("isBestMatch", out var isBestMatch) && isBestMatch.ValueKind == JsonValueKind.True);
        if (best.ValueKind == JsonValueKind.Undefined)
        {
            best = comparisons.EnumerateArray().FirstOrDefault();
        }

        return TryGetString(best, "referenceFileName");
    }
}

internal static class CsvReportWriter
{
    public static string Write(IReadOnlyList<BenchmarkRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("documentId,documentPath,resultPath,groundTruthPath,ocrLayoutPath,documentProcessingMs,scenario,usesKnownZones,signatureId,script,expectedClass,expectedSignerId,actualSignerId,engineDecision,engineDetected,engineMatched,confidence,bestReferenceId,bestReferenceFileName,warnings,outcome");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Esc(row.DocumentId),
                Esc(row.DocumentPath),
                Esc(row.ResultPath),
                Esc(row.GroundTruthPath),
                Esc(row.OcrLayoutPath),
                row.DocumentProcessingMs.ToString("0.00", CultureInfo.InvariantCulture),
                Esc(row.Scenario),
                row.UsesKnownZones.ToString(CultureInfo.InvariantCulture),
                Esc(row.SignatureId),
                Esc(row.Script),
                Esc(row.ExpectedClass),
                Esc(row.ExpectedSignerId),
                Esc(row.ActualSignerId),
                Esc(row.EngineDecision),
                row.EngineDetected.ToString(CultureInfo.InvariantCulture),
                row.EngineMatched.ToString(CultureInfo.InvariantCulture),
                row.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
                Esc(row.BestReferenceId),
                Esc(row.BestReferenceFileName),
                Esc(row.Warnings),
                Esc(row.Outcome)
            }));
        }

        return sb.ToString();
    }

    private static string Esc(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class HtmlReportWriter
{
    public static string Write(BenchmarkSummary summary, IReadOnlyList<BenchmarkRow> rows)
    {
        var worstRows = rows
            .Where(row => row.Outcome is "DangerousFalseAccept" or "FalseReject" or "MissingNeedsReview" or "RejectedReviewCase")
            .Take(250)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Signature Verification Benchmark</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 32px; color: #202124; }
    h1 { font-size: 28px; margin-bottom: 8px; }
    .grid { display: grid; grid-template-columns: repeat(4, minmax(140px, 1fr)); gap: 12px; max-width: 1100px; }
    .metric { border: 1px solid #d0d7de; border-radius: 6px; padding: 12px; }
    .metric b { display: block; font-size: 22px; margin-top: 4px; }
    table { border-collapse: collapse; width: 100%; margin-top: 20px; font-size: 13px; }
    th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }
    th { background: #f6f8fa; }
    .bad { color: #b42318; font-weight: 600; }
    .good { color: #067647; font-weight: 600; }
  </style>
</head>
<body>
""");
        sb.AppendLine($"<h1>Signature Verification Benchmark</h1>");
        sb.AppendLine($"<p>Root: {Html(summary.Root)}<br>Generated: {summary.GeneratedUtc:u}</p>");
        sb.AppendLine("<div class=\"grid\">");
        Metric(sb, "Documents", summary.TotalDocuments.ToString(CultureInfo.InvariantCulture));
        Metric(sb, "Signature cases", summary.TotalSignatures.ToString(CultureInfo.InvariantCulture));
        Metric(sb, "Dangerous false accept rate", summary.DangerousFalseAcceptRate.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Hard false reject rate", summary.HardFalseRejectRate.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Detection rate", summary.DetectionRate.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Review rate", summary.ReviewRate.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Auto accept recall", summary.AutoAcceptRecall.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Missing needs review rate", summary.MissingNeedsReviewRate.ToString("P2", CultureInfo.InvariantCulture));
        Metric(sb, "Avg positive confidence", summary.AveragePositiveConfidence.ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "Avg negative confidence", summary.AverageNegativeConfidence.ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "Avg doc ms", summary.AverageDocumentProcessingMs.ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "P95 doc ms", summary.P95DocumentProcessingMs.ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "Total processing ms", summary.TotalProcessingMs.ToString("0.00", CultureInfo.InvariantCulture));
        sb.AppendLine("</div>");
        if (summary.RunMatchedThresholdOverride.HasValue || summary.RunProbableThresholdOverride.HasValue || summary.RunReviewThresholdOverride.HasValue)
        {
            sb.AppendLine($"<p><b>Run threshold overrides:</b> Matched={Html(FormatOptional(summary.RunMatchedThresholdOverride))}, Probable={Html(FormatOptional(summary.RunProbableThresholdOverride))}, Review={Html(FormatOptional(summary.RunReviewThresholdOverride))}</p>");
        }

        sb.AppendLine("<p><b>Definitions:</b> Dangerous false accept means a reject/missing case was accepted as matched. Hard false reject means an accept case was missed or rejected, not merely sent to review. NeedsReview outcomes are counted separately.</p>");
        sb.AppendLine("<h2>Suggested Thresholds</h2>");
        sb.AppendLine($"<p>Matched: <b>{summary.SuggestedMatchedThreshold:0.00}</b>, Probable: <b>{summary.SuggestedProbableThreshold:0.00}</b>, Review: <b>{summary.SuggestedReviewThreshold:0.00}</b></p>");
        sb.AppendLine("<h2>Threshold Sweep</h2>");
        sb.AppendLine("<table><thead><tr><th>Matched threshold</th><th>True accepts</th><th>Accept needs review/reject</th><th>Dangerous false accepts</th><th>Auto accept recall</th><th>Dangerous false accept rate</th></tr></thead><tbody>");
        foreach (var sweep in summary.ThresholdSweep)
        {
            sb.AppendLine($"<tr><td>{sweep.MatchedThreshold:0.00}</td><td>{sweep.TrueAccepts}</td><td>{sweep.AcceptNotAutoAccepted}</td><td>{sweep.DangerousFalseAccepts}</td><td>{sweep.AutoAcceptRecall:P2}</td><td>{sweep.DangerousFalseAcceptRate:P2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("<h2>Review Threshold Sweep</h2>");
        sb.AppendLine("<p>This table keeps auto-match separate. It shows how lowering the review threshold converts expected-accept hard rejects into review cases, and how many reject/missing cases would also be sent to review.</p>");
        sb.AppendLine("<table><thead><tr><th>Review threshold</th><th>Hard false rejects</th><th>Hard false reject rate</th><th>Accept cases sent to review</th><th>Reject/missing cases sent to review</th><th>Reject/missing review rate</th></tr></thead><tbody>");
        foreach (var sweep in summary.ReviewThresholdSweep)
        {
            sb.AppendLine($"<tr><td>{sweep.ReviewThreshold:0.00}</td><td>{sweep.HardFalseRejects}</td><td>{sweep.HardFalseRejectRate:P2}</td><td>{sweep.AcceptNeedsReview}</td><td>{sweep.NegativeNeedsReview}</td><td>{sweep.NegativeNeedsReviewRate:P2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        WriteGroupTable(sb, "By Scenario", summary.ScenarioSummaries);
        WriteGroupTable(sb, "By Role", summary.RoleSummaries);
        WriteGroupTable(sb, "By Script", summary.ScriptSummaries);
        sb.AppendLine("<h2>Failure/Attention Cases</h2>");
        sb.AppendLine("<table><thead><tr><th>Document</th><th>Files</th><th>Scenario</th><th>Role</th><th>Expected</th><th>Decision</th><th>Detected</th><th>Matched</th><th>Confidence</th><th>Outcome</th><th>Warnings</th></tr></thead><tbody>");
        foreach (var row in worstRows)
        {
            var cls = row.Outcome is "DangerousFalseAccept" or "FalseReject" ? "bad" : string.Empty;
            sb.AppendLine($"<tr><td><a href=\"{FileUri(row.DocumentPath)}\">{Html(row.DocumentId)}</a></td><td>{FileLink(row.DocumentPath, "image")} | {FileLink(row.ResultPath, "result")} | {FileLink(row.GroundTruthPath, "truth")} | {FileLink(row.OcrLayoutPath, "ocr")}</td><td>{Html(row.Scenario)}</td><td>{Html(row.SignatureId)}</td><td>{Html(row.ExpectedClass)}</td><td>{Html(row.EngineDecision)}</td><td>{row.EngineDetected}</td><td>{row.EngineMatched}</td><td>{row.Confidence:0.00}</td><td class=\"{cls}\">{Html(row.Outcome)}</td><td>{Html(row.Warnings)}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine("<h2>All Signature Cases</h2>");
        sb.AppendLine("<table><thead><tr><th>Document</th><th>Files</th><th>Scenario</th><th>Role</th><th>Expected</th><th>Decision</th><th>Detected</th><th>Matched</th><th>Confidence</th><th>Outcome</th><th>Warnings</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            var cls = row.Outcome is "DangerousFalseAccept" or "FalseReject" ? "bad" : row.Outcome is "TrueAccept" or "TrueReject" or "CorrectMissing" or "CorrectReview" ? "good" : string.Empty;
            sb.AppendLine($"<tr><td><a href=\"{FileUri(row.DocumentPath)}\">{Html(row.DocumentId)}</a></td><td>{FileLink(row.DocumentPath, "image")} | {FileLink(row.ResultPath, "result")} | {FileLink(row.GroundTruthPath, "truth")} | {FileLink(row.OcrLayoutPath, "ocr")}</td><td>{Html(row.Scenario)}</td><td>{Html(row.SignatureId)}</td><td>{Html(row.ExpectedClass)}</td><td>{Html(row.EngineDecision)}</td><td>{row.EngineDetected}</td><td>{row.EngineMatched}</td><td>{row.Confidence:0.00}</td><td class=\"{cls}\">{Html(row.Outcome)}</td><td>{Html(row.Warnings)}</td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static void Metric(StringBuilder sb, string name, string value) =>
        sb.AppendLine($"<div class=\"metric\">{Html(name)}<b>{Html(value)}</b></div>");

    private static string FormatOptional(double? value) =>
        value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "default";

    private static void WriteGroupTable(StringBuilder sb, string title, IReadOnlyList<GroupSummary> groups)
    {
        sb.AppendLine($"<h2>{Html(title)}</h2>");
        sb.AppendLine("<table><thead><tr><th>Group</th><th>Cases</th><th>Detected</th><th>True accepts</th><th>Needs review</th><th>Dangerous false accepts</th><th>Hard false rejects</th><th>Average confidence</th></tr></thead><tbody>");
        foreach (var group in groups)
        {
            sb.AppendLine($"<tr><td>{Html(group.Name)}</td><td>{group.Total}</td><td>{group.Detected}</td><td>{group.TrueAccepts}</td><td>{group.NeedsReview}</td><td>{group.DangerousFalseAccepts}</td><td>{group.HardFalseRejects}</td><td>{group.AverageConfidence:0.00}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FileLink(string? path, string label) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : $"<a href=\"{FileUri(path)}\">{Html(label)}</a>";

    private static string FileUri(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Html(new Uri(Path.GetFullPath(path)).AbsoluteUri);
}

internal sealed class SyntheticSigner
{
    public string SignerId { get; init; } = string.Empty;
    public string Script { get; init; } = "English";
    public List<SyntheticStroke> Strokes { get; init; } = new();

    public static SyntheticSigner Create(int index, string script, Random random)
    {
        return new SyntheticSigner
        {
            SignerId = $"SIGNER_{index:0000}_{script.ToUpperInvariant()}",
            Script = script,
            Strokes = script == "Chinese" ? CreateChineseStrokes(random) : CreateEnglishStrokes(random)
        };
    }

    public Mat RenderSignature(Random random, SignatureRenderProfile profile)
    {
        var image = new Mat(new Size(460, 180), MatType.CV_8UC1, Scalar.White);
        foreach (var stroke in Strokes)
        {
            var points = stroke.Points
                .Select(p => new Point(
                    (int)Math.Round(p.X + random.NextDouble() * profile.Jitter - profile.Jitter / 2.0),
                    (int)Math.Round(p.Y + random.NextDouble() * profile.Jitter - profile.Jitter / 2.0)))
                .ToArray();
            if (points.Length >= 2)
            {
                Cv2.Polylines(image, new[] { points }, false, new Scalar(profile.InkGray), profile.Thickness, LineTypes.AntiAlias);
            }
        }

        if (profile.RotateDegrees != 0)
        {
            using var matrix = Cv2.GetRotationMatrix2D(new Point2f(image.Width / 2f, image.Height / 2f), profile.RotateDegrees, 1.0);
            var rotated = new Mat();
            Cv2.WarpAffine(image, rotated, matrix, image.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
            image.Dispose();
            image = rotated;
        }

        if (profile.Blur)
        {
            Cv2.GaussianBlur(image, image, new Size(3, 3), 0);
        }

        if (profile.NoiseStdDev > 0)
        {
            using var noise = new Mat(image.Size(), MatType.CV_8UC1);
            Cv2.Randn(noise, Scalar.All(0), Scalar.All(profile.NoiseStdDev));
            Cv2.Add(image, noise, image);
        }

        return image;
    }

    private static List<SyntheticStroke> CreateEnglishStrokes(Random random)
    {
        var strokes = new List<SyntheticStroke>();
        var points = new List<Point2f>();
        var x = 25f;
        var baseline = 108 + random.Next(-16, 17);
        for (var i = 0; i < 11; i++)
        {
            var y = baseline + (float)Math.Sin(i * 1.15 + random.NextDouble()) * random.Next(24, 48);
            points.Add(new Point2f(x, y));
            x += random.Next(28, 48);
        }

        strokes.Add(new SyntheticStroke(points));
        for (var loop = 0; loop < random.Next(1, 4); loop++)
        {
            var cx = random.Next(70, 360);
            var cy = random.Next(72, 118);
            strokes.Add(new SyntheticStroke(Enumerable.Range(0, 18)
                .Select(i =>
                {
                    var angle = i / 17.0 * Math.PI * 2;
                    return new Point2f((float)(cx + Math.Cos(angle) * random.Next(18, 34)), (float)(cy + Math.Sin(angle) * random.Next(10, 24)));
                })
                .ToList()));
        }

        strokes.Add(new SyntheticStroke(new List<Point2f> { new(35, 138 + random.Next(-7, 8)), new(420, 136 + random.Next(-8, 9)) }));
        return strokes;
    }

    private static List<SyntheticStroke> CreateChineseStrokes(Random random)
    {
        var strokes = new List<SyntheticStroke>();
        var characters = random.Next(2, 4);
        for (var c = 0; c < characters; c++)
        {
            var originX = 35 + c * 135 + random.Next(-8, 9);
            var originY = 32 + random.Next(-6, 7);
            var size = random.Next(76, 108);
            strokes.Add(new SyntheticStroke(new List<Point2f> { new(originX, originY + size * 0.18f), new(originX + size, originY + size * 0.1f) }));
            strokes.Add(new SyntheticStroke(new List<Point2f> { new(originX + size * 0.45f, originY), new(originX + size * 0.38f, originY + size) }));
            strokes.Add(new SyntheticStroke(new List<Point2f> { new(originX + size * 0.18f, originY + size * 0.48f), new(originX + size * 0.88f, originY + size * 0.52f) }));
            strokes.Add(new SyntheticStroke(new List<Point2f> { new(originX + size * 0.15f, originY + size * 0.85f), new(originX + size * 0.55f, originY + size * 0.62f), new(originX + size * 0.95f, originY + size * 0.95f) }));
            if (random.NextDouble() < 0.7)
            {
                strokes.Add(new SyntheticStroke(new List<Point2f> { new(originX + size * 0.1f, originY + size * 0.15f), new(originX + size * 0.35f, originY + size * 0.38f), new(originX + size * 0.2f, originY + size * 0.72f) }));
            }
        }

        return strokes;
    }
}

internal sealed record SyntheticStroke(List<Point2f> Points);

internal sealed record SignatureRenderProfile(int InkGray, int Thickness, double Jitter, bool Blur, double NoiseStdDev, double RotateDegrees)
{
    public static SignatureRenderProfile Reference() => new(0, 4, 5.5, false, 0, 0);

    public static SignatureRenderProfile ForExpectedClass(ExpectedClass expectedClass, Random random) => expectedClass switch
    {
        ExpectedClass.Review => new SignatureRenderProfile(random.Next(80, 150), random.Next(2, 4), 10, true, random.Next(6, 18), random.Next(-4, 5)),
        ExpectedClass.Reject => new SignatureRenderProfile(random.Next(0, 70), random.Next(3, 6), 7, random.NextDouble() < 0.2, random.NextDouble() < 0.4 ? random.Next(3, 10) : 0, random.Next(-3, 4)),
        _ => new SignatureRenderProfile(random.Next(0, 55), random.Next(3, 6), 6.5, random.NextDouble() < 0.08, random.NextDouble() < 0.12 ? random.Next(2, 8) : 0, random.Next(-2, 3))
    };
}

internal sealed record SyntheticScenario(string Name, ExpectedClass PrimaryClass, ExpectedClass SecondaryClass)
{
    public ExpectedClass ClassForRole(int roleIndex, int roleCount)
    {
        if (roleIndex == 0)
        {
            return PrimaryClass;
        }

        if (Name == "DuplicateRole" && roleIndex == 1)
        {
            return ExpectedClass.Reject;
        }

        return SecondaryClass;
    }

    public static SyntheticScenario CleanMatch() => new("CleanMatch", ExpectedClass.Accept, ExpectedClass.Accept);
    public static SyntheticScenario PoorQuality() => new("PoorQuality", ExpectedClass.Review, ExpectedClass.Accept);
    public static SyntheticScenario NegativeMismatch() => new("NegativeMismatch", ExpectedClass.Reject, ExpectedClass.Accept);
    public static SyntheticScenario Missing() => new("MissingSignature", ExpectedClass.Missing, ExpectedClass.Accept);
    public static SyntheticScenario WrongField() => new("WrongField", ExpectedClass.Missing, ExpectedClass.Reject);
    public static SyntheticScenario DuplicateRole() => new("DuplicateRole", ExpectedClass.Accept, ExpectedClass.Reject);
}

internal enum ExpectedClass
{
    Accept,
    Reject,
    Review,
    Missing
}

internal static class SignatureRoleCatalog
{
    public static readonly IReadOnlyList<SignatureRole> Roles = new[]
    {
        new SignatureRole("applicant_signature", "Applicant Signature", "Applicant Signature"),
        new SignatureRole("witness_signature", "Witness Signature", "Witness Signature"),
        new SignatureRole("authorised_signature", "Authorised Signature", "Authorised Signature")
    };

    public static string ModeForRoleCount(int roleCount) => roleCount switch
    {
        1 => "ApplicantOnly",
        2 => "ApplicantWitness",
        _ => "ApplicantWitnessAuthorised"
    };

    public static string[] LabelsFor(string signatureId)
    {
        return signatureId switch
        {
            "applicant_signature" => new[] { "Applicant Signature", "Customer Signature", "Signed By" },
            "witness_signature" => new[] { "Witness Signature", "Witness" },
            "authorised_signature" => new[] { "Authorised Signature", "Authorized Signature", "Authorised By", "Authorized By" },
            _ => new[] { "Signature", "Signed By" }
        };
    }
}

internal sealed record SignatureRole(string SignatureId, string DisplayName, string Label);
internal sealed record OcrLine(string Text, double[] Polygon);

internal sealed class BenchmarkFolders
{
    public string Root { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public string ReferenceSignatures { get; init; } = string.Empty;
    public string GroundTruth { get; init; } = string.Empty;
    public string OcrLayout { get; init; } = string.Empty;
    public string Results { get; init; } = string.Empty;
    public string Report { get; init; } = string.Empty;

    public static BenchmarkFolders Create(string root)
    {
        root = Path.GetFullPath(root);
        return new BenchmarkFolders
        {
            Root = root,
            Input = Path.Combine(root, "Input"),
            ReferenceSignatures = Path.Combine(root, "ReferenceSignatures"),
            GroundTruth = Path.Combine(root, "GroundTruth"),
            OcrLayout = Path.Combine(root, "OcrLayout"),
            Results = Path.Combine(root, "Results"),
            Report = Path.Combine(root, "Report")
        };
    }
}

internal sealed class BenchmarkManifest
{
    public DateTimeOffset GeneratedUtc { get; set; }
    public string Root { get; set; } = string.Empty;
    public int DocumentsRequested { get; set; }
    public int Signers { get; set; }
    public int ReferencesPerSigner { get; set; }
    public List<GroundTruthDocument> Documents { get; set; } = new();
}

internal sealed class GroundTruthDocument
{
    public string DocumentId { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string FormRoleMode { get; set; } = "Auto";
    public string InputDocumentPath { get; set; } = string.Empty;
    public string OcrLayoutPath { get; set; } = string.Empty;
    public string OptionsPath { get; set; } = string.Empty;
    public bool UsesKnownZones { get; set; }
    public List<GroundTruthSignature> ExpectedSignatures { get; set; } = new();
}

internal sealed class GroundTruthSignature
{
    public string SignatureId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ExpectedClass { get; set; } = string.Empty;
    public string ExpectedSignerId { get; set; } = string.Empty;
    public string? ActualSignerId { get; set; }
    public string Script { get; set; } = string.Empty;
    public List<string> ReferenceImagePaths { get; set; } = new();
    public GroundTruthZone Zone { get; set; } = new();
}

internal sealed class GroundTruthZone
{
    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string CoordinateSystem { get; set; } = "pixels";
}

internal sealed class BenchmarkRow
{
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentPath { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string GroundTruthPath { get; set; } = string.Empty;
    public string OcrLayoutPath { get; set; } = string.Empty;
    public double DocumentProcessingMs { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public bool UsesKnownZones { get; set; }
    public string SignatureId { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string ExpectedClass { get; set; } = string.Empty;
    public string ExpectedSignerId { get; set; } = string.Empty;
    public string? ActualSignerId { get; set; }
    public string EngineDecision { get; set; } = string.Empty;
    public bool EngineDetected { get; set; }
    public bool EngineMatched { get; set; }
    public double Confidence { get; set; }
    public string? BestReferenceId { get; set; }
    public string? BestReferenceFileName { get; set; }
    public string Warnings { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
}

internal sealed class BenchmarkSummary
{
    public string Root { get; set; } = string.Empty;
    public DateTimeOffset GeneratedUtc { get; set; }
    public int TotalDocuments { get; set; }
    public int TotalSignatures { get; set; }
    public int TrueAccepts { get; set; }
    public int TrueRejects { get; set; }
    public int CorrectMissing { get; set; }
    public int CorrectReviews { get; set; }
    public int DangerousFalseAccepts { get; set; }
    public int HardFalseRejects { get; set; }
    public int AcceptNeedsReview { get; set; }
    public int RejectNeedsReview { get; set; }
    public int MissingNeedsReview { get; set; }
    public int DetectedSignatures { get; set; }
    public int ReviewDecisions { get; set; }
    public double DetectionRate { get; set; }
    public double DangerousFalseAcceptRate { get; set; }
    public double HardFalseRejectRate { get; set; }
    public double AutoAcceptRecall { get; set; }
    public double MissingNeedsReviewRate { get; set; }
    public double ReviewRate { get; set; }
    public double AveragePositiveConfidence { get; set; }
    public double AverageNegativeConfidence { get; set; }
    public double AverageDocumentProcessingMs { get; set; }
    public double P50DocumentProcessingMs { get; set; }
    public double P95DocumentProcessingMs { get; set; }
    public double TotalProcessingMs { get; set; }
    public double SuggestedMatchedThreshold { get; set; }
    public double SuggestedProbableThreshold { get; set; }
    public double SuggestedReviewThreshold { get; set; }
    public double? RunMatchedThresholdOverride { get; set; }
    public double? RunProbableThresholdOverride { get; set; }
    public double? RunReviewThresholdOverride { get; set; }
    public List<ThresholdSweepRow> ThresholdSweep { get; set; } = new();
    public List<ReviewThresholdSweepRow> ReviewThresholdSweep { get; set; } = new();
    public List<GroupSummary> ScenarioSummaries { get; set; } = new();
    public List<GroupSummary> RoleSummaries { get; set; } = new();
    public List<GroupSummary> ScriptSummaries { get; set; } = new();
    public string CsvReportPath { get; set; } = string.Empty;
    public string JsonReportPath { get; set; } = string.Empty;
    public string HtmlReportPath { get; set; } = string.Empty;

    public static BenchmarkSummary FromRows(IReadOnlyList<BenchmarkRow> rows)
    {
        var positives = rows.Where(r => r.ExpectedClass == ExpectedClass.Accept.ToString()).ToList();
        var negatives = rows.Where(r => r.ExpectedClass == ExpectedClass.Reject.ToString()).ToList();
        var missing = rows.Where(r => r.ExpectedClass == ExpectedClass.Missing.ToString()).ToList();
        var review = rows.Where(r => r.ExpectedClass == ExpectedClass.Review.ToString()).ToList();
        var present = rows.Where(r => r.ExpectedClass != ExpectedClass.Missing.ToString()).ToList();
        var positiveScores = positives.Select(r => r.Confidence).Where(s => s > 0).OrderBy(s => s).ToList();
        var negativeScores = negatives.Concat(missing).Select(r => r.Confidence).Where(s => s > 0).OrderBy(s => s).ToList();
        var documentDurations = rows
            .GroupBy(r => r.DocumentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Max(r => r.DocumentProcessingMs))
            .OrderBy(value => value)
            .ToList();

        var suggestedMatched = Math.Clamp(Math.Max(Percentile(negativeScores, 0.95) + 3, Percentile(positiveScores, 0.25)), 60, 96);
        var suggestedReview = Math.Clamp(Math.Max(Percentile(negativeScores, 0.70), Percentile(positiveScores, 0.05) - 8), 35, suggestedMatched - 5);
        var suggestedProbable = Math.Clamp((suggestedMatched + suggestedReview) / 2.0, suggestedReview + 1, suggestedMatched - 1);

        return new BenchmarkSummary
        {
            TotalDocuments = rows.Select(r => r.DocumentId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalSignatures = rows.Count,
            TrueAccepts = rows.Count(r => r.Outcome == "TrueAccept"),
            TrueRejects = rows.Count(r => r.Outcome == "TrueReject"),
            CorrectMissing = rows.Count(r => r.Outcome == "CorrectMissing"),
            CorrectReviews = rows.Count(r => r.Outcome == "CorrectReview"),
            DangerousFalseAccepts = rows.Count(r => r.Outcome == "DangerousFalseAccept"),
            HardFalseRejects = rows.Count(r => r.Outcome == "FalseReject"),
            AcceptNeedsReview = rows.Count(r => r.Outcome == "AcceptNeedsReview"),
            RejectNeedsReview = rows.Count(r => r.Outcome == "RejectNeedsReview"),
            MissingNeedsReview = rows.Count(r => r.Outcome == "MissingNeedsReview"),
            DetectedSignatures = rows.Count(r => r.EngineDetected),
            ReviewDecisions = rows.Count(r => r.EngineDecision is "ReviewRequired" or "ProbableMatch" or "InsufficientQuality"),
            DetectionRate = present.Count == 0 ? 0 : present.Count(r => r.EngineDetected) / (double)present.Count,
            DangerousFalseAcceptRate = negatives.Count + missing.Count == 0 ? 0 : rows.Count(r => r.Outcome == "DangerousFalseAccept") / (double)(negatives.Count + missing.Count),
            HardFalseRejectRate = positives.Count == 0 ? 0 : rows.Count(r => r.Outcome == "FalseReject") / (double)positives.Count,
            AutoAcceptRecall = positives.Count == 0 ? 0 : rows.Count(r => r.ExpectedClass == ExpectedClass.Accept.ToString() && r.EngineDecision == "Matched") / (double)positives.Count,
            MissingNeedsReviewRate = missing.Count == 0 ? 0 : rows.Count(r => r.Outcome == "MissingNeedsReview") / (double)missing.Count,
            ReviewRate = rows.Count == 0 ? 0 : rows.Count(r => r.EngineDecision is "ReviewRequired" or "ProbableMatch" or "InsufficientQuality") / (double)rows.Count,
            AveragePositiveConfidence = positiveScores.Count == 0 ? 0 : Math.Round(positiveScores.Average(), 2),
            AverageNegativeConfidence = negativeScores.Count == 0 ? 0 : Math.Round(negativeScores.Average(), 2),
            AverageDocumentProcessingMs = documentDurations.Count == 0 ? 0 : Math.Round(documentDurations.Average(), 2),
            P50DocumentProcessingMs = Math.Round(Percentile(documentDurations, 0.50), 2),
            P95DocumentProcessingMs = Math.Round(Percentile(documentDurations, 0.95), 2),
            TotalProcessingMs = Math.Round(documentDurations.Sum(), 2),
            SuggestedMatchedThreshold = Math.Round(suggestedMatched, 2),
            SuggestedProbableThreshold = Math.Round(suggestedProbable, 2),
            SuggestedReviewThreshold = Math.Round(suggestedReview, 2),
            ThresholdSweep = BuildThresholdSweep(rows),
            ReviewThresholdSweep = BuildReviewThresholdSweep(rows),
            ScenarioSummaries = BuildGroupSummaries(rows, r => r.Scenario),
            RoleSummaries = BuildGroupSummaries(rows, r => r.SignatureId),
            ScriptSummaries = BuildGroupSummaries(rows, r => r.Script)
        };
    }

    private static List<ThresholdSweepRow> BuildThresholdSweep(IReadOnlyList<BenchmarkRow> rows)
    {
        var positives = rows.Where(r => r.ExpectedClass == ExpectedClass.Accept.ToString()).ToList();
        var riskyNegatives = rows.Where(r => r.ExpectedClass is "Reject" or "Missing").ToList();
        var thresholds = rows
            .Select(r => Math.Round(r.Confidence, 0))
            .Where(v => v > 0)
            .Concat(new[] { 45.0, 50.0, 55.0, 60.0, 65.0, 70.0, 75.0, 80.0, 85.0 })
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        return thresholds.Select(threshold =>
        {
            var trueAccepts = positives.Count(r => r.Confidence >= threshold);
            var dangerousFalseAccepts = riskyNegatives.Count(r => r.Confidence >= threshold);
            return new ThresholdSweepRow
            {
                MatchedThreshold = threshold,
                TrueAccepts = trueAccepts,
                AcceptNotAutoAccepted = positives.Count - trueAccepts,
                DangerousFalseAccepts = dangerousFalseAccepts,
                AutoAcceptRecall = positives.Count == 0 ? 0 : trueAccepts / (double)positives.Count,
                DangerousFalseAcceptRate = riskyNegatives.Count == 0 ? 0 : dangerousFalseAccepts / (double)riskyNegatives.Count
            };
        }).ToList();
    }

    private static List<ReviewThresholdSweepRow> BuildReviewThresholdSweep(IReadOnlyList<BenchmarkRow> rows)
    {
        var positives = rows.Where(r => r.ExpectedClass == ExpectedClass.Accept.ToString()).ToList();
        var riskyNonAccepts = rows.Where(r => r.ExpectedClass is "Reject" or "Missing").ToList();
        var thresholds = rows
            .Select(r => Math.Round(r.Confidence, 0))
            .Where(v => v > 0)
            .Concat(new[] { 35.0, 40.0, 45.0, 50.0, 55.0, 60.0, 65.0, 70.0 })
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        return thresholds.Select(threshold =>
        {
            var hardFalseRejects = positives.Count(r => !r.EngineDetected || r.Confidence < threshold);
            var acceptNeedsReview = positives.Count(r => r.EngineDetected && r.Confidence >= threshold);
            var negativeNeedsReview = riskyNonAccepts.Count(r => r.EngineDetected && r.Confidence >= threshold);
            return new ReviewThresholdSweepRow
            {
                ReviewThreshold = threshold,
                HardFalseRejects = hardFalseRejects,
                HardFalseRejectRate = positives.Count == 0 ? 0 : hardFalseRejects / (double)positives.Count,
                AcceptNeedsReview = acceptNeedsReview,
                NegativeNeedsReview = negativeNeedsReview,
                NegativeNeedsReviewRate = riskyNonAccepts.Count == 0 ? 0 : negativeNeedsReview / (double)riskyNonAccepts.Count
            };
        }).ToList();
    }

    private static List<GroupSummary> BuildGroupSummaries(IReadOnlyList<BenchmarkRow> rows, Func<BenchmarkRow, string> keySelector)
    {
        return rows
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                return new GroupSummary
                {
                    Name = group.Key,
                    Total = items.Count,
                    Detected = items.Count(r => r.EngineDetected),
                    TrueAccepts = items.Count(r => r.Outcome == "TrueAccept"),
                    NeedsReview = items.Count(r => r.Outcome is "AcceptNeedsReview" or "RejectNeedsReview" or "MissingNeedsReview" or "CorrectReview"),
                    DangerousFalseAccepts = items.Count(r => r.Outcome == "DangerousFalseAccept"),
                    HardFalseRejects = items.Count(r => r.Outcome == "FalseReject"),
                    AverageConfidence = items.Count == 0 ? 0 : Math.Round(items.Average(r => r.Confidence), 2)
                };
            })
            .ToList();
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = index - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }
}

internal sealed class ThresholdSweepRow
{
    public double MatchedThreshold { get; set; }
    public int TrueAccepts { get; set; }
    public int AcceptNotAutoAccepted { get; set; }
    public int DangerousFalseAccepts { get; set; }
    public double AutoAcceptRecall { get; set; }
    public double DangerousFalseAcceptRate { get; set; }
}

internal sealed class ReviewThresholdSweepRow
{
    public double ReviewThreshold { get; set; }
    public int HardFalseRejects { get; set; }
    public double HardFalseRejectRate { get; set; }
    public int AcceptNeedsReview { get; set; }
    public int NegativeNeedsReview { get; set; }
    public double NegativeNeedsReviewRate { get; set; }
}

internal sealed class GroupSummary
{
    public string Name { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Detected { get; set; }
    public int TrueAccepts { get; set; }
    public int NeedsReview { get; set; }
    public int DangerousFalseAccepts { get; set; }
    public int HardFalseRejects { get; set; }
    public double AverageConfidence { get; set; }
}

internal sealed record BenchmarkThresholdOverrides(double? Matched, double? Probable, double? Review)
{
    public bool HasAny => Matched.HasValue || Probable.HasValue || Review.HasValue;
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; private set; } = "generate-run";

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        var commandSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!commandSet)
                {
                    options.Command = arg;
                    commandSet = true;
                }

                continue;
            }

            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            options._values[arg] = value;
        }

        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;
    public int GetInt(string key, int fallback) => _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    public double? GetDouble(string key) => _values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
