using Microsoft.Data.SqlClient;
using StaticSignatureVerification.Core;
using StaticSignatureVerification.Production;
using StaticSignatureVerification.Storage;
using System.Data;
using System.Text.Json;

var runtimeOptions = new EnvironmentSignatureVerificationConfigService().GetRuntimeOptions();
var cli = Cli.Parse(args);
if (cli.Has("--help") || args.Length == 0)
{
    Usage();
    return;
}

try
{
    var command = cli.Command;
    var connectionString = cli.Get("--connectionString") ??
                           runtimeOptions.DatabaseConnectionString ??
                           DbStorageDefaults.DefaultLocalDbConnectionString;
    var store = new SqlProductionWorkflowStore(connectionString);

    switch (command)
    {
        case "check-prereqs":
            RunPrereqs(cli, connectionString);
            break;
        case "setup-wizard":
            RunSetupWizard(cli, connectionString);
            break;
        case "enroll-reference":
            await EnrollReference(cli, store);
            break;
        case "approve-reference":
        case "reject-reference":
        case "retire-reference":
            await UpdateReference(command, cli, store);
            break;
        case "scan-duplicates":
            await ScanDuplicates(cli, store);
            break;
        case "create-case":
            await CreateCase(cli, store);
            break;
        case "assign-case":
        case "complete-case":
        case "escalate-case":
        case "cancel-case":
            await UpdateCase(command, cli, store);
            break;
        case "set-retention":
            await SetRetention(cli, store);
            break;
        case "purge":
            await Purge(cli, store);
            break;
        case "export-dr":
            await ExportDr(cli, store, connectionString);
            break;
        case "case-dashboard":
            await CaseDashboard(cli, connectionString);
            break;
        default:
            throw new InvalidOperationException($"Unknown command: {command}");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task EnrollReference(Cli cli, SqlProductionWorkflowStore store)
{
    var imageFile = Required(cli, "--imageFile");
    var referenceSetId = Required(cli, "--referenceSetId");
    var referenceId = Required(cli, "--referenceId");
    var signatureId = Required(cli, "--signatureId");
    var actor = cli.Get("--actor") ?? Environment.UserName;
    var bytes = await File.ReadAllBytesAsync(imageFile);
    var quality = ReferenceQualityAnalyzer.Analyze(bytes);
    var nativeStore = new SqlDatabaseNativeStore(cli.Get("--connectionString") ??
                           Environment.GetEnvironmentVariable("SIGNATURE_VERIFICATION_DB_CONNECTION") ??
                           DbStorageDefaults.DefaultLocalDbConnectionString);
    var storageMode = await nativeStore.GetStorageModeAsync();
    var stored = storageMode == SignatureStorageMode.Database
        ? new StoredArtifact(DbReferenceStorageUri(referenceSetId, referenceId), ReferenceQualityAnalyzer.Sha256Hex(bytes), bytes.LongLength, false)
        : SecureArtifactStore.StoreReferenceImage(
            bytes,
            cli.Get("--storageFolder") ?? Path.Combine(DefaultRoot(), "ReferenceStore"),
            referenceSetId,
            referenceId,
            cli.Bool("--encrypt", true));

    var enrollmentJson = JsonSerializer.Serialize(new
    {
        quality.Status,
        quality.Score,
        quality.Passed,
        quality.Width,
        quality.Height,
        quality.InkDensityPercent,
        quality.Sharpness,
        quality.AverageHash,
        quality.Warnings,
        stored.IsEncrypted,
        stored.SizeBytes
    }, SignatureJsonOptions.Compact);

    var id = await store.EnrollReferenceAsync(new ReferenceEnrollmentRecord(
        ReferenceSetId: referenceSetId,
        ReferenceId: referenceId,
        SignatureId: signatureId,
        DisplayName: cli.Get("--displayName"),
        ExternalPartyId: cli.Get("--partyId"),
        PartyName: cli.Get("--partyName"),
        LanguageCode: cli.Get("--language"),
        SourceFileName: Path.GetFileName(imageFile),
        SourceFilePath: storageMode == SignatureStorageMode.Database ? null : imageFile,
        StorageUri: stored.StorageUri,
        Sha256Hash: stored.Sha256Hash,
        ImageWidth: quality.Width,
        ImageHeight: quality.Height,
        QualityStatus: quality.Status,
        QualityScore: quality.Score,
        PassedQualityGate: quality.Passed,
        Actor: actor,
        EnrollmentJson: enrollmentJson));
    if (storageMode == SignatureStorageMode.Database)
    {
        await nativeStore.SaveReferenceImageBlobAsync(id, ContentType(imageFile), bytes);
        Console.WriteLine("Reference image bytes stored in SQL.");
    }
    else
    {
        await store.RegisterStorageObjectAsync("ReferenceImage", "ReferenceImageRegistry", id.ToString(), stored.StorageUri, DateTimeOffset.UtcNow, null, stored.SizeBytes);
    }

    Console.WriteLine($"Reference enrolled: {referenceId}");
    Console.WriteLine($"ReferenceImageRegistryId: {id}");
    Console.WriteLine($"Quality: {quality.Status} {quality.Score:0.00}");
    if (!quality.Passed)
    {
        Console.WriteLine("Rejected by quality gate: " + string.Join(", ", quality.Warnings));
    }
}

static async Task UpdateReference(string command, Cli cli, SqlProductionWorkflowStore store)
{
    var action = command.Replace("-reference", "", StringComparison.OrdinalIgnoreCase);
    await store.UpdateReferenceStatusAsync(new ReferenceStatusUpdateRecord(
        ReferenceId: Required(cli, "--referenceId"),
        Action: action,
        Actor: cli.Get("--actor") ?? Environment.UserName,
        ReasonCode: cli.Get("--reason"),
        Notes: cli.Get("--notes")));
    Console.WriteLine($"Reference {action} completed.");
}

static async Task ScanDuplicates(Cli cli, SqlProductionWorkflowStore store)
{
    var threshold = cli.Double("--threshold", 92);
    var alerts = await store.ScanDuplicateReferenceAlertsAsync(threshold);
    Console.WriteLine($"Duplicate scan complete. Alerts: {alerts.Count}");
    foreach (var alert in alerts)
    {
        Console.WriteLine($"{alert.AlertId}: {alert.FirstReferenceId} <-> {alert.SecondReferenceId} {alert.SimilarityScore:0.00}% {alert.AlertType}");
    }
}

static async Task CreateCase(Cli cli, SqlProductionWorkflowStore store)
{
    var caseNumber = cli.Get("--caseNumber") ?? "CASE-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
    var id = await store.CreateReviewCaseAsync(new ReviewCaseRecord(
        CaseNumber: caseNumber,
        DocumentResultId: cli.Get("--documentResultId"),
        SignatureId: cli.Get("--signatureId"),
        Priority: cli.Get("--priority") ?? "Normal",
        AssignedTo: cli.Get("--assignedTo"),
        DueUtc: cli.Date("--dueUtc"),
        Branch: cli.Get("--branch"),
        CustomerId: cli.Get("--customerId"),
        DocumentType: cli.Get("--documentType"),
        CaseJson: JsonSerializer.Serialize(SanitizedCliValues(cli), SignatureJsonOptions.Compact)));
    Console.WriteLine($"Review case created: {caseNumber} ({id})");
}

static async Task UpdateCase(string command, Cli cli, SqlProductionWorkflowStore store)
{
    var action = command.Replace("-case", "", StringComparison.OrdinalIgnoreCase);
    await store.UpdateReviewCaseAsync(new ReviewCaseUpdateRecord(
        CaseNumber: Required(cli, "--caseNumber"),
        Action: action,
        Actor: cli.Get("--actor") ?? Environment.UserName,
        AssignedTo: cli.Get("--assignedTo"),
        Notes: cli.Get("--notes")));
    Console.WriteLine($"Review case {action} completed.");
}

static async Task SetRetention(Cli cli, SqlProductionWorkflowStore store)
{
    var id = await store.UpsertRetentionPolicyAsync(new RetentionPolicyRecord(
        PolicyName: Required(cli, "--policyName"),
        TargetObjectType: Required(cli, "--targetObjectType"),
        RetentionDays: cli.Int("--retentionDays", 90),
        IsActive: !cli.Bool("--inactive", false),
        CreatedBy: cli.Get("--actor") ?? Environment.UserName,
        PolicyJson: JsonSerializer.Serialize(SanitizedCliValues(cli), SignatureJsonOptions.Compact)));
    Console.WriteLine($"Retention policy saved: {id}");
}

static async Task Purge(Cli cli, SqlProductionWorkflowStore store)
{
    var dryRun = cli.Bool("--dryRun", false);
    var result = await store.RunRetentionPurgeAsync(DateTimeOffset.UtcNow, cli.Get("--actor") ?? Environment.UserName, cli.Bool("--deleteFiles", false), dryRun);
    Console.WriteLine($"Purge run {result.PurgeRunId}: {result.Status}. Candidates={result.CandidateCount}, Purged={result.PurgedCount}");
    if (dryRun)
    {
        Console.WriteLine("Dry run: no files or database rows were deleted. Re-run without --dryRun to purge for real.");
    }

    if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
    {
        Console.WriteLine(result.ErrorMessage);
    }
}

static void RunPrereqs(Cli cli, string connectionString)
{
    var root = cli.Get("--root") ?? DefaultRoot();
    var checks = ReadinessChecker.Run(
        Path.Combine(root, "Input"),
        Path.Combine(root, "Output"),
        Path.Combine(root, "ReferenceSignatures"),
        cli.Get("--ghostscriptPath"),
        connectionString);
    foreach (var check in checks)
    {
        Console.WriteLine($"{check.Status}: {check.Name} - {check.Message}");
    }
}

static void RunSetupWizard(Cli cli, string connectionString)
{
    var root = cli.Get("--root") ?? DefaultRoot();
    var modeText = cli.Get("--storageMode") ?? PromptStorageMode();
    if (!Enum.TryParse<SignatureStorageMode>(modeText, true, out var mode))
    {
        throw new InvalidOperationException("Storage mode must be Hybrid or Database.");
    }

    Directory.CreateDirectory(root);
    foreach (var folder in new[] { "Input", "Output", "ReferenceSignatures", "BusinessReports", "Logs", "ReferenceStore", "Exports" })
    {
        Directory.CreateDirectory(Path.Combine(root, folder));
    }

    File.WriteAllText(Path.Combine(root, "database-connection.txt"), connectionString);
    new SqlDatabaseNativeStore(connectionString).SetStorageModeAsync(mode, Environment.UserName).GetAwaiter().GetResult();
    RunPrereqs(cli, connectionString);
    Console.WriteLine("Setup completed: " + root);
    Console.WriteLine("Storage mode: " + mode);
}

static async Task ExportDr(Cli cli, SqlProductionWorkflowStore store, string connectionString)
{
    var outputFolder = cli.Get("--outputFolder") ?? Path.Combine(DefaultRoot(), "Exports");
    Directory.CreateDirectory(outputFolder);
    var tables = new[]
    {
        "ReferenceSubject", "ReferenceSetRegistry", "ReferenceImageRegistry", "ReferenceApprovalEvent",
        "ReferenceSimilarityAlert", "FormTemplate", "FormTemplateZone", "ThresholdProfile",
        "ReviewerOutcome", "ReviewCase", "ReviewCaseEvent", "RetentionPolicy", "SecurityRole"
    };

    var files = new Dictionary<string, string>();
    foreach (var table in tables)
    {
        var path = Path.Combine(outputFolder, table + ".json");
        await File.WriteAllTextAsync(path, await DumpTable(connectionString, table));
        files[table + ".json"] = path;
    }

    var manifest = new { createdUtc = DateTimeOffset.UtcNow, tables };
    var zipPath = SecureArtifactStore.CreateZipExport(outputFolder, "signature-dr-export", files, manifest);
    var hash = ReferenceQualityAnalyzer.Sha256Hex(await File.ReadAllBytesAsync(zipPath));
    var id = await store.CreateExportPackageAsync(new ExportPackageRecord("DisasterRecovery", "Completed", zipPath, hash, cli.Get("--actor") ?? Environment.UserName, JsonSerializer.Serialize(manifest, SignatureJsonOptions.Compact)));
    Console.WriteLine($"Export package created: {zipPath}");
    Console.WriteLine($"ExportPackageId: {id}");
}

static async Task CaseDashboard(Cli cli, string connectionString)
{
    var output = cli.Get("--output") ?? Path.Combine(DefaultRoot(), "BusinessReports", "case-management-dashboard.html");
    var rows = await QueryRows(connectionString, "SELECT * FROM ssv.vwCaseManagementQueue ORDER BY Priority DESC, DueUtc, CreatedUtc DESC;");
    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
    await File.WriteAllTextAsync(output, DashboardHtml(rows));
    Console.WriteLine("Case dashboard generated: " + output);
}

static async Task<string> DumpTable(string connectionString, string table)
{
    var rows = await QueryRows(connectionString, $"SELECT * FROM ssv.{table};");
    return JsonSerializer.Serialize(rows, SignatureJsonOptions.Indented);
}

static IReadOnlyDictionary<string, string> SanitizedCliValues(Cli cli)
{
    var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--connectionString",
        "--apiKey",
        "--password",
        "--secret",
        "--token"
    };

    return cli.Values
        .Where(pair => !sensitiveKeys.Contains(pair.Key))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}

static async Task<List<Dictionary<string, object?>>> QueryRows(string connectionString, string sql)
{
    var rows = new List<Dictionary<string, object?>>();
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        rows.Add(row);
    }

    return rows;
}

