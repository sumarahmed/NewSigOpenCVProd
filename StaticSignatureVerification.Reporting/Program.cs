using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string DefaultInputFolder = @"C:\Temp\SignatureVerification\Output";
const string DefaultOutputFolder = @"C:\Temp\SignatureVerification\BusinessReports";

var cli = CliOptions.Parse(args);
if (cli.Has("--help") || cli.Has("-h"))
{
    PrintUsage();
    return;
}

var input = Path.GetFullPath(cli.Get("--input", DefaultInputFolder));
var output = Path.GetFullPath(cli.Get("--output", DefaultOutputFolder));
var title = cli.Get("--title", "Signature Verification Business Report");

try
{
    if (cli.Has("--health-check"))
    {
        Directory.CreateDirectory(output);
        var health = HealthCheck.Run(input, output);
        health.WriteFiles();
        Console.WriteLine($"Health check generated: {health.HtmlPath}");
        Console.WriteLine($"Status: {health.Status}");
        Environment.ExitCode = health.Status == "Pass" ? 0 : 2;
        return;
    }

    var cases = ResultReader.ReadCases(input).ToList();
    Directory.CreateDirectory(output);

    if (cli.Has("--feedback-tune"))
    {
        var outcomesPath = Path.GetFullPath(cli.Get("--reviewer-outcomes", Path.Combine(output, "reviewer-outcomes.csv")));
        var currentOptionsPath = cli.Get("--current-options", string.Empty);
        var currentOptions = string.IsNullOrWhiteSpace(currentOptionsPath) ? null : Path.GetFullPath(currentOptionsPath);
        var feedback = FeedbackTuningReport.Build(cases, outcomesPath, currentOptions, output);
        feedback.WriteFiles();
        Console.WriteLine($"Feedback tuning report generated: {feedback.HtmlPath}");
        Console.WriteLine($"Reviewed outcomes: {feedback.TotalOutcomes}, matched to result cases: {feedback.MatchedOutcomes}");
        return;
    }

    var report = BusinessReport.Build(title, input, output, cases);
    report.WriteFiles();

    Console.WriteLine($"Business report generated: {report.HtmlPath}");
    Console.WriteLine($"Documents: {report.TotalDocuments}, signature cases: {report.TotalSignatureCases}");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static void PrintUsage()
{
    Console.WriteLine($"""
StaticSignatureVerification.Reporting

Generates business-ready daily, weekly, and monthly reports from verifier result JSON files.

Options:
  --input <folder>   Folder containing result JSON files. Default: {DefaultInputFolder}
  --output <folder>  Report output folder. Default: {DefaultOutputFolder}
  --title <text>     Report title.
  --health-check     Check source/result folders and reporting write access.
  --feedback-tune    Build threshold/reference recommendations from reviewer outcomes.
  --reviewer-outcomes <csv>
                    Reviewer outcomes CSV exported from review-queue.html.
                    Default: <output>\reviewer-outcomes.csv
  --current-options <json>
                    Current options.json used as the base for tuned-options.json.

Examples:
  dotnet run --project .\StaticSignatureVerification.Reporting -- --input C:\Temp\SignatureVerification\Output
  dotnet run --project .\StaticSignatureVerification.Reporting -- --input C:\Temp\SignatureVerification\SyntheticBenchmark\Results --output C:\Temp\SignatureVerification\BusinessReports
  dotnet run --project .\StaticSignatureVerification.Reporting -- --health-check --input C:\Temp\SignatureVerification\Output
  dotnet run --project .\StaticSignatureVerification.Reporting -- --feedback-tune --input C:\Temp\SignatureVerification\Output --output C:\Temp\SignatureVerification\BusinessReports --reviewer-outcomes C:\Temp\SignatureVerification\BusinessReports\reviewer-outcomes.csv --current-options C:\Temp\SignatureVerification\Input\options.json
""");
}

internal static class ResultReader
{
    public static IEnumerable<SignatureCase> ReadCases(string inputFolder)
    {
        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input folder was not found: {inputFolder}");
        }

        foreach (var path in Directory.EnumerateFiles(inputFolder, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("signatureResults", out var signatureResults) ||
                    signatureResults.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var operational = TryGetObject(root, "operationalAudit");
                var eventUtc = TryGetDate(operational, "finishedUtc") ??
                               TryGetDate(operational, "startedUtc") ??
                               File.GetLastWriteTimeUtc(path);
                var result = new DocumentResult
                {
                    ResultPath = path,
                    DocumentName = InferDocumentName(path),
                    SourceDocumentPath = InferSourceDocumentPath(path),
                    DocumentResultId = TryGetString(root, "documentResultId") ?? string.Empty,
                    CorrelationId = TryGetString(operational, "correlationId"),
                    EventUtc = eventUtc,
                    OverallDecision = TryGetString(root, "overallDecision") ?? "Unknown",
                    OverallConfidence = TryGetDouble(root, "overallConfidence"),
                    InputDocumentType = TryGetString(root, "inputDocumentType") ?? "Unknown",
                    SignatureCountExpected = TryGetInt(root, "signatureCountExpected"),
                    SignatureCountDetected = TryGetInt(root, "signatureCountDetected"),
                    DurationMs = TryGetDouble(operational, "durationMs"),
                    ErrorCode = TryGetString(operational, "errorCode") ?? FirstErrorCode(root)
                };

                if (signatureResults.GetArrayLength() == 0)
                {
                    yield return SignatureCase.ForDocumentOnly(result);
                    continue;
                }

                foreach (var signature in signatureResults.EnumerateArray())
                {
                    var audit = TryGetObject(signature, "audit");
                    var detectedRegion = TryGetObject(signature, "detectedRegion");
                    var mapping = TryGetObject(signature, "mapping");
                    var best = FindBestReference(audit);
                    yield return new SignatureCase
                    {
                        Document = result,
                        SignatureId = TryGetString(signature, "signatureId") ?? string.Empty,
                        DisplayName = TryGetString(signature, "displayName") ?? string.Empty,
                        Decision = TryGetString(signature, "decision") ?? "Unknown",
                        IsMatched = TryGetBool(signature, "isMatched"),
                        ReviewRequired = TryGetBool(signature, "reviewRequired"),
                        SignatureDetected = TryGetBool(signature, "signatureDetected"),
                        Confidence = TryGetDouble(signature, "confidence"),
                        SignatureQuality = TryGetString(signature, "signatureQuality") ?? "Unknown",
                        BestReferenceId = TryGetString(signature, "bestReferenceId") ?? best.ReferenceId,
                        BestReferenceFileName = best.ReferenceFileName,
                        BestReferenceFilePath = best.ReferenceFilePath,
                        MappingDisplayName = TryGetString(mapping, "displayName"),
                        PartyId = TryGetString(mapping, "partyId"),
                        PartyName = TryGetString(mapping, "partyName"),
                        ReferenceSetId = TryGetString(mapping, "referenceSetId"),
                        ExpectedSignerId = TryGetString(mapping, "expectedSignerId"),
                        ActualSignerId = TryGetString(mapping, "actualSignerId"),
                        ExpectedClass = TryGetString(mapping, "expectedClass"),
                        MappingSource = TryGetString(mapping, "source"),
                        GroundTruthPath = InferGroundTruthPath(path),
                        ExtractedSignatureImagePath = FindDebugImage(audit, "rawCropPath", "cleanedCropPath", "normalizedPath", "renderedPagePath"),
                        DetectedX = TryGetInt(detectedRegion, "x"),
                        DetectedY = TryGetInt(detectedRegion, "y"),
                        DetectedWidth = TryGetInt(detectedRegion, "width"),
                        DetectedHeight = TryGetInt(detectedRegion, "height"),
                        CandidateCount = TryGetArrayLength(signature, "candidateRegions"),
                        WarningCount = TryGetArrayLength(signature, "warnings"),
                        DebugFileCount = CountDebugImages(audit)
                    };
                }
            }
        }
    }

    private static string InferDocumentName(string resultPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(resultPath);
        if (fileName.EndsWith(".result", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".result".Length];
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(resultPath));
        return string.IsNullOrWhiteSpace(parent) ? fileName : parent;
    }

    private static string? InferSourceDocumentPath(string resultPath)
    {
        var documentName = InferDocumentName(resultPath);
        var extensions = new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".pdf" };
        var directory = Path.GetDirectoryName(resultPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var full = Path.GetFullPath(resultPath);
        var marker = $"{Path.DirectorySeparatorChar}Results{Path.DirectorySeparatorChar}";
        var markerIndex = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var root = full[..markerIndex];
            var input = Path.Combine(root, "Input");
            var match = FindDocument(input, documentName, extensions);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        var outputMarker = $"{Path.DirectorySeparatorChar}Output{Path.DirectorySeparatorChar}";
        var outputIndex = full.IndexOf(outputMarker, StringComparison.OrdinalIgnoreCase);
        if (outputIndex >= 0)
        {
            var root = full[..outputIndex];
            var input = Path.Combine(root, "Input");
            var match = FindDocument(input, documentName, extensions);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return FindDocument(directory, documentName, extensions);
    }

    private static string? InferGroundTruthPath(string resultPath)
    {
        var documentName = InferDocumentName(resultPath);
        var full = Path.GetFullPath(resultPath);
        var marker = $"{Path.DirectorySeparatorChar}Results{Path.DirectorySeparatorChar}";
        var markerIndex = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var root = full[..markerIndex];
        var path = Path.Combine(root, "GroundTruth", documentName + ".json");
        return File.Exists(path) ? path : null;
    }

    private static string? FindDocument(string folder, string documentName, IReadOnlyList<string> extensions)
    {
        if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(documentName))
        {
            return null;
        }

        foreach (var extension in extensions)
        {
            var direct = Path.Combine(folder, documentName + extension);
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), documentName, StringComparison.OrdinalIgnoreCase));
    }

    private static BestReference FindBestReference(JsonElement? audit)
    {
        if (audit is null ||
            !audit.Value.TryGetProperty("referenceComparisons", out var comparisons) ||
            comparisons.ValueKind != JsonValueKind.Array)
        {
            return new BestReference(null, null, null);
        }

        JsonElement? selected = null;
        foreach (var item in comparisons.EnumerateArray())
        {
            if (TryGetBool(item, "isBestMatch"))
            {
                selected = item;
                break;
            }

            selected ??= item;
        }

        return selected is null
            ? new BestReference(null, null, null)
            : new BestReference(
                TryGetString(selected.Value, "referenceId"),
                TryGetString(selected.Value, "referenceFileName"),
                TryGetString(selected.Value, "referenceFilePath"));
    }

    private static int CountDebugImages(JsonElement? audit)
    {
        if (audit is null ||
            !audit.Value.TryGetProperty("debugImages", out var debugImages) ||
            debugImages.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return debugImages.EnumerateObject().Count();
    }

    private static string? FindDebugImage(JsonElement? audit, params string[] keys)
    {
        if (audit is null ||
            !audit.Value.TryGetProperty("debugImages", out var debugImages) ||
            debugImages.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (debugImages.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                File.Exists(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string? FirstErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return null;
        }

        return TryGetString(errors[0], "code");
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double TryGetDouble(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return element.Value.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var parsed)
            ? parsed
            : 0;
    }

    private static int TryGetInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static int TryGetInt(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return TryGetInt(element.Value, propertyName);
    }

    private static bool TryGetBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static int TryGetArrayLength(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static DateTimeOffset? TryGetDate(JsonElement? element, string propertyName)
    {
        var text = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private sealed record BestReference(string? ReferenceId, string? ReferenceFileName, string? ReferenceFilePath);
}

internal sealed class BusinessReport
{
    public string Title { get; init; } = string.Empty;
    public string InputFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public string HtmlPath => Path.Combine(OutputFolder, "signature-business-report.html");
    public List<SignatureCase> Cases { get; init; } = new();
    public int TotalDocuments { get; init; }
    public int TotalSignatureCases { get; init; }
    public List<PeriodSummary> Daily { get; init; } = new();
    public List<PeriodSummary> Weekly { get; init; } = new();
    public List<PeriodSummary> Monthly { get; init; } = new();
    public List<ReferenceSummary> References { get; init; } = new();
    public List<SignatureCase> ReviewQueue { get; init; } = new();

    public static BusinessReport Build(string title, string input, string output, IReadOnlyList<SignatureCase> cases)
    {
        var normalized = cases.OrderByDescending(c => c.Document.EventUtc).ThenBy(c => c.Document.DocumentName, StringComparer.OrdinalIgnoreCase).ToList();
        return new BusinessReport
        {
            Title = title,
            InputFolder = input,
            OutputFolder = output,
            Cases = normalized,
            TotalDocuments = normalized.Select(c => c.Document.ResultPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalSignatureCases = normalized.Count,
            Daily = BuildPeriodSummaries(normalized, PeriodKind.Day),
            Weekly = BuildPeriodSummaries(normalized, PeriodKind.Week),
            Monthly = BuildPeriodSummaries(normalized, PeriodKind.Month),
            References = BuildReferenceSummaries(normalized),
            ReviewQueue = normalized.Where(NeedsHumanReview).ToList()
        };
    }

    public void WriteFiles()
    {
        File.WriteAllText(HtmlPath, HtmlReportWriter.Write(this));
        File.WriteAllText(Path.Combine(OutputFolder, "review-queue.html"), ReviewQueueHtmlWriter.Write(this));
        File.WriteAllText(Path.Combine(OutputFolder, "reference-registry.html"), ReferenceRegistryHtmlWriter.Write(this));
        File.WriteAllText(Path.Combine(OutputFolder, "daily-summary.csv"), CsvWriter.WritePeriods(Daily));
        File.WriteAllText(Path.Combine(OutputFolder, "weekly-summary.csv"), CsvWriter.WritePeriods(Weekly));
        File.WriteAllText(Path.Combine(OutputFolder, "monthly-summary.csv"), CsvWriter.WritePeriods(Monthly));
        File.WriteAllText(Path.Combine(OutputFolder, "reference-summary.csv"), CsvWriter.WriteReferences(References));
        File.WriteAllText(Path.Combine(OutputFolder, "reference-cases.csv"), CsvWriter.WriteCases(Cases));
        File.WriteAllText(Path.Combine(OutputFolder, "review-queue.csv"), CsvWriter.WriteCases(ReviewQueue));
        File.WriteAllText(Path.Combine(OutputFolder, "reviewer-outcomes-template.csv"), CsvWriter.WriteReviewerOutcomeTemplate(ReviewQueue));
        File.WriteAllText(Path.Combine(OutputFolder, "batch-manifest.json"), BatchManifestWriter.Write(this));
    }

    private static bool NeedsHumanReview(SignatureCase item) =>
        item.Document.OverallDecision == "Error" ||
        item.ReviewRequired ||
        !item.SignatureDetected ||
        item.Decision is "ProbableMatch" or "ReviewRequired" or "InsufficientQuality" or "NotMatched" or "NoSignatureDetected";

    private static List<PeriodSummary> BuildPeriodSummaries(IReadOnlyList<SignatureCase> cases, PeriodKind kind)
    {
        return cases
            .GroupBy(item => PeriodKey.Create(item.Document.EventUtc, kind))
            .OrderByDescending(group => group.Key.SortKey, StringComparer.Ordinal)
            .Select(group => PeriodSummary.FromCases(group.Key.Label, group.ToList()))
            .ToList();
    }

    private static List<ReferenceSummary> BuildReferenceSummaries(IReadOnlyList<SignatureCase> cases)
    {
        return cases
            .Where(c => !string.IsNullOrWhiteSpace(c.BestReferenceId) || !string.IsNullOrWhiteSpace(c.BestReferenceFileName))
            .GroupBy(c => c.ReferenceKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var latest = items.OrderByDescending(c => c.Document.EventUtc).First();
                return new ReferenceSummary
                {
                    ReferenceKey = group.Key,
                    ReferenceId = latest.BestReferenceId ?? string.Empty,
                    ReferenceFileName = latest.BestReferenceFileName ?? string.Empty,
                    ReferenceFilePath = latest.BestReferenceFilePath,
                    Cases = items.Count,
                    Matched = items.Count(c => c.Decision == "Matched"),
                    Review = items.Count(c => c.ReviewRequired),
                    NotMatched = items.Count(c => c.Decision == "NotMatched"),
                    AverageConfidence = items.Count == 0 ? 0 : Math.Round(items.Average(c => c.Confidence), 2),
                    LastSeenUtc = latest.Document.EventUtc,
                    LastResultPath = latest.Document.ResultPath
                };
            })
            .ToList();
    }
}

internal static class HtmlReportWriter
{
    public static string Write(BusinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Signature Business Report</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; color: #202124; margin: 28px; background: #ffffff; }
    h1 { font-size: 28px; margin: 0 0 8px; }
    h2 { font-size: 19px; margin-top: 30px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
    .meta { color: #57606a; margin-bottom: 18px; }
    .metrics { display: grid; grid-template-columns: repeat(5, minmax(130px, 1fr)); gap: 10px; max-width: 1180px; }
    .metric { border: 1px solid #d0d7de; border-radius: 6px; padding: 10px 12px; background: #f8fafc; }
    .metric span { color: #57606a; display: block; font-size: 12px; }
    .metric b { display: block; font-size: 22px; margin-top: 3px; }
    table { border-collapse: collapse; width: 100%; margin-top: 12px; font-size: 13px; }
    th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }
    th { background: #f6f8fa; position: sticky; top: 0; }
    .good { color: #067647; font-weight: 600; }
    .warn { color: #b54708; font-weight: 600; }
    .bad { color: #b42318; font-weight: 600; }
    .muted { color: #667085; }
    .toolbar { margin: 18px 0 8px; display: flex; gap: 8px; align-items: center; }
    input { border: 1px solid #b6c2cf; border-radius: 4px; padding: 7px 9px; width: 360px; }
    a { color: #175cd3; text-decoration: none; }
    a:hover { text-decoration: underline; }
  </style>
  <script>
    function filterTable(id, value) {
      const q = value.toLowerCase();
      document.querySelectorAll('#' + id + ' tbody tr').forEach(row => {
        row.style.display = row.innerText.toLowerCase().includes(q) ? '' : 'none';
      });
    }
  </script>
</head>
<body>
""");
        sb.AppendLine($"<h1>{Html(report.Title)}</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated {DateTimeOffset.UtcNow:u}<br>Source: {Html(report.InputFolder)}</div>");
        sb.AppendLine("<div class=\"metrics\">");
        Metric(sb, "Documents", report.TotalDocuments);
        Metric(sb, "Signature cases", report.TotalSignatureCases);
        Metric(sb, "Matched", report.Cases.Count(c => c.Decision == "Matched"));
        Metric(sb, "Needs review", report.Cases.Count(c => c.ReviewRequired));
        Metric(sb, "Errors", report.Cases.Count(c => !string.IsNullOrWhiteSpace(c.Document.ErrorCode)));
        Metric(sb, "Avg confidence", report.Cases.Count == 0 ? "0.00" : report.Cases.Average(c => c.Confidence).ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "Avg duration ms", report.Cases.Count == 0 ? "0.00" : report.Cases.Select(c => c.Document).DistinctBy(d => d.ResultPath).Average(d => d.DurationMs).ToString("0.00", CultureInfo.InvariantCulture));
        Metric(sb, "References used", report.References.Count);
        Metric(sb, "No signature", report.Cases.Count(c => !c.SignatureDetected));
        Metric(sb, "Not matched", report.Cases.Count(c => c.Decision == "NotMatched"));
        sb.AppendLine("</div>");

        WritePeriodTable(sb, "Daily Statistics", "daily", report.Daily);
        WritePeriodTable(sb, "Weekly Statistics", "weekly", report.Weekly);
        WritePeriodTable(sb, "Monthly Statistics", "monthly", report.Monthly);
        WriteReferenceTable(sb, report.References);
        WriteCasesTable(sb, report.Cases);

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void Metric(StringBuilder sb, string label, object value) =>
        sb.AppendLine($"<div class=\"metric\"><span>{Html(label)}</span><b>{Html(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}</b></div>");

    private static void WritePeriodTable(StringBuilder sb, string title, string id, IReadOnlyList<PeriodSummary> rows)
    {
        sb.AppendLine($"<h2>{Html(title)}</h2>");
        sb.AppendLine($"<table id=\"{id}\"><thead><tr><th>Period</th><th>Documents</th><th>Cases</th><th>Matched</th><th>Probable</th><th>Review</th><th>Not matched</th><th>No signature</th><th>Errors</th><th>Avg confidence</th><th>Avg ms</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.AppendLine($"<tr><td>{Html(row.Period)}</td><td>{row.Documents}</td><td>{row.Cases}</td><td class=\"good\">{row.Matched}</td><td class=\"warn\">{row.ProbableMatch}</td><td class=\"warn\">{row.ReviewRequired}</td><td class=\"bad\">{row.NotMatched}</td><td class=\"bad\">{row.NoSignatureDetected}</td><td class=\"bad\">{row.Errors}</td><td>{row.AverageConfidence:0.00}</td><td>{row.AverageDurationMs:0.00}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
    }

    private static void WriteReferenceTable(StringBuilder sb, IReadOnlyList<ReferenceSummary> rows)
    {
        sb.AppendLine("<h2>Reference Usage</h2>");
        sb.AppendLine("<div class=\"toolbar\"><input oninput=\"filterTable('references', this.value)\" placeholder=\"Search references, filenames, result paths\"></div>");
        sb.AppendLine("<table id=\"references\"><thead><tr><th>Reference</th><th>File</th><th>Cases</th><th>Matched</th><th>Review</th><th>Not matched</th><th>Avg confidence</th><th>Last seen</th><th>Last result</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            var resultLink = FileLink(row.LastResultPath, "result");
            sb.AppendLine($"<tr><td>{Html(row.ReferenceId)}</td><td>{FileLink(row.ReferenceFilePath, row.ReferenceFileName)}</td><td>{row.Cases}</td><td class=\"good\">{row.Matched}</td><td class=\"warn\">{row.Review}</td><td class=\"bad\">{row.NotMatched}</td><td>{row.AverageConfidence:0.00}</td><td>{row.LastSeenUtc:u}</td><td>{resultLink}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
    }

    private static void WriteCasesTable(StringBuilder sb, IReadOnlyList<SignatureCase> rows)
    {
        sb.AppendLine("<h2>Reference Case Drilldown</h2>");
        sb.AppendLine("<div class=\"toolbar\"><input oninput=\"filterTable('cases', this.value)\" placeholder=\"Search document, role, decision, reference, correlation ID\"></div>");
        sb.AppendLine("<table id=\"cases\"><thead><tr><th>Date</th><th>Document</th><th>Correlation</th><th>Overall</th><th>Role</th><th>Mapping</th><th>Decision</th><th>Confidence</th><th>Detected</th><th>Best reference</th><th>Reference file</th><th>Result</th><th>Warnings</th></tr></thead><tbody>");
        foreach (var row in rows)
        {
            var cls = row.Decision == "Matched" ? "good" : row.ReviewRequired ? "warn" : "bad";
            var resultLink = FileLink(row.Document.ResultPath, "result");
            sb.AppendLine($"<tr><td>{row.Document.EventUtc:u}</td><td>{Html(row.Document.DocumentName)}</td><td>{Html(row.Document.CorrelationId)}</td><td>{Html(row.Document.OverallDecision)}</td><td>{Html(row.SignatureId)}</td><td>{MappingSummary(row)}</td><td class=\"{cls}\">{Html(row.Decision)}</td><td>{row.Confidence:0.00}</td><td>{row.SignatureDetected}</td><td>{Html(row.BestReferenceId)}</td><td>{FileLink(row.BestReferenceFilePath, ReferenceDisplayName(row.BestReferenceFilePath, row.BestReferenceFileName))}</td><td>{resultLink}</td><td>{row.WarningCount}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
    }

    private static string ReferenceDisplayName(string? path, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var file = Path.GetFileName(path);
            var folder = Path.GetFileName(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return $"{folder} / {file}";
            }

            return file;
        }

        return fallback ?? string.Empty;
    }

    private static string MappingSummary(SignatureCase row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.PartyId)) parts.Add($"Party {row.PartyId}");
        if (!string.IsNullOrWhiteSpace(row.PartyName)) parts.Add(row.PartyName);
        if (!string.IsNullOrWhiteSpace(row.ReferenceSetId)) parts.Add($"Ref set {row.ReferenceSetId}");
        if (!string.IsNullOrWhiteSpace(row.ExpectedSignerId)) parts.Add($"Expected {row.ExpectedSignerId}");
        if (!string.IsNullOrWhiteSpace(row.ActualSignerId)) parts.Add($"Actual {row.ActualSignerId}");
        if (!string.IsNullOrWhiteSpace(row.ExpectedClass)) parts.Add($"Class {row.ExpectedClass}");
        var text = parts.Count == 0 ? "No mapping supplied" : string.Join("<br>", parts.Select(Html));
        var link = FileLink(row.GroundTruthPath, "ground truth");
        return string.IsNullOrWhiteSpace(row.GroundTruthPath) ? text : text + "<br>" + link;
    }

    private static string FileLink(string? path, string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? path ?? string.Empty : label;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Html(text);
        }

        return $"<a href=\"{FileUri(path)}\">{Html(text)}</a>";
    }

    private static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

internal static class ReviewQueueHtmlWriter
{
    public static string Write(BusinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Signature Review Queue</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 28px; color: #202124; }
    h1 { margin: 0 0 8px; font-size: 28px; }
    .meta { color: #57606a; margin-bottom: 16px; }
    .summary { display: grid; grid-template-columns: repeat(4, minmax(140px, 1fr)); gap: 10px; max-width: 900px; margin-bottom: 18px; }
    .metric { border: 1px solid #d0d7de; background: #f8fafc; border-radius: 6px; padding: 10px; }
    .metric span { color: #667085; font-size: 12px; display: block; }
    .metric b { display: block; font-size: 22px; }
    .toolbar { display: flex; gap: 8px; margin: 14px 0; align-items: center; flex-wrap: wrap; }
    input, select { border: 1px solid #b6c2cf; border-radius: 4px; padding: 6px 8px; }
    input.search { width: 360px; }
    button { border: 1px solid #175cd3; background: #175cd3; color: white; border-radius: 4px; padding: 7px 12px; cursor: pointer; }
    button.secondary { border-color: #98a2b3; background: #ffffff; color: #344054; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }
    th { background: #f6f8fa; position: sticky; top: 0; }
    .bad { color: #b42318; font-weight: 600; }
    .warn { color: #b54708; font-weight: 600; }
    .visuals { display: grid; grid-template-columns: minmax(220px, 1fr) minmax(220px, 1fr); gap: 10px; min-width: 470px; }
    .panel { border: 1px solid #d0d7de; border-radius: 6px; background: #f8fafc; padding: 8px; }
    .panel b { display: block; font-size: 12px; color: #475467; margin-bottom: 6px; }
    .panel img { display: block; width: 100%; max-height: 210px; object-fit: contain; background: white; border: 1px solid #eaecf0; }
    .panel canvas { display: block; width: 100%; max-height: 210px; background: white; border: 1px solid #eaecf0; }
    .fallback { color: #667085; font-size: 12px; }
    .saved { color: #067647; font-size: 12px; white-space: nowrap; }
    .unsaved { color: #b54708; font-size: 12px; white-space: nowrap; }
    a { color: #175cd3; text-decoration: none; }
    a:hover { text-decoration: underline; }
  </style>
  <script>
    function filterTable(value) {
      const q = value.toLowerCase();
      document.querySelectorAll('#queue tbody tr').forEach(row => {
        row.style.display = row.innerText.toLowerCase().includes(q) ? '' : 'none';
      });
    }
    function csvEscape(value) {
      return '"' + String(value || '').replaceAll('"', '""') + '"';
    }
    function rowKey(row) {
      return 'signature-review:' + [
        row.dataset.document || '',
        row.dataset.resultid || '',
        row.dataset.signature || ''
      ].join('|');
    }
    function rowPayload(row) {
      return {
        documentName: row.dataset.document,
        documentResultId: row.dataset.resultid,
        correlationId: row.dataset.correlation,
        signatureId: row.dataset.signature,
        engineDecision: row.dataset.decision,
        engineConfidence: row.dataset.confidence,
        bestReferenceId: row.dataset.referenceid,
        bestReferenceFileName: row.dataset.referencefile,
        sourceDocumentPath: row.dataset.source,
        extractedSignatureImagePath: row.dataset.extracted,
        bestReferenceFilePath: row.dataset.referencepath,
        resultPath: row.dataset.resultpath,
        partyId: row.dataset.partyid,
        partyName: row.dataset.partyname,
        referenceSetId: row.dataset.referenceset,
        expectedSignerId: row.dataset.expectedsigner,
        actualSignerId: row.dataset.actualsigner,
        expectedClass: row.dataset.expectedclass,
        mappingSource: row.dataset.mappingsource,
        groundTruthPath: row.dataset.groundtruth,
        reviewerOutcome: row.querySelector('.outcome').value,
        reasonCode: row.querySelector('.reason').value,
        reviewer: row.querySelector('.reviewer').value,
        reviewNotes: row.querySelector('.notes').value,
        savedUtc: new Date().toISOString()
      };
    }
    function applyPayload(row, payload) {
      if (!payload) return;
      row.querySelector('.outcome').value = payload.reviewerOutcome || '';
      row.querySelector('.reason').value = payload.reasonCode || '';
      row.querySelector('.reviewer').value = payload.reviewer || '';
      row.querySelector('.notes').value = payload.reviewNotes || '';
      const status = row.querySelector('.save-status');
      if (status) {
        status.textContent = payload.savedUtc ? 'Saved ' + payload.savedUtc.substring(0, 19).replace('T', ' ') : 'Saved';
        status.className = 'save-status saved';
      }
    }
    function saveRow(button) {
      const row = button.closest('tr');
      const payload = rowPayload(row);
      localStorage.setItem(rowKey(row), JSON.stringify(payload));
      applyPayload(row, payload);
    }
    function markUnsaved(input) {
      const row = input.closest('tr');
      const status = row.querySelector('.save-status');
      if (status) {
        status.textContent = 'Unsaved change';
        status.className = 'save-status unsaved';
      }
      localStorage.setItem(rowKey(row), JSON.stringify(rowPayload(row)));
      applyPayload(row, JSON.parse(localStorage.getItem(rowKey(row))));
    }
    function restoreSavedRows() {
      document.querySelectorAll('#queue tbody tr').forEach(row => {
        const saved = localStorage.getItem(rowKey(row));
        if (saved) {
          try { applyPayload(row, JSON.parse(saved)); } catch {}
        }
        row.querySelectorAll('select,input').forEach(input => {
          input.addEventListener('change', () => markUnsaved(input));
          input.addEventListener('input', () => markUnsaved(input));
        });
      });
    }
    function exportOutcomes() {
      const rows = [['documentName','documentResultId','correlationId','signatureId','engineDecision','engineConfidence','bestReferenceId','bestReferenceFileName','sourceDocumentPath','extractedSignatureImagePath','bestReferenceFilePath','resultPath','partyId','partyName','referenceSetId','expectedSignerId','actualSignerId','expectedClass','mappingSource','groundTruthPath','reviewerOutcome','reasonCode','reviewer','reviewNotes','savedUtc']];
      document.querySelectorAll('#queue tbody tr').forEach(row => {
        const payload = rowPayload(row);
        localStorage.setItem(rowKey(row), JSON.stringify(payload));
        rows.push([
          payload.documentName,
          payload.documentResultId,
          payload.correlationId,
          payload.signatureId,
          payload.engineDecision,
          payload.engineConfidence,
          payload.bestReferenceId,
          payload.bestReferenceFileName,
          payload.sourceDocumentPath,
          payload.extractedSignatureImagePath,
          payload.bestReferenceFilePath,
          payload.resultPath,
          payload.partyId,
          payload.partyName,
          payload.referenceSetId,
          payload.expectedSignerId,
          payload.actualSignerId,
          payload.expectedClass,
          payload.mappingSource,
          payload.groundTruthPath,
          payload.reviewerOutcome,
          payload.reasonCode,
          payload.reviewer,
          payload.reviewNotes,
          payload.savedUtc
        ]);
      });
      const csv = rows.map(r => r.map(csvEscape).join(',')).join('\n');
      const blob = new Blob([csv], { type: 'text/csv' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'reviewer-outcomes.csv';
      a.click();
      URL.revokeObjectURL(url);
    }
    function clearSavedOutcomes() {
      if (!confirm('Clear saved reviewer outcomes for this report in this browser?')) return;
      document.querySelectorAll('#queue tbody tr').forEach(row => localStorage.removeItem(rowKey(row)));
      location.reload();
    }
    function renderCrops() {
      document.querySelectorAll('canvas.crop').forEach(canvas => {
        const src = canvas.dataset.src;
        if (!src) return;
        const image = new Image();
        image.onload = () => {
          const ctx = canvas.getContext('2d');
          const x = Number(canvas.dataset.x || 0);
          const y = Number(canvas.dataset.y || 0);
          const w = Number(canvas.dataset.w || image.naturalWidth);
          const h = Number(canvas.dataset.h || image.naturalHeight);
          const pad = Math.max(12, Math.round(Math.min(w, h) * 0.12));
          const sx = Math.max(0, x - pad);
          const sy = Math.max(0, y - pad);
          const sw = Math.min(image.naturalWidth - sx, w + pad * 2);
          const sh = Math.min(image.naturalHeight - sy, h + pad * 2);
          canvas.width = 420;
          canvas.height = 180;
          ctx.fillStyle = '#fff';
          ctx.fillRect(0, 0, canvas.width, canvas.height);
          ctx.drawImage(image, sx, sy, sw, sh, 0, 0, canvas.width, canvas.height);
        };
        image.src = src;
      });
    }
    window.addEventListener('load', () => { restoreSavedRows(); renderCrops(); });
  </script>
</head>
<body>
""");
        sb.AppendLine($"<h1>{Html(report.Title)} - Review Queue</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated {DateTimeOffset.UtcNow:u}<br>Source: {Html(report.InputFolder)}</div>");
        sb.AppendLine("<div class=\"summary\">");
        Metric(sb, "Review cases", report.ReviewQueue.Count);
        Metric(sb, "No signature", report.ReviewQueue.Count(c => !c.SignatureDetected));
        Metric(sb, "Not matched", report.ReviewQueue.Count(c => c.Decision == "NotMatched"));
        Metric(sb, "Errors", report.ReviewQueue.Count(c => !string.IsNullOrWhiteSpace(c.Document.ErrorCode)));
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"toolbar\"><input class=\"search\" oninput=\"filterTable(this.value)\" placeholder=\"Search document, role, reference, decision\"><button onclick=\"exportOutcomes()\">Export Reviewer Outcomes</button><button class=\"secondary\" onclick=\"clearSavedOutcomes()\">Clear Saved Outcomes</button></div>");
        sb.AppendLine("<table id=\"queue\"><thead><tr><th>Date</th><th>Document</th><th>Role</th><th>Mapping</th><th>Engine</th><th>Visual decision view</th><th>Reviewer outcome</th><th>Reason</th><th>Reviewer</th><th>Notes</th><th>Save</th></tr></thead><tbody>");
        foreach (var item in report.ReviewQueue)
        {
            var cls = item.Decision == "NotMatched" || !item.SignatureDetected ? "bad" : "warn";
            var formImage = item.ExtractedSignatureImagePath ?? item.Document.SourceDocumentPath;
            var visual = WriteVisualComparison(item, formImage, item.BestReferenceFilePath);
            sb.AppendLine($"<tr data-document=\"{Attr(item.Document.DocumentName)}\" data-resultid=\"{Attr(item.Document.DocumentResultId)}\" data-correlation=\"{Attr(item.Document.CorrelationId)}\" data-signature=\"{Attr(item.SignatureId)}\" data-decision=\"{Attr(item.Decision)}\" data-confidence=\"{item.Confidence:0.00}\" data-referenceid=\"{Attr(item.BestReferenceId)}\" data-referencefile=\"{Attr(item.BestReferenceFileName)}\" data-source=\"{Attr(item.Document.SourceDocumentPath)}\" data-extracted=\"{Attr(item.ExtractedSignatureImagePath)}\" data-referencepath=\"{Attr(item.BestReferenceFilePath)}\" data-resultpath=\"{Attr(item.Document.ResultPath)}\" data-partyid=\"{Attr(item.PartyId)}\" data-partyname=\"{Attr(item.PartyName)}\" data-referenceset=\"{Attr(item.ReferenceSetId)}\" data-expectedsigner=\"{Attr(item.ExpectedSignerId)}\" data-actualsigner=\"{Attr(item.ActualSignerId)}\" data-expectedclass=\"{Attr(item.ExpectedClass)}\" data-mappingsource=\"{Attr(item.MappingSource)}\" data-groundtruth=\"{Attr(item.GroundTruthPath)}\"><td>{item.Document.EventUtc:u}</td><td>{Html(item.Document.DocumentName)}<br>{FileLink(item.Document.SourceDocumentPath, "open form")}<br>{FileLink(item.Document.ResultPath, "result details")}</td><td>{Html(item.SignatureId)}</td><td>{MappingSummary(item)}</td><td class=\"{cls}\">{Html(item.Decision)}<br><span class=\"fallback\">Confidence {item.Confidence:0.00}</span></td><td>{visual}</td><td><select class=\"outcome\"><option></option><option>Accepted by reviewer</option><option>Rejected by reviewer</option><option>Needs second review</option></select></td><td><select class=\"reason\"><option></option><option>Signature match acceptable</option><option>Signature mismatch</option><option>Missing signature</option><option>Wrong field detected</option><option>Poor scan quality</option><option>Reference outdated</option><option>Reference missing</option></select></td><td><input class=\"reviewer\" size=\"12\"></td><td><input class=\"notes\" size=\"24\"></td><td><button class=\"secondary\" onclick=\"saveRow(this)\">Save</button><br><span class=\"save-status unsaved\">Not saved</span></td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static void Metric(StringBuilder sb, string label, int value) =>
        sb.AppendLine($"<div class=\"metric\"><span>{Html(label)}</span><b>{value}</b></div>");

    private static string WriteVisualComparison(SignatureCase item, string? formImagePath, string? referenceImagePath)
    {
        var formLabel = string.IsNullOrWhiteSpace(item.ExtractedSignatureImagePath) ? "Form / detected area" : "Extracted form signature";
        var referenceName = ReferenceDisplayName(item.BestReferenceFilePath, item.BestReferenceFileName);
        var referenceLabel = string.IsNullOrWhiteSpace(referenceName) ? "Reference signature" : $"Reference: {referenceName}";
        return "<div class=\"visuals\">" +
               FormVisualPanel(item, formImagePath) +
               VisualPanel(referenceLabel, referenceImagePath, "No reference image path available in the result audit.") +
               "</div>";
    }

    private static string VisualPanel(string title, string? imagePath, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath) && IsBrowserImage(imagePath))
        {
            return $"<div class=\"panel\"><b>{Html(title)}</b><a href=\"{FileUri(imagePath)}\"><img src=\"{FileUri(imagePath)}\" alt=\"{Html(title)}\"></a></div>";
        }

        return $"<div class=\"panel\"><b>{Html(title)}</b><div class=\"fallback\">{Html(fallback)}<br>{FileLink(imagePath, "open file")}</div></div>";
    }

    private static string FormVisualPanel(SignatureCase item, string? imagePath)
    {
        if (!string.IsNullOrWhiteSpace(item.ExtractedSignatureImagePath) &&
            File.Exists(item.ExtractedSignatureImagePath) &&
            IsBrowserImage(item.ExtractedSignatureImagePath))
        {
            return VisualPanel("Extracted form signature", item.ExtractedSignatureImagePath, "No extracted signature image available.");
        }

        if (!string.IsNullOrWhiteSpace(imagePath) &&
            File.Exists(imagePath) &&
            IsBrowserImage(imagePath) &&
            item.DetectedWidth > 0 &&
            item.DetectedHeight > 0)
        {
            return $"<div class=\"panel\"><b>Detected form signature area</b><a href=\"{FileUri(imagePath)}\"><canvas class=\"crop\" data-src=\"{FileUri(imagePath)}\" data-x=\"{item.DetectedX}\" data-y=\"{item.DetectedY}\" data-w=\"{item.DetectedWidth}\" data-h=\"{item.DetectedHeight}\"></canvas></a></div>";
        }

        return VisualPanel("Form / detected area", imagePath, "No form or extracted signature image available. Enable debug images or run from a folder where the source form can be inferred.");
    }

    private static bool IsBrowserImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileLink(string? path, string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? path ?? string.Empty : label;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Html(text);
        }

        return $"<a href=\"{FileUri(path)}\">{Html(text)}</a>";
    }

    private static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Attr(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string ReferenceDisplayName(string? path, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var file = Path.GetFileName(path);
            var folder = Path.GetFileName(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return $"{folder} / {file}";
            }

            return file;
        }

        return fallback ?? string.Empty;
    }

    private static string MappingSummary(SignatureCase row)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.PartyId)) parts.Add($"Party {row.PartyId}");
        if (!string.IsNullOrWhiteSpace(row.PartyName)) parts.Add(row.PartyName);
        if (!string.IsNullOrWhiteSpace(row.ReferenceSetId)) parts.Add($"Ref set {row.ReferenceSetId}");
        if (!string.IsNullOrWhiteSpace(row.ExpectedSignerId)) parts.Add($"Expected {row.ExpectedSignerId}");
        if (!string.IsNullOrWhiteSpace(row.ActualSignerId)) parts.Add($"Actual {row.ActualSignerId}");
        if (!string.IsNullOrWhiteSpace(row.ExpectedClass)) parts.Add($"Class {row.ExpectedClass}");
        if (!string.IsNullOrWhiteSpace(row.MappingSource)) parts.Add($"Source {row.MappingSource}");
        var text = parts.Count == 0 ? "<span class=\"fallback\">No mapping supplied</span>" : string.Join("<br>", parts.Select(Html));
        var link = FileLink(row.GroundTruthPath, "ground truth");
        return string.IsNullOrWhiteSpace(row.GroundTruthPath) ? text : text + "<br>" + link;
    }
}

internal static class ReferenceRegistryHtmlWriter
{
    public static string Write(BusinessReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Reference Signature Registry</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 28px; color: #202124; }
    h1 { margin: 0 0 8px; font-size: 28px; }
    .meta { color: #57606a; margin-bottom: 16px; }
    input { border: 1px solid #b6c2cf; border-radius: 4px; padding: 7px 9px; width: 380px; margin: 12px 0; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }
    th { background: #f6f8fa; position: sticky; top: 0; }
    .warn { color: #b54708; font-weight: 600; }
    .good { color: #067647; font-weight: 600; }
    a { color: #175cd3; text-decoration: none; }
  </style>
  <script>
    function filterTable(value) {
      const q = value.toLowerCase();
      document.querySelectorAll('#registry tbody tr').forEach(row => {
        row.style.display = row.innerText.toLowerCase().includes(q) ? '' : 'none';
      });
    }
  </script>
</head>
<body>
""");
        sb.AppendLine($"<h1>{Html(report.Title)} - Reference Registry</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated {DateTimeOffset.UtcNow:u}<br>References used: {report.References.Count}</div>");
        sb.AppendLine("<input oninput=\"filterTable(this.value)\" placeholder=\"Search reference ID, filename, path\">");
        sb.AppendLine("<table id=\"registry\"><thead><tr><th>Status</th><th>Reference ID</th><th>Reference file</th><th>Cases</th><th>Matched</th><th>Review</th><th>Not matched</th><th>Avg confidence</th><th>Last seen</th><th>Last result</th></tr></thead><tbody>");
        foreach (var reference in report.References)
        {
            var status = reference.NotMatched > 0 || reference.AverageConfidence < 55 ? "Needs review" : "Active";
            var cls = status == "Active" ? "good" : "warn";
            sb.AppendLine($"<tr><td class=\"{cls}\">{Html(status)}</td><td>{Html(reference.ReferenceId)}</td><td>{FileLink(reference.ReferenceFilePath, ReferenceDisplayName(reference.ReferenceFilePath, reference.ReferenceFileName))}</td><td>{reference.Cases}</td><td>{reference.Matched}</td><td>{reference.Review}</td><td>{reference.NotMatched}</td><td>{reference.AverageConfidence:0.00}</td><td>{reference.LastSeenUtc:u}</td><td>{FileLink(reference.LastResultPath, "result")}</td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string FileLink(string? path, string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? path ?? string.Empty : label;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Html(text);
        }

        return $"<a href=\"{new Uri(Path.GetFullPath(path)).AbsoluteUri}\">{Html(text)}</a>";
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string ReferenceDisplayName(string? path, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var file = Path.GetFileName(path);
            var folder = Path.GetFileName(Path.GetDirectoryName(path));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return $"{folder} / {file}";
            }

            return file;
        }

        return fallback ?? string.Empty;
    }
}

internal static class BatchManifestWriter
{
    public static string Write(BusinessReport report)
    {
        var documents = report.Cases.Select(c => c.Document).DistinctBy(d => d.ResultPath).ToList();
        var payload = new
        {
            batchId = Guid.NewGuid().ToString("N"),
            generatedUtc = DateTimeOffset.UtcNow,
            report.Title,
            report.InputFolder,
            report.OutputFolder,
            report.TotalDocuments,
            report.TotalSignatureCases,
            reviewQueueCases = report.ReviewQueue.Count,
            referencesUsed = report.References.Count,
            matchedCases = report.Cases.Count(c => c.Decision == "Matched"),
            reviewCases = report.Cases.Count(c => c.ReviewRequired),
            notMatchedCases = report.Cases.Count(c => c.Decision == "NotMatched"),
            noSignatureDetectedCases = report.Cases.Count(c => !c.SignatureDetected),
            errorDocuments = documents.Count(d => !string.IsNullOrWhiteSpace(d.ErrorCode) || d.OverallDecision == "Error"),
            averageConfidence = report.Cases.Count == 0 ? 0 : Math.Round(report.Cases.Average(c => c.Confidence), 2),
            averageDurationMs = documents.Count == 0 ? 0 : Math.Round(documents.Average(d => d.DurationMs), 2),
            files = new[]
            {
                "signature-business-report.html",
                "review-queue.html",
                "reference-registry.html",
                "daily-summary.csv",
                "weekly-summary.csv",
                "monthly-summary.csv",
                "reference-summary.csv",
                "reference-cases.csv",
                "review-queue.csv",
                "reviewer-outcomes-template.csv"
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}

internal sealed class FeedbackTuningReport
{
    public string OutputFolder { get; init; } = string.Empty;
    public string ReviewerOutcomesPath { get; init; } = string.Empty;
    public string? CurrentOptionsPath { get; init; }
    public string HtmlPath => Path.Combine(OutputFolder, "feedback-tuning-report.html");
    public string SummaryPath => Path.Combine(OutputFolder, "feedback-tuning-summary.json");
    public string TunedOptionsPath => Path.Combine(OutputFolder, "feedback-tuned-options.json");
    public string ThresholdSweepPath => Path.Combine(OutputFolder, "feedback-threshold-sweep.csv");
    public string ReferenceActionsPath => Path.Combine(OutputFolder, "reference-maintenance-actions.csv");
    public int TotalOutcomes { get; init; }
    public int MatchedOutcomes { get; init; }
    public int AcceptedByReviewer { get; init; }
    public int RejectedByReviewer { get; init; }
    public int NeedsSecondReview { get; init; }
    public int FalseRejectCandidates { get; init; }
    public int FalseAcceptCandidates { get; init; }
    public ThresholdConfig CurrentThresholds { get; init; } = ThresholdConfig.Default;
    public ThresholdRecommendation Recommendation { get; init; } = new();
    public List<FeedbackCase> Cases { get; init; } = new();
    public List<FeedbackThresholdSweepRow> ThresholdSweep { get; init; } = new();
    public List<ReferenceMaintenanceAction> ReferenceActions { get; init; } = new();

    public static FeedbackTuningReport Build(IReadOnlyList<SignatureCase> cases, string reviewerOutcomesPath, string? currentOptionsPath, string outputFolder)
    {
        if (!File.Exists(reviewerOutcomesPath))
        {
            throw new FileNotFoundException($"Reviewer outcomes CSV was not found: {reviewerOutcomesPath}");
        }

        var outcomes = ReviewerOutcomeCsvReader.Read(reviewerOutcomesPath)
            .Where(o => !string.IsNullOrWhiteSpace(o.OutcomeDecision))
            .ToList();
        var byResult = cases
            .Where(c => !string.IsNullOrWhiteSpace(c.Document.DocumentResultId))
            .GroupBy(c => CaseKey(c.Document.DocumentResultId, c.SignatureId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byDocument = cases
            .GroupBy(c => CaseKey(c.Document.DocumentName, c.SignatureId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var feedbackCases = new List<FeedbackCase>();
        foreach (var outcome in outcomes)
        {
            SignatureCase? match = null;
            if (!string.IsNullOrWhiteSpace(outcome.DocumentResultId))
            {
                byResult.TryGetValue(CaseKey(outcome.DocumentResultId, outcome.SignatureId), out match);
            }

            match ??= byDocument.GetValueOrDefault(CaseKey(outcome.DocumentName, outcome.SignatureId));
            feedbackCases.Add(new FeedbackCase(outcome, match));
        }

        var current = ThresholdConfig.Load(currentOptionsPath);
        var accepted = feedbackCases.Where(c => c.IsAccepted && c.ResultCase is not null).ToList();
        var rejected = feedbackCases.Where(c => c.IsRejected && c.ResultCase is not null).ToList();
        var sweep = BuildThresholdSweep(accepted, rejected);
        var recommendation = ThresholdRecommendation.Create(current, accepted, rejected, sweep);
        var actions = BuildReferenceActions(feedbackCases);

        return new FeedbackTuningReport
        {
            OutputFolder = outputFolder,
            ReviewerOutcomesPath = reviewerOutcomesPath,
            CurrentOptionsPath = currentOptionsPath,
            TotalOutcomes = outcomes.Count,
            MatchedOutcomes = feedbackCases.Count(c => c.ResultCase is not null),
            AcceptedByReviewer = feedbackCases.Count(c => c.IsAccepted),
            RejectedByReviewer = feedbackCases.Count(c => c.IsRejected),
            NeedsSecondReview = feedbackCases.Count(c => c.IsSecondReview),
            FalseRejectCandidates = feedbackCases.Count(c => c.IsAccepted && c.ResultCase is not null && !IsEngineAutoAccept(c.ResultCase.Decision)),
            FalseAcceptCandidates = feedbackCases.Count(c => c.IsRejected && c.ResultCase is not null && IsEnginePositiveDecision(c.ResultCase.Decision)),
            CurrentThresholds = current,
            Recommendation = recommendation,
            Cases = feedbackCases,
            ThresholdSweep = sweep,
            ReferenceActions = actions
        };
    }

    public void WriteFiles()
    {
        Directory.CreateDirectory(OutputFolder);
        File.WriteAllText(HtmlPath, FeedbackTuningHtmlWriter.Write(this));
        File.WriteAllText(SummaryPath, JsonSerializer.Serialize(ToSummaryPayload(), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(TunedOptionsPath, Recommendation.WriteTunedOptions(CurrentOptionsPath));
        File.WriteAllText(ThresholdSweepPath, FeedbackCsvWriter.WriteThresholdSweep(ThresholdSweep));
        File.WriteAllText(ReferenceActionsPath, FeedbackCsvWriter.WriteReferenceActions(ReferenceActions));
    }

    private object ToSummaryPayload() => new
    {
        generatedUtc = DateTimeOffset.UtcNow,
        reviewerOutcomesPath = ReviewerOutcomesPath,
        currentOptionsPath = CurrentOptionsPath,
        totalOutcomes = TotalOutcomes,
        matchedOutcomes = MatchedOutcomes,
        acceptedByReviewer = AcceptedByReviewer,
        rejectedByReviewer = RejectedByReviewer,
        needsSecondReview = NeedsSecondReview,
        falseRejectCandidates = FalseRejectCandidates,
        falseAcceptCandidates = FalseAcceptCandidates,
        currentThresholds = CurrentThresholds,
        recommendation = Recommendation,
        referenceActionCount = ReferenceActions.Count,
        evidenceNote = "Rates and recommendations are calculated only from reviewer outcome rows supplied in reviewer-outcomes.csv."
    };

    private static string CaseKey(string? documentKey, string? signatureId) =>
        $"{documentKey ?? string.Empty}|{signatureId ?? string.Empty}";

    private static List<FeedbackThresholdSweepRow> BuildThresholdSweep(IReadOnlyList<FeedbackCase> accepted, IReadOnlyList<FeedbackCase> rejected)
    {
        var rows = new List<FeedbackThresholdSweepRow>();
        for (var threshold = 0; threshold <= 100; threshold++)
        {
            var acceptedAuto = accepted.Count(c => c.Confidence >= threshold);
            var rejectedAuto = rejected.Count(c => c.Confidence >= threshold);
            rows.Add(new FeedbackThresholdSweepRow
            {
                MatchedThreshold = threshold,
                AcceptedAutoAccepted = acceptedAuto,
                AcceptedBelowThreshold = accepted.Count - acceptedAuto,
                RejectedAutoAccepted = rejectedAuto,
                RejectedBelowThreshold = rejected.Count - rejectedAuto,
                FalseRejectRateOnReviewedAccepted = accepted.Count == 0 ? null : Math.Round((accepted.Count - acceptedAuto) / (double)accepted.Count, 4),
                FalseAcceptRateOnReviewedRejected = rejected.Count == 0 ? null : Math.Round(rejectedAuto / (double)rejected.Count, 4)
            });
        }

        return rows;
    }

    private static List<ReferenceMaintenanceAction> BuildReferenceActions(IReadOnlyList<FeedbackCase> rows)
    {
        var actions = new List<ReferenceMaintenanceAction>();
        foreach (var row in rows.Where(r => r.ResultCase is not null))
        {
            var item = row.ResultCase!;
            if (row.IsAccepted && !IsEngineAutoAccept(item.Decision) && !string.IsNullOrWhiteSpace(row.Outcome.ExtractedSignatureImagePath))
            {
                actions.Add(new ReferenceMaintenanceAction
                {
                    Action = "CandidateReferenceSample",
                    Priority = item.Confidence < 55 ? "High" : "Medium",
                    DocumentName = row.Outcome.DocumentName,
                    SignatureId = row.Outcome.SignatureId,
                    ReferenceFilePath = row.Outcome.BestReferenceFilePath,
                    CandidateImagePath = row.Outcome.ExtractedSignatureImagePath,
                    Reason = "Reviewer accepted a case the engine did not auto-accept. Consider this extracted image as an additional reference sample after QA approval."
                });
            }

            if (row.IsRejected && !string.IsNullOrWhiteSpace(row.Outcome.BestReferenceFilePath))
            {
                actions.Add(new ReferenceMaintenanceAction
                {
                    Action = "ReviewReferenceForFalseAcceptRisk",
                    Priority = IsEnginePositiveDecision(item.Decision) ? "High" : "Medium",
                    DocumentName = row.Outcome.DocumentName,
                    SignatureId = row.Outcome.SignatureId,
                    ReferenceFilePath = row.Outcome.BestReferenceFilePath,
                    CandidateImagePath = row.Outcome.ExtractedSignatureImagePath,
                    Reason = "Reviewer rejected a case linked to this reference. Check whether the reference is wrong, outdated, or too permissive."
                });
            }

            if (row.Outcome.ReasonCode is "Reference outdated" or "Reference missing")
            {
                actions.Add(new ReferenceMaintenanceAction
                {
                    Action = "ReferenceEnrollmentNeeded",
                    Priority = "High",
                    DocumentName = row.Outcome.DocumentName,
                    SignatureId = row.Outcome.SignatureId,
                    ReferenceFilePath = row.Outcome.BestReferenceFilePath,
                    CandidateImagePath = row.Outcome.ExtractedSignatureImagePath,
                    Reason = $"Reviewer selected '{row.Outcome.ReasonCode}'."
                });
            }

            if (row.Outcome.ReasonCode is "Missing signature" or "Wrong field detected")
            {
                actions.Add(new ReferenceMaintenanceAction
                {
                    Action = "DetectionZoneReview",
                    Priority = "High",
                    DocumentName = row.Outcome.DocumentName,
                    SignatureId = row.Outcome.SignatureId,
                    ReferenceFilePath = row.Outcome.BestReferenceFilePath,
                    CandidateImagePath = row.Outcome.ExtractedSignatureImagePath,
                    Reason = $"Reviewer selected '{row.Outcome.ReasonCode}'. Review form zones, OCR anchors, or page search settings."
                });
            }
        }

        return actions
            .GroupBy(a => string.Join("|", a.Action, a.DocumentName, a.SignatureId, a.ReferenceFilePath, a.CandidateImagePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsEngineAutoAccept(string? decision) =>
        string.Equals(decision, "Matched", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnginePositiveDecision(string? decision) =>
        decision is not null &&
        (decision.Equals("Matched", StringComparison.OrdinalIgnoreCase) ||
         decision.Equals("ProbableMatch", StringComparison.OrdinalIgnoreCase));
}

internal static class FeedbackTuningHtmlWriter
{
    public static string Write(FeedbackTuningReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <title>Signature Feedback Tuning</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 28px; color: #202124; }
    h1 { margin: 0 0 8px; font-size: 28px; }
    h2 { margin-top: 28px; font-size: 19px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
    .meta, .note { color: #57606a; }
    .metrics { display: grid; grid-template-columns: repeat(4, minmax(150px, 1fr)); gap: 10px; max-width: 980px; margin: 16px 0; }
    .metric { border: 1px solid #d0d7de; border-radius: 6px; background: #f8fafc; padding: 10px; }
    .metric span { display: block; color: #667085; font-size: 12px; }
    .metric b { display: block; font-size: 22px; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; margin-top: 10px; }
    th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }
    th { background: #f6f8fa; }
    .good { color: #067647; font-weight: 600; }
    .warn { color: #b54708; font-weight: 600; }
    .bad { color: #b42318; font-weight: 600; }
    a { color: #175cd3; text-decoration: none; }
  </style>
</head>
<body>
""");
        sb.AppendLine("<h1>Signature Feedback Tuning</h1>");
        sb.AppendLine($"<div class=\"meta\">Generated {DateTimeOffset.UtcNow:u}<br>Reviewer outcomes: {Html(report.ReviewerOutcomesPath)}</div>");
        sb.AppendLine("<p class=\"note\">All percentages below are calculated only from reviewer-labelled rows in the supplied CSV. They are feedback evidence, not production accuracy claims.</p>");
        sb.AppendLine("<div class=\"metrics\">");
        Metric(sb, "Reviewer outcomes", report.TotalOutcomes);
        Metric(sb, "Matched to results", report.MatchedOutcomes);
        Metric(sb, "Accepted by reviewer", report.AcceptedByReviewer);
        Metric(sb, "Rejected by reviewer", report.RejectedByReviewer);
        Metric(sb, "Needs second review", report.NeedsSecondReview);
        Metric(sb, "False reject candidates", report.FalseRejectCandidates);
        Metric(sb, "False accept candidates", report.FalseAcceptCandidates);
        Metric(sb, "Reference actions", report.ReferenceActions.Count);
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Recommended Thresholds</h2>");
        sb.AppendLine("<table><thead><tr><th>Threshold</th><th>Current</th><th>Recommended</th><th>Status</th><th>Evidence</th></tr></thead><tbody>");
        ThresholdRow(sb, "Matched", report.CurrentThresholds.Matched, report.Recommendation.MatchedThreshold, report.Recommendation.MatchedStatus, report.Recommendation.MatchedEvidence);
        ThresholdRow(sb, "Probable match", report.CurrentThresholds.ProbableMatch, report.Recommendation.ProbableMatchThreshold, report.Recommendation.ProbableStatus, report.Recommendation.ProbableEvidence);
        ThresholdRow(sb, "Review required", report.CurrentThresholds.ReviewRequired, report.Recommendation.ReviewRequiredThreshold, report.Recommendation.ReviewStatus, report.Recommendation.ReviewEvidence);
        sb.AppendLine("</tbody></table>");
        sb.AppendLine($"<p>Generated tuned options: {FileLink(report.TunedOptionsPath, "feedback-tuned-options.json")}</p>");

        sb.AppendLine("<h2>Best Threshold Sweep Rows</h2>");
        sb.AppendLine("<table><thead><tr><th>Matched threshold</th><th>Accepted auto-accepted</th><th>Accepted below threshold</th><th>Rejected auto-accepted</th><th>Reviewed accepted false reject rate</th><th>Reviewed rejected false accept rate</th></tr></thead><tbody>");
        foreach (var row in report.ThresholdSweep.Where(r => r.RejectedAutoAccepted == 0).OrderBy(r => r.AcceptedBelowThreshold).ThenBy(r => r.MatchedThreshold).Take(10))
        {
            sb.AppendLine($"<tr><td>{row.MatchedThreshold:0.00}</td><td class=\"good\">{row.AcceptedAutoAccepted}</td><td class=\"warn\">{row.AcceptedBelowThreshold}</td><td class=\"good\">{row.RejectedAutoAccepted}</td><td>{FormatPercent(row.FalseRejectRateOnReviewedAccepted)}</td><td>{FormatPercent(row.FalseAcceptRateOnReviewedRejected)}</td></tr>");
        }

        sb.AppendLine("</tbody></table>");
        sb.AppendLine($"<p>Full sweep CSV: {FileLink(report.ThresholdSweepPath, "feedback-threshold-sweep.csv")}</p>");

        sb.AppendLine("<h2>Reference Maintenance Queue</h2>");
        sb.AppendLine($"<p>CSV: {FileLink(report.ReferenceActionsPath, "reference-maintenance-actions.csv")}</p>");
        sb.AppendLine("<table><thead><tr><th>Priority</th><th>Action</th><th>Document</th><th>Role</th><th>Reference</th><th>Candidate image</th><th>Reason</th></tr></thead><tbody>");
        foreach (var action in report.ReferenceActions)
        {
            var cls = action.Priority == "High" ? "bad" : "warn";
            sb.AppendLine($"<tr><td class=\"{cls}\">{Html(action.Priority)}</td><td>{Html(action.Action)}</td><td>{Html(action.DocumentName)}</td><td>{Html(action.SignatureId)}</td><td>{FileLink(action.ReferenceFilePath, Path.GetFileName(action.ReferenceFilePath ?? string.Empty))}</td><td>{FileLink(action.CandidateImagePath, Path.GetFileName(action.CandidateImagePath ?? string.Empty))}</td><td>{Html(action.Reason)}</td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static void Metric(StringBuilder sb, string label, object value) =>
        sb.AppendLine($"<div class=\"metric\"><span>{Html(label)}</span><b>{Html(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}</b></div>");

    private static void ThresholdRow(StringBuilder sb, string name, double current, double recommended, string status, string evidence)
    {
        var cls = status == "ChangeRecommended" ? "warn" : status == "InsufficientEvidence" ? "bad" : "good";
        sb.AppendLine($"<tr><td>{Html(name)}</td><td>{current:0.00}</td><td>{recommended:0.00}</td><td class=\"{cls}\">{Html(status)}</td><td>{Html(evidence)}</td></tr>");
    }

    private static string FormatPercent(double? value) =>
        value.HasValue ? value.Value.ToString("P2", CultureInfo.InvariantCulture) : "No reviewer labels";

    private static string FileLink(string? path, string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? path ?? string.Empty : label;
        if (string.IsNullOrWhiteSpace(path))
        {
            return Html(text);
        }

        return $"<a href=\"{new Uri(Path.GetFullPath(path)).AbsoluteUri}\">{Html(text)}</a>";
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

internal sealed class ThresholdConfig
{
    public static ThresholdConfig Default { get; } = new() { Matched = 85, ProbableMatch = 70, ReviewRequired = 55 };
    public double Matched { get; init; } = 85;
    public double ProbableMatch { get; init; } = 70;
    public double ReviewRequired { get; init; } = 55;

    public static ThresholdConfig Load(string? optionsPath)
    {
        if (string.IsNullOrWhiteSpace(optionsPath) || !File.Exists(optionsPath))
        {
            return Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(optionsPath));
            if (!doc.RootElement.TryGetProperty("thresholds", out var thresholds))
            {
                return Default;
            }

            return new ThresholdConfig
            {
                Matched = TryGet(thresholds, "matched", Default.Matched),
                ProbableMatch = TryGet(thresholds, "probableMatch", Default.ProbableMatch),
                ReviewRequired = TryGet(thresholds, "reviewRequired", Default.ReviewRequired)
            };
        }
        catch
        {
            return Default;
        }
    }

    private static double TryGet(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : fallback;
}

internal sealed class ThresholdRecommendation
{
    public double MatchedThreshold { get; init; } = 85;
    public double ProbableMatchThreshold { get; init; } = 70;
    public double ReviewRequiredThreshold { get; init; } = 55;
    public string MatchedStatus { get; init; } = "NoChange";
    public string ProbableStatus { get; init; } = "NoChange";
    public string ReviewStatus { get; init; } = "NoChange";
    public string MatchedEvidence { get; init; } = string.Empty;
    public string ProbableEvidence { get; init; } = string.Empty;
    public string ReviewEvidence { get; init; } = string.Empty;

    public static ThresholdRecommendation Create(ThresholdConfig current, IReadOnlyList<FeedbackCase> accepted, IReadOnlyList<FeedbackCase> rejected, IReadOnlyList<FeedbackThresholdSweepRow> sweep)
    {
        var matched = current.Matched;
        var probable = current.ProbableMatch;
        var review = current.ReviewRequired;
        var matchedStatus = "NoChange";
        var probableStatus = "NoChange";
        var reviewStatus = "NoChange";
        var matchedEvidence = "No reviewed threshold change was supported by the supplied labels.";
        var probableEvidence = "No reviewed threshold change was supported by the supplied labels.";
        var reviewEvidence = "No reviewed threshold change was supported by the supplied labels.";

        if (accepted.Count == 0)
        {
            matchedStatus = probableStatus = reviewStatus = "InsufficientEvidence";
            matchedEvidence = probableEvidence = reviewEvidence = "No reviewer-accepted rows were supplied.";
        }
        else
        {
            if (rejected.Count > 0)
            {
                var candidate = sweep
                    .Where(r => r.RejectedAutoAccepted == 0)
                    .OrderByDescending(r => r.AcceptedAutoAccepted)
                    .ThenBy(r => r.MatchedThreshold)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    matched = candidate.MatchedThreshold;
                    matchedStatus = Math.Abs(matched - current.Matched) < 0.01 ? "NoChange" : "ChangeRecommended";
                    matchedEvidence = $"Uses {accepted.Count} reviewer-accepted and {rejected.Count} reviewer-rejected rows; selected the threshold with zero reviewed rejected rows auto-accepted and the highest accepted coverage.";
                }
            }
            else
            {
                matchedStatus = "InsufficientEvidence";
                matchedEvidence = $"There are {accepted.Count} reviewer-accepted rows but no reviewer-rejected rows, so a lower auto-match threshold cannot be validated against false accepts.";
            }

            var acceptedScores = accepted.Select(c => c.Confidence).OrderBy(v => v).ToList();
            var lowAccepted = acceptedScores.First();
            review = Clamp(Math.Min(current.ReviewRequired, Math.Max(0, Math.Floor(lowAccepted - 1))), 0, Math.Max(0, matched - 2));
            reviewStatus = Math.Abs(review - current.ReviewRequired) < 0.01 ? "NoChange" : "ChangeRecommended";
            reviewEvidence = $"Lowest reviewer-accepted confidence was {lowAccepted:0.00}; lower review threshold keeps similar future cases out of hard rejection and in review.";

            var lowerQuartile = Percentile(acceptedScores, 0.25);
            probable = Clamp(Math.Min(current.ProbableMatch, Math.Floor(lowerQuartile)), review + 1, Math.Max(review + 1, matched - 1));
            probableStatus = Math.Abs(probable - current.ProbableMatch) < 0.01 ? "NoChange" : "ChangeRecommended";
            probableEvidence = $"Reviewer-accepted lower quartile confidence was {lowerQuartile:0.00}; probable threshold is kept between review and matched thresholds.";
        }

        return new ThresholdRecommendation
        {
            MatchedThreshold = Math.Round(matched, 2),
            ProbableMatchThreshold = Math.Round(probable, 2),
            ReviewRequiredThreshold = Math.Round(review, 2),
            MatchedStatus = matchedStatus,
            ProbableStatus = probableStatus,
            ReviewStatus = reviewStatus,
            MatchedEvidence = matchedEvidence,
            ProbableEvidence = probableEvidence,
            ReviewEvidence = reviewEvidence
        };
    }

    public string WriteTunedOptions(string? currentOptionsPath)
    {
        JsonObject root;
        if (!string.IsNullOrWhiteSpace(currentOptionsPath) && File.Exists(currentOptionsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(currentOptionsPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var thresholds = root["thresholds"] as JsonObject ?? new JsonObject();
        thresholds["matched"] = MatchedThreshold;
        thresholds["probableMatch"] = ProbableMatchThreshold;
        thresholds["reviewRequired"] = ReviewRequiredThreshold;
        root["thresholds"] = thresholds;
        root["feedbackTuning"] = new JsonObject
        {
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["matchedStatus"] = MatchedStatus,
            ["probableStatus"] = ProbableStatus,
            ["reviewStatus"] = ReviewStatus,
            ["note"] = "Generated from reviewer outcomes. Validate on a holdout set before production rollout."
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var position = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var fraction = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(max, value));
}

internal sealed record FeedbackCase(ReviewerOutcome Outcome, SignatureCase? ResultCase)
{
    public bool IsAccepted => Outcome.OutcomeDecision.Equals("Accepted by reviewer", StringComparison.OrdinalIgnoreCase);
    public bool IsRejected => Outcome.OutcomeDecision.Equals("Rejected by reviewer", StringComparison.OrdinalIgnoreCase);
    public bool IsSecondReview => Outcome.OutcomeDecision.Equals("Needs second review", StringComparison.OrdinalIgnoreCase);
    public double Confidence => ResultCase?.Confidence ?? Outcome.EngineConfidence;
}

internal sealed class ReviewerOutcome
{
    public string DocumentName { get; init; } = string.Empty;
    public string DocumentResultId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string SignatureId { get; init; } = string.Empty;
    public string EngineDecision { get; init; } = string.Empty;
    public double EngineConfidence { get; init; }
    public string BestReferenceId { get; init; } = string.Empty;
    public string BestReferenceFileName { get; init; } = string.Empty;
    public string SourceDocumentPath { get; init; } = string.Empty;
    public string ExtractedSignatureImagePath { get; init; } = string.Empty;
    public string BestReferenceFilePath { get; init; } = string.Empty;
    public string ResultPath { get; init; } = string.Empty;
    public string PartyId { get; init; } = string.Empty;
    public string PartyName { get; init; } = string.Empty;
    public string ReferenceSetId { get; init; } = string.Empty;
    public string ExpectedSignerId { get; init; } = string.Empty;
    public string ActualSignerId { get; init; } = string.Empty;
    public string ExpectedClass { get; init; } = string.Empty;
    public string MappingSource { get; init; } = string.Empty;
    public string GroundTruthPath { get; init; } = string.Empty;
    public string OutcomeDecision { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public string Reviewer { get; init; } = string.Empty;
    public string ReviewNotes { get; init; } = string.Empty;
    public string SavedUtc { get; init; } = string.Empty;
}

internal static class ReviewerOutcomeCsvReader
{
    public static List<ReviewerOutcome> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return new List<ReviewerOutcome>();
        }

        var headers = ParseLine(lines[0]);
        var rows = new List<ReviewerOutcome>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseLine(line);
            var row = headers
                .Select((header, index) => new { header, value = index < values.Count ? values[index] : string.Empty })
                .ToDictionary(x => x.header, x => x.value, StringComparer.OrdinalIgnoreCase);
            rows.Add(new ReviewerOutcome
            {
                DocumentName = Get(row, "documentName"),
                DocumentResultId = Get(row, "documentResultId"),
                CorrelationId = Get(row, "correlationId"),
                SignatureId = Get(row, "signatureId"),
                EngineDecision = Get(row, "engineDecision"),
                EngineConfidence = ParseDouble(Get(row, "engineConfidence")),
                BestReferenceId = Get(row, "bestReferenceId"),
                BestReferenceFileName = Get(row, "bestReferenceFileName"),
                SourceDocumentPath = Get(row, "sourceDocumentPath"),
                ExtractedSignatureImagePath = Get(row, "extractedSignatureImagePath"),
                BestReferenceFilePath = Get(row, "bestReferenceFilePath"),
                ResultPath = Get(row, "resultPath"),
                PartyId = Get(row, "partyId"),
                PartyName = Get(row, "partyName"),
                ReferenceSetId = Get(row, "referenceSetId"),
                ExpectedSignerId = Get(row, "expectedSignerId"),
                ActualSignerId = Get(row, "actualSignerId"),
                ExpectedClass = Get(row, "expectedClass"),
                MappingSource = Get(row, "mappingSource"),
                GroundTruthPath = Get(row, "groundTruthPath"),
                OutcomeDecision = Get(row, "reviewerOutcome"),
                ReasonCode = Get(row, "reasonCode"),
                Reviewer = Get(row, "reviewer"),
                ReviewNotes = Get(row, "reviewNotes"),
                SavedUtc = Get(row, "savedUtc")
            });
        }

        return rows;
    }

    private static List<string> ParseLine(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                values.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        values.Add(sb.ToString());
        return values;
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) ? value : string.Empty;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
}

internal static class FeedbackCsvWriter
{
    public static string WriteThresholdSweep(IReadOnlyList<FeedbackThresholdSweepRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("matchedThreshold,acceptedAutoAccepted,acceptedBelowThreshold,rejectedAutoAccepted,rejectedBelowThreshold,falseRejectRateOnReviewedAccepted,falseAcceptRateOnReviewedRejected");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                Format(row.MatchedThreshold),
                row.AcceptedAutoAccepted,
                row.AcceptedBelowThreshold,
                row.RejectedAutoAccepted,
                row.RejectedBelowThreshold,
                Format(row.FalseRejectRateOnReviewedAccepted),
                Format(row.FalseAcceptRateOnReviewedRejected)));
        }

        return sb.ToString();
    }

    public static string WriteReferenceActions(IReadOnlyList<ReferenceMaintenanceAction> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("priority,action,documentName,signatureId,referenceFilePath,candidateImagePath,reason");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                Esc(row.Priority),
                Esc(row.Action),
                Esc(row.DocumentName),
                Esc(row.SignatureId),
                Esc(row.ReferenceFilePath),
                Esc(row.CandidateImagePath),
                Esc(row.Reason)));
        }

        return sb.ToString();
    }

    private static string Esc(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Format(double? value) => value.HasValue ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture) : string.Empty;
}

internal sealed class FeedbackThresholdSweepRow
{
    public double MatchedThreshold { get; init; }
    public int AcceptedAutoAccepted { get; init; }
    public int AcceptedBelowThreshold { get; init; }
    public int RejectedAutoAccepted { get; init; }
    public int RejectedBelowThreshold { get; init; }
    public double? FalseRejectRateOnReviewedAccepted { get; init; }
    public double? FalseAcceptRateOnReviewedRejected { get; init; }
}

internal sealed class ReferenceMaintenanceAction
{
    public string Priority { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string DocumentName { get; init; } = string.Empty;
    public string SignatureId { get; init; } = string.Empty;
    public string? ReferenceFilePath { get; init; }
    public string? CandidateImagePath { get; init; }
    public string Reason { get; init; } = string.Empty;
}

internal sealed class HealthCheck
{
    public string OutputFolder { get; init; } = string.Empty;
    public string HtmlPath => Path.Combine(OutputFolder, "reporting-health-check.html");
    public string Status => Checks.All(c => c.Status == "Pass") ? "Pass" : "Fail";
    public List<HealthCheckItem> Checks { get; init; } = new();

    public static HealthCheck Run(string inputFolder, string outputFolder)
    {
        var checks = new List<HealthCheckItem>();
        checks.Add(Check("Input folder exists", Directory.Exists(inputFolder), inputFolder));
        checks.Add(Check("Output folder writable", CanWrite(outputFolder), outputFolder));
        var resultFiles = Directory.Exists(inputFolder)
            ? Directory.EnumerateFiles(inputFolder, "*.json", SearchOption.AllDirectories).Count()
            : 0;
        checks.Add(Check("Result JSON files found", resultFiles > 0, $"{resultFiles} JSON file(s)"));
        var readableCases = 0;
        try
        {
            readableCases = Directory.Exists(inputFolder) ? ResultReader.ReadCases(inputFolder).Count() : 0;
        }
        catch
        {
            readableCases = 0;
        }

        checks.Add(Check("Readable signature cases found", readableCases > 0, $"{readableCases} signature case(s)"));

        return new HealthCheck
        {
            OutputFolder = outputFolder,
            Checks = checks
        };
    }

    public void WriteFiles()
    {
        Directory.CreateDirectory(OutputFolder);
        File.WriteAllText(HtmlPath, WriteHtml());
    }

    private string WriteHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Reporting Health Check</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:28px}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border:1px solid #d0d7de;padding:6px 8px;text-align:left}.pass{color:#067647;font-weight:600}.fail{color:#b42318;font-weight:600}</style></head><body>");
        sb.AppendLine($"<h1>Reporting Health Check: {Status}</h1>");
        sb.AppendLine($"<p>Generated {DateTimeOffset.UtcNow:u}</p><table><thead><tr><th>Check</th><th>Status</th><th>Detail</th></tr></thead><tbody>");
        foreach (var check in Checks)
        {
            var cls = check.Status == "Pass" ? "pass" : "fail";
            sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(check.Name)}</td><td class=\"{cls}\">{check.Status}</td><td>{WebUtility.HtmlEncode(check.Detail)}</td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static HealthCheckItem Check(string name, bool passed, string detail) => new(name, passed ? "Pass" : "Fail", detail);

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(path, "ok");
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed record HealthCheckItem(string Name, string Status, string Detail);

internal static class CsvWriter
{
    public static string WritePeriods(IReadOnlyList<PeriodSummary> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("period,documents,cases,matched,probableMatch,reviewRequired,notMatched,noSignatureDetected,errors,averageConfidence,averageDurationMs");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", Esc(row.Period), row.Documents, row.Cases, row.Matched, row.ProbableMatch, row.ReviewRequired, row.NotMatched, row.NoSignatureDetected, row.Errors, Format(row.AverageConfidence), Format(row.AverageDurationMs)));
        }

        return sb.ToString();
    }

    public static string WriteReferences(IReadOnlyList<ReferenceSummary> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("referenceId,referenceFileName,referenceFilePath,cases,matched,review,notMatched,averageConfidence,lastSeenUtc,lastResultPath");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", Esc(row.ReferenceId), Esc(row.ReferenceFileName), Esc(row.ReferenceFilePath), row.Cases, row.Matched, row.Review, row.NotMatched, Format(row.AverageConfidence), Esc(row.LastSeenUtc.ToString("u")), Esc(row.LastResultPath)));
        }

        return sb.ToString();
    }

    public static string WriteCases(IReadOnlyList<SignatureCase> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("eventUtc,documentName,documentResultId,correlationId,overallDecision,signatureId,displayName,partyId,partyName,referenceSetId,expectedSignerId,actualSignerId,expectedClass,mappingSource,groundTruthPath,decision,isMatched,reviewRequired,signatureDetected,confidence,quality,bestReferenceId,bestReferenceFileName,bestReferenceFilePath,sourceDocumentPath,extractedSignatureImagePath,resultPath,warningCount,candidateCount,debugFileCount,durationMs,errorCode");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                Esc(row.Document.EventUtc.ToString("u")),
                Esc(row.Document.DocumentName),
                Esc(row.Document.DocumentResultId),
                Esc(row.Document.CorrelationId),
                Esc(row.Document.OverallDecision),
                Esc(row.SignatureId),
                Esc(row.DisplayName),
                Esc(row.PartyId),
                Esc(row.PartyName),
                Esc(row.ReferenceSetId),
                Esc(row.ExpectedSignerId),
                Esc(row.ActualSignerId),
                Esc(row.ExpectedClass),
                Esc(row.MappingSource),
                Esc(row.GroundTruthPath),
                Esc(row.Decision),
                row.IsMatched,
                row.ReviewRequired,
                row.SignatureDetected,
                Format(row.Confidence),
                Esc(row.SignatureQuality),
                Esc(row.BestReferenceId),
                Esc(row.BestReferenceFileName),
                Esc(row.BestReferenceFilePath),
                Esc(row.Document.SourceDocumentPath),
                Esc(row.ExtractedSignatureImagePath),
                Esc(row.Document.ResultPath),
                row.WarningCount,
                row.CandidateCount,
                row.DebugFileCount,
                Format(row.Document.DurationMs),
                Esc(row.Document.ErrorCode)));
        }

        return sb.ToString();
    }

    public static string WriteReviewerOutcomeTemplate(IReadOnlyList<SignatureCase> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("documentName,documentResultId,correlationId,signatureId,engineDecision,engineConfidence,bestReferenceId,bestReferenceFileName,sourceDocumentPath,extractedSignatureImagePath,bestReferenceFilePath,resultPath,partyId,partyName,referenceSetId,expectedSignerId,actualSignerId,expectedClass,mappingSource,groundTruthPath,reviewerOutcome,reasonCode,reviewer,reviewNotes,savedUtc");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                Esc(row.Document.DocumentName),
                Esc(row.Document.DocumentResultId),
                Esc(row.Document.CorrelationId),
                Esc(row.SignatureId),
                Esc(row.Decision),
                Format(row.Confidence),
                Esc(row.BestReferenceId),
                Esc(row.BestReferenceFileName),
                Esc(row.Document.SourceDocumentPath),
                Esc(row.ExtractedSignatureImagePath),
                Esc(row.BestReferenceFilePath),
                Esc(row.Document.ResultPath),
                Esc(row.PartyId),
                Esc(row.PartyName),
                Esc(row.ReferenceSetId),
                Esc(row.ExpectedSignerId),
                Esc(row.ActualSignerId),
                Esc(row.ExpectedClass),
                Esc(row.MappingSource),
                Esc(row.GroundTruthPath),
                Esc(string.Empty),
                Esc(string.Empty),
                Esc(string.Empty),
                Esc(string.Empty),
                Esc(string.Empty)));
        }

        return sb.ToString();
    }

    private static string Esc(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Format(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

internal sealed class DocumentResult
{
    public string ResultPath { get; init; } = string.Empty;
    public string DocumentName { get; init; } = string.Empty;
    public string? SourceDocumentPath { get; init; }
    public string DocumentResultId { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public DateTimeOffset EventUtc { get; init; }
    public string OverallDecision { get; init; } = "Unknown";
    public double OverallConfidence { get; init; }
    public string InputDocumentType { get; init; } = "Unknown";
    public int SignatureCountExpected { get; init; }
    public int SignatureCountDetected { get; init; }
    public double DurationMs { get; init; }
    public string? ErrorCode { get; init; }
}

internal sealed class SignatureCase
{
    public DocumentResult Document { get; init; } = new();
    public string SignatureId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Decision { get; init; } = "Unknown";
    public bool IsMatched { get; init; }
    public bool ReviewRequired { get; init; }
    public bool SignatureDetected { get; init; }
    public double Confidence { get; init; }
    public string SignatureQuality { get; init; } = "Unknown";
    public string? BestReferenceId { get; init; }
    public string? BestReferenceFileName { get; init; }
    public string? BestReferenceFilePath { get; init; }
    public string? MappingDisplayName { get; init; }
    public string? PartyId { get; init; }
    public string? PartyName { get; init; }
    public string? ReferenceSetId { get; init; }
    public string? ExpectedSignerId { get; init; }
    public string? ActualSignerId { get; init; }
    public string? ExpectedClass { get; init; }
    public string? MappingSource { get; init; }
    public string? GroundTruthPath { get; init; }
    public string? ExtractedSignatureImagePath { get; init; }
    public int DetectedX { get; init; }
    public int DetectedY { get; init; }
    public int DetectedWidth { get; init; }
    public int DetectedHeight { get; init; }
    public int CandidateCount { get; init; }
    public int WarningCount { get; init; }
    public int DebugFileCount { get; init; }
    public string ReferenceKey => !string.IsNullOrWhiteSpace(BestReferenceFilePath)
        ? BestReferenceFilePath
        : !string.IsNullOrWhiteSpace(BestReferenceFileName)
            ? BestReferenceFileName
            : BestReferenceId ?? string.Empty;

    public static SignatureCase ForDocumentOnly(DocumentResult document) => new()
    {
        Document = document,
        Decision = document.OverallDecision,
        SignatureDetected = false,
        Confidence = document.OverallConfidence
    };
}

internal sealed class PeriodSummary
{
    public string Period { get; init; } = string.Empty;
    public int Documents { get; init; }
    public int Cases { get; init; }
    public int Matched { get; init; }
    public int ProbableMatch { get; init; }
    public int ReviewRequired { get; init; }
    public int NotMatched { get; init; }
    public int NoSignatureDetected { get; init; }
    public int Errors { get; init; }
    public double AverageConfidence { get; init; }
    public double AverageDurationMs { get; init; }

    public static PeriodSummary FromCases(string period, IReadOnlyList<SignatureCase> cases)
    {
        var documents = cases.Select(c => c.Document).DistinctBy(d => d.ResultPath).ToList();
        return new PeriodSummary
        {
            Period = period,
            Documents = documents.Count,
            Cases = cases.Count,
            Matched = cases.Count(c => c.Decision == "Matched"),
            ProbableMatch = cases.Count(c => c.Decision == "ProbableMatch"),
            ReviewRequired = cases.Count(c => c.Decision == "ReviewRequired" || c.Decision == "InsufficientQuality"),
            NotMatched = cases.Count(c => c.Decision == "NotMatched"),
            NoSignatureDetected = cases.Count(c => !c.SignatureDetected),
            Errors = documents.Count(d => !string.IsNullOrWhiteSpace(d.ErrorCode) || d.OverallDecision == "Error"),
            AverageConfidence = cases.Count == 0 ? 0 : Math.Round(cases.Average(c => c.Confidence), 2),
            AverageDurationMs = documents.Count == 0 ? 0 : Math.Round(documents.Average(d => d.DurationMs), 2)
        };
    }
}

internal sealed class ReferenceSummary
{
    public string ReferenceKey { get; init; } = string.Empty;
    public string ReferenceId { get; init; } = string.Empty;
    public string ReferenceFileName { get; init; } = string.Empty;
    public string? ReferenceFilePath { get; init; }
    public int Cases { get; init; }
    public int Matched { get; init; }
    public int Review { get; init; }
    public int NotMatched { get; init; }
    public double AverageConfidence { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public string LastResultPath { get; init; } = string.Empty;
}

internal enum PeriodKind
{
    Day,
    Week,
    Month
}

internal sealed record PeriodKey(string Label, string SortKey)
{
    public static PeriodKey Create(DateTimeOffset value, PeriodKind kind)
    {
        var utc = value.ToUniversalTime();
        return kind switch
        {
            PeriodKind.Day => new PeriodKey(utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            PeriodKind.Week => CreateWeek(utc),
            PeriodKind.Month => new PeriodKey(utc.ToString("yyyy-MM", CultureInfo.InvariantCulture), utc.ToString("yyyyMM", CultureInfo.InvariantCulture)),
            _ => new PeriodKey("Unknown", string.Empty)
        };
    }

    private static PeriodKey CreateWeek(DateTimeOffset value)
    {
        var year = ISOWeek.GetYear(value.Date);
        var week = ISOWeek.GetWeekOfYear(value.Date);
        return new PeriodKey($"{year}-W{week:00}", $"{year}{week:00}");
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            options._values[key] = value;
        }

        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;
}