static string DashboardHtml(IReadOnlyList<Dictionary<string, object?>> rows)
{
    var htmlRows = string.Join(Environment.NewLine, rows.Select(row =>
        "<tr>" + string.Join("", row.Values.Select(value => $"<td>{System.Net.WebUtility.HtmlEncode(Convert.ToString(value))}</td>")) + "</tr>"));
    var headers = rows.Count == 0
        ? "<th>No active cases</th>"
        : string.Join("", rows[0].Keys.Select(key => $"<th>{System.Net.WebUtility.HtmlEncode(key)}</th>"));
    return $$"""
<!doctype html><html><head><meta charset="utf-8"><title>Case Management Dashboard</title>
<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#202124}table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #d0d7de;padding:7px 9px;text-align:left;vertical-align:top}th{background:#f6f8fa;position:sticky;top:0}.toolbar{margin:0 0 14px}input{padding:8px;width:320px}</style>
<script>function filter(q){q=q.toLowerCase();document.querySelectorAll('tbody tr').forEach(r=>r.style.display=r.textContent.toLowerCase().includes(q)?'':'none')}</script></head>
<body><h1>Case Management Dashboard</h1><div class="toolbar"><input placeholder="Filter cases" oninput="filter(this.value)"></div><table><thead><tr>{{headers}}</tr></thead><tbody>{{htmlRows}}</tbody></table></body></html>
""";
}

static string Required(Cli cli, string key) => cli.Get(key) ?? throw new InvalidOperationException($"{key} is required.");

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".tif" or ".tiff" => "image/tiff",
    ".bmp" => "image/bmp",
    ".pdf" => "application/pdf",
    _ => "image/png"
};

static string DbReferenceStorageUri(string referenceSetId, string referenceId) =>
    "sql://ssv.ReferenceImageBlob/reference/" + Uri.EscapeDataString(referenceSetId) + "/" + Uri.EscapeDataString(referenceId);

static string DefaultRoot() =>
    new EnvironmentSignatureVerificationConfigService().GetRuntimeOptions().RootFolder;

static void Usage() => Console.WriteLine("""
StaticSignatureVerification.Operations

Commands:
  check-prereqs
  setup-wizard
  enroll-reference --imageFile <path> --referenceSetId <id> --referenceId <id> --signatureId <role>
  approve-reference --referenceId <id>
  reject-reference --referenceId <id>
  retire-reference --referenceId <id>
  scan-duplicates [--threshold 92]
  create-case --caseNumber <id> [--documentResultId <id>] [--signatureId <role>]
  assign-case --caseNumber <id> --assignedTo <user>
  complete-case --caseNumber <id>
  escalate-case --caseNumber <id>
  cancel-case --caseNumber <id>
  set-retention --policyName <name> --targetObjectType <type> --retentionDays <days>
  purge [--deleteFiles true]
  export-dr [--outputFolder <path>]
  case-dashboard [--output <html>]

Common:
  --connectionString <sql-connection-string>
  --storageMode Hybrid|Database
  or SIGNATURE_VERIFICATION_DB_CONNECTION
""");

static string PromptStorageMode()
{
    if (!Environment.UserInteractive)
    {
        return "Hybrid";
    }

    Console.Write("Choose storage mode [Hybrid/Database] (default Hybrid): ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? "Hybrid" : value.Trim();
}

internal sealed class Cli
{
    public string Command { get; private init; } = "";
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static Cli Parse(string[] args)
    {
        var cli = new Cli { Command = args.FirstOrDefault() ?? "" };
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            cli.Values[key] = value;
        }

        return cli;
    }

    public bool Has(string key) => Values.ContainsKey(key);
    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
    public bool Bool(string key, bool fallback) => Values.TryGetValue(key, out var value) ? bool.TryParse(value, out var parsed) ? parsed : fallback : fallback;
    public int Int(string key, int fallback) => Values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    public double Double(string key, double fallback) => Values.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;
    public DateTimeOffset? Date(string key) => Values.TryGetValue(key, out var value) && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
