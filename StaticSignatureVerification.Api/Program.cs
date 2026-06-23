using StaticSignatureVerification.Production;
using StaticSignatureVerification.Core;
using StaticSignatureVerification.Storage;
using StaticSignatureVerification.TotalAgilityWrapper;
using System.Security.Cryptography;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var app = builder.Build();
var runtimeOptions = new EnvironmentSignatureVerificationConfigService().GetRuntimeOptions();
var connectionString = app.Configuration["SignatureVerification:ConnectionString"] ??
                       runtimeOptions.DatabaseConnectionString ??
                       DbStorageDefaults.DefaultLocalDbConnectionString;
var apiKeys = ApiKeyRegistry.Load(app.Configuration["SignatureVerification:ApiKeys"] ?? Environment.GetEnvironmentVariable("SIGNATURE_API_KEYS"));

app.Use(async (context, next) =>
{
    var requestId = context.Request.Headers.TryGetValue("X-Request-ID", out var supplied) && !string.IsNullOrWhiteSpace(supplied)
        ? supplied.ToString()
        : Guid.NewGuid().ToString("N");
    context.Items["requestId"] = requestId;
    context.Response.Headers["X-Request-ID"] = requestId;
    await next();
});

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var requestId = Convert.ToString(context.Items["requestId"]) ?? Guid.NewGuid().ToString("N");
        app.Logger.LogError(ex, "Unhandled API error for request {RequestId}", requestId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "INTERNAL_ERROR",
            message = "The request could not be completed.",
            requestId
        });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/v1/verify", async (HttpContext context, JsonElement body) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer", "Verifier");
    if (auth is not null) return auth;

    var requestId = Convert.ToString(context.Items["requestId"]) ?? Guid.NewGuid().ToString("N");
    var wrapper = new SignatureVerificationWrapper();
    var resultJson = wrapper.VerifySignaturesRequest(body.GetRawText());
    var store = new SqlVerificationResultStore(connectionString);
    var record = JsonVerificationResultMapper.FromJson(resultJson, documentName: requestId, sourceDocumentPath: null, resultPath: null);
    await store.UpsertVerificationResultAsync(record);
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("ApiVerify", "Information", requestId, context.User.Identity?.Name, "VerificationDocument", record.DocumentResultId, "Verification request completed.", null));
    return Results.Text(resultJson, "application/json");
});

app.MapPost("/api/v1/verify-db-native", async (HttpContext context, DbNativeVerifyRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer", "Verifier");
    if (auth is not null) return auth;

    var requestId = Convert.ToString(context.Items["requestId"]) ?? Guid.NewGuid().ToString("N");
    var nativeStore = new SqlDatabaseNativeStore(connectionString);
    var mode = await nativeStore.GetStorageModeAsync();
    if (mode != SignatureStorageMode.Database)
    {
        return Results.Json(new { error = "DATABASE_NATIVE_MODE_DISABLED", message = "StorageMode must be Database for /api/v1/verify-db-native." }, statusCode: 409);
    }

    if (!TryReadBase64(request.DocumentBase64, out var documentBytes))
    {
        return BadRequest("INVALID_DOCUMENT_BASE64", "documentBase64 must be valid Base64.");
    }

    if (request.SignatureMappings is null || request.SignatureMappings.Count == 0)
    {
        return BadRequest("SIGNATURE_MAPPINGS_REQUIRED", "At least one signature mapping is required.");
    }

    var documentBlobKey = request.DocumentBlobKey ?? request.CorrelationId ?? requestId;
    await nativeStore.SaveDocumentBlobAsync(new DocumentBlobRecord(
        DocumentBlobKey: documentBlobKey,
        CorrelationId: request.CorrelationId ?? requestId,
        DocumentName: request.DocumentName ?? documentBlobKey,
        FileName: request.FileName,
        ContentType: request.ContentType ?? "application/octet-stream",
        ContentBytes: documentBytes,
        CreatedBy: Actor(context),
        RetainUntilUtc: null,
        MetadataJson: JsonSerializer.Serialize(new { request.SignatureMappings }, SignatureJsonOptions.Compact)));

    var referencesJson = await nativeStore.BuildReferenceSignaturesJsonAsync(request.SignatureMappings.Select(m => new DbNativeReferenceRequest(m.SignatureId, m.ReferenceSetId, m.PartyId)).ToList());
    var optionsJson = MergeDbNativeOptions(request.OptionsJson, request.CorrelationId ?? requestId);
    var wrapperRequest = JsonSerializer.Serialize(new
    {
        correlationId = request.CorrelationId ?? requestId,
        documentBase64 = request.DocumentBase64,
        ocrLayoutJson = request.OcrLayoutJson ?? "",
        referenceSignaturesJson = referencesJson,
        optionsJson,
        signatureMappings = request.SignatureMappings,
        signatureRoleMode = request.SignatureRoleMode
    }, SignatureJsonOptions.Compact);

    var resultJson = new SignatureVerificationWrapper().VerifySignaturesRequest(wrapperRequest);
    var record = JsonVerificationResultMapper.FromJson(resultJson, request.DocumentName ?? documentBlobKey, "sql://ssv.DocumentBlob/" + documentBlobKey, null);
    await new SqlVerificationResultStore(connectionString).UpsertVerificationResultAsync(record);
    await nativeStore.LinkDocumentBlobToResultAsync(documentBlobKey, record.DocumentResultId);
    await nativeStore.SaveDebugArtifactBlobsAsync(record.DocumentResultId);
    TryDeleteDirectory(DbNativeDebugFolder(request.CorrelationId ?? requestId));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("ApiVerifyDbNative", "Information", request.CorrelationId ?? requestId, Actor(context), "VerificationDocument", record.DocumentResultId, "DB-native verification request completed.", null));
    return Results.Text(resultJson, "application/json");
});

app.MapPost("/api/v1/references/enroll", async (HttpContext context, ReferenceEnrollRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "ReferenceApprover");
    if (auth is not null) return auth;

    if (!TryReadBase64(request.ImageBase64, out var imageBytes))
    {
        return BadRequest("INVALID_REFERENCE_IMAGE_BASE64", "imageBase64 must be valid Base64.");
    }

    var quality = ReferenceQualityAnalyzer.Analyze(imageBytes);
    var nativeStore = new SqlDatabaseNativeStore(connectionString);
    var storageMode = await nativeStore.GetStorageModeAsync();
    var stored = storageMode == SignatureStorageMode.Database
        ? new StoredArtifact(DbReferenceStorageUri(request.ReferenceSetId, request.ReferenceId), ReferenceQualityAnalyzer.Sha256Hex(imageBytes), imageBytes.LongLength, false)
        : SecureArtifactStore.StoreReferenceImage(
            imageBytes,
            request.StorageFolder ?? runtimeOptions.ReferenceStoreFolder,
            request.ReferenceSetId,
            request.ReferenceId,
            request.Encrypt);
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
    var id = await new SqlProductionWorkflowStore(connectionString).EnrollReferenceAsync(new ReferenceEnrollmentRecord(
        request.ReferenceSetId,
        request.ReferenceId,
        request.SignatureId,
        request.DisplayName,
        request.PartyId,
        request.PartyName,
        request.LanguageCode,
        request.SourceFileName,
        null,
        stored.StorageUri,
        stored.Sha256Hash,
        quality.Width,
        quality.Height,
        quality.Status,
        quality.Score,
        quality.Passed,
        Actor(context),
        enrollmentJson));
    if (storageMode == SignatureStorageMode.Database)
    {
        await nativeStore.SaveReferenceImageBlobAsync(id, request.ContentType ?? "image/png", imageBytes);
    }
    return Results.Ok(new { referenceImageRegistryId = id, quality.Status, quality.Score, quality.Passed, quality.Warnings });
});

app.MapPost("/api/v1/references/{referenceId}/approve", async (HttpContext context, string referenceId, StatusRequest request) =>
    await ReferenceStatus(context, apiKeys, connectionString, referenceId, "approve", request));
app.MapPost("/api/v1/references/{referenceId}/reject", async (HttpContext context, string referenceId, StatusRequest request) =>
    await ReferenceStatus(context, apiKeys, connectionString, referenceId, "reject", request));
app.MapPost("/api/v1/references/{referenceId}/retire", async (HttpContext context, string referenceId, StatusRequest request) =>
    await ReferenceStatus(context, apiKeys, connectionString, referenceId, "retire", request));

app.MapPost("/api/v1/references/scan-duplicates", async (HttpContext context, DuplicateScanRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "ReferenceApprover", "Auditor");
    if (auth is not null) return auth;
    var alerts = await new SqlProductionWorkflowStore(connectionString).ScanDuplicateReferenceAlertsAsync(request.Threshold <= 0 ? 92 : request.Threshold);
    return Results.Ok(alerts);
});

app.MapPost("/api/v1/review-cases", async (HttpContext context, ReviewCaseRecord request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer");
    if (auth is not null) return auth;
    var id = await new SqlProductionWorkflowStore(connectionString).CreateReviewCaseAsync(request);
    return Results.Ok(new { reviewCaseId = id });
});

app.MapPost("/api/v1/review-cases/{caseNumber}/{action}", async (HttpContext context, string caseNumber, string action, ReviewCaseActionRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer");
    if (auth is not null) return auth;
    await new SqlProductionWorkflowStore(connectionString).UpdateReviewCaseAsync(new ReviewCaseUpdateRecord(caseNumber, action, Actor(context), request.AssignedTo, request.Notes));
    return Results.Ok(new { caseNumber, action });
});

app.MapPost("/api/v1/reviewer-outcomes", async (HttpContext context, ReviewerOutcomeRecord request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer");
    if (auth is not null) return auth;
    await new SqlVerificationResultStore(connectionString).SaveReviewerOutcomeAsync(request);
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/v1/retention/purge", async (HttpContext context, PurgeRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    var result = await new SqlProductionWorkflowStore(connectionString).RunRetentionPurgeAsync(DateTimeOffset.UtcNow, Actor(context), request.DeleteFiles);
    return Results.Ok(result);
});

app.MapGet("/api/v1/readiness", (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var root = runtimeOptions.RootFolder;
    var checks = ReadinessChecker.Run(Path.Combine(root, "Input"), Path.Combine(root, "Output"), Path.Combine(root, "ReferenceSignatures"), null, connectionString);
    return Results.Ok(checks);
});

app.MapGet("/admin", () => Results.Text(AdminPortal.Html(), "text/html"));

app.MapGet("/api/v1/admin/summary", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;

    var nativeStore = new SqlDatabaseNativeStore(connectionString);
    var storageMode = await nativeStore.GetStorageModeAsync();
    var counts = await AdminData.QuerySingleAsync(connectionString, """
SELECT
    (SELECT COUNT(*) FROM ssv.VerificationDocument) AS VerificationDocuments,
    (SELECT COUNT(*) FROM ssv.SignatureCase) AS SignatureCases,
    (SELECT COUNT(*) FROM ssv.ReferenceComparison) AS ReferenceComparisons,
    (SELECT COUNT(*) FROM ssv.ReviewerOutcome) AS ReviewerOutcomes,
    (SELECT COUNT(*) FROM ssv.ReferenceSetRegistry) AS ReferenceSets,
    (SELECT COUNT(*) FROM ssv.ReferenceImageRegistry) AS ReferenceImages,
    (SELECT COUNT(*) FROM ssv.ReviewCase) AS ReviewCases,
    (SELECT COUNT(*) FROM ssv.AuditEvent) AS AuditEvents,
    (SELECT COUNT(*) FROM ssv.DocumentBlob) AS DocumentBlobs,
    (SELECT COUNT(*) FROM ssv.ReferenceImageBlob) AS ReferenceImageBlobs,
    (SELECT COUNT(*) FROM ssv.DebugArtifactBlob) AS DebugArtifactBlobs,
    (SELECT COUNT(*) FROM ssv.vwApprovedReferenceImageNative) AS ApprovedNativeReferences;
""");
    var latest = await AdminData.QueryAsync(connectionString, """
SELECT TOP (10) EventUtc, DocumentName, DocumentResultId, OverallDecision, OverallConfidence, SignatureCountExpected, SignatureCountDetected
FROM ssv.VerificationDocument
ORDER BY UpdatedUtc DESC;
""");
    return Results.Ok(new { storageMode = storageMode.ToString(), counts, latest });
});

app.MapGet("/api/v1/admin/settings", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var settings = await AdminData.QueryAsync(connectionString, "SELECT SettingKey, SettingValue, UpdatedBy, UpdatedUtc FROM ssv.SystemSetting ORDER BY SettingKey;");
    return Results.Ok(settings);
});

app.MapPut("/api/v1/admin/settings/storage-mode", async (HttpContext context, StorageModeRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (!Enum.TryParse<SignatureStorageMode>(request.StorageMode, true, out var mode))
    {
        return BadRequest("INVALID_STORAGE_MODE", "Storage mode must be Hybrid or Database.");
    }

    await new SqlDatabaseNativeStore(connectionString).SetStorageModeAsync(mode, Actor(context));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminStorageModeChanged", "Information", null, Actor(context), "SystemSetting", "StorageMode", $"Storage mode changed to {mode}.", null));
    return Results.Ok(new { storageMode = mode.ToString() });
});

app.MapGet("/api/v1/admin/references", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "ReferenceApprover", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, """
SELECT TOP (200)
    s.ReferenceSetId, s.SignatureId, s.DisplayName, s.Status AS SetStatus, s.QualityStatus AS SetQualityStatus, s.QualityScore AS SetQualityScore,
    subj.ExternalPartyId, subj.PartyName,
    i.ReferenceId, i.VersionNumber, i.EnrollmentStatus, i.IsApprovedForMatching, i.QualityStatus, i.QualityScore, i.SourceFileName, i.CreatedUtc, i.ApprovedUtc
FROM ssv.ReferenceSetRegistry s
LEFT JOIN ssv.ReferenceSubject subj ON subj.ReferenceSubjectId = s.ReferenceSubjectId
LEFT JOIN ssv.ReferenceImageRegistry i ON i.ReferenceSetRegistryId = s.ReferenceSetRegistryId
ORDER BY s.UpdatedUtc DESC, i.ReferenceImageRegistryId DESC;
""");
    return Results.Ok(rows);
});

app.MapGet("/api/v1/admin/cases", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, "SELECT TOP (200) * FROM ssv.vwCaseManagementQueue ORDER BY Priority DESC, DueUtc, CreatedUtc DESC;");
    return Results.Ok(rows);
});

app.MapGet("/api/v1/admin/audit", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, """
SELECT TOP (200) EventUtc, EventType, Severity, Actor, EntityType, EntityId, CorrelationId, Message
FROM ssv.AuditEvent
ORDER BY EventUtc DESC;
""");
    return Results.Ok(rows);
});

app.MapGet("/api/v1/admin/retention", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, "SELECT PolicyName, TargetObjectType, RetentionDays, IsActive, CreatedBy, CreatedUtc FROM ssv.RetentionPolicy ORDER BY PolicyName;");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/retention", async (HttpContext context, RetentionPolicyRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.PolicyName) || string.IsNullOrWhiteSpace(request.TargetObjectType) || request.RetentionDays <= 0)
    {
        return BadRequest("INVALID_RETENTION_POLICY", "Policy name, target object type, and positive retention days are required.");
    }

    var id = await new SqlProductionWorkflowStore(connectionString).UpsertRetentionPolicyAsync(new RetentionPolicyRecord(
        request.PolicyName,
        request.TargetObjectType,
        request.RetentionDays,
        request.IsActive,
        Actor(context),
        JsonSerializer.Serialize(request, SignatureJsonOptions.Compact)));
    return Results.Ok(new { retentionPolicyId = id });
});

app.MapGet("/dashboard/cases", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Reviewer", "Auditor");
    if (auth is not null) return auth;
    var html = await DashboardWriter.CaseDashboardHtml(connectionString);
    return Results.Text(html, "text/html");
});

app.Run();

static async Task<IResult> ReferenceStatus(HttpContext context, ApiKeyRegistry apiKeys, string connectionString, string referenceId, string action, StatusRequest request)
{
    var auth = RequireRole(context, apiKeys, "Administrator", "ReferenceApprover");
    if (auth is not null) return auth;
    await new SqlProductionWorkflowStore(connectionString).UpdateReferenceStatusAsync(new ReferenceStatusUpdateRecord(referenceId, action, Actor(context), request.ReasonCode, request.Notes));
    return Results.Ok(new { referenceId, action });
}

static IResult? RequireRole(HttpContext context, ApiKeyRegistry registry, params string[] roles)
{
    if (!context.Request.Headers.TryGetValue("X-API-Key", out var key) ||
        !registry.TryAuthorize(key.ToString(), roles, out var principal))
    {
        return Results.Json(new { error = "UNAUTHORIZED", message = "A valid X-API-Key with the required role is required." }, statusCode: 401);
    }

    context.Items["principal"] = principal;
    return null;
}

static string Actor(HttpContext context) => Convert.ToString(context.Items["principal"]) ?? "api";

static IResult BadRequest(string error, string message) =>
    Results.Json(new { error, message }, statusCode: StatusCodes.Status400BadRequest);

static bool TryReadBase64(string? base64, out byte[] bytes)
{
    bytes = Array.Empty<byte>();
    if (string.IsNullOrWhiteSpace(base64))
    {
        return false;
    }

    try
    {
        bytes = Convert.FromBase64String(base64);
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static string MergeDbNativeOptions(string? optionsJson, string correlationId)
{
    var node = string.IsNullOrWhiteSpace(optionsJson)
        ? new System.Text.Json.Nodes.JsonObject()
        : System.Text.Json.Nodes.JsonNode.Parse(optionsJson)?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
    node["correlationId"] = correlationId;
    node["saveDebugImages"] = true;
    node["debugOutputFolder"] = DbNativeDebugFolder(correlationId);
    return node.ToJsonString(SignatureJsonOptions.Compact);
}

static string DbReferenceStorageUri(string referenceSetId, string referenceId) =>
    "sql://ssv.ReferenceImageBlob/reference/" + Uri.EscapeDataString(referenceSetId) + "/" + Uri.EscapeDataString(referenceId);

static string DbNativeDebugFolder(string correlationId) =>
    Path.Combine(Path.GetTempPath(), "SignatureVerificationDbNative", correlationId, "debug");

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
        // Temporary debug cleanup should never change the verification result.
    }
}

public sealed record ReferenceEnrollRequest(
    string ReferenceSetId,
    string ReferenceId,
    string SignatureId,
    string ImageBase64,
    string? DisplayName,
    string? PartyId,
    string? PartyName,
    string? LanguageCode,
    string? SourceFileName,
    string? ContentType,
    string? StorageFolder,
    bool Encrypt = true);

public sealed record StatusRequest(string? ReasonCode, string? Notes);
public sealed record DuplicateScanRequest(double Threshold);
public sealed record ReviewCaseActionRequest(string? AssignedTo, string? Notes);
public sealed record PurgeRequest(bool DeleteFiles);
public sealed record StorageModeRequest(string StorageMode);
public sealed record RetentionPolicyRequest(string PolicyName, string TargetObjectType, int RetentionDays, bool IsActive = true);
public sealed record DbNativeVerifyRequest(
    string DocumentBase64,
    string? CorrelationId,
    string? DocumentBlobKey,
    string? DocumentName,
    string? FileName,
    string? ContentType,
    string? OcrLayoutJson,
    string? OptionsJson,
    string? SignatureRoleMode,
    List<DbNativeSignatureMapping> SignatureMappings);

public sealed record DbNativeSignatureMapping(
    string SignatureId,
    string? PartyId,
    string? PartyName,
    string? ReferenceSetId,
    string? ExpectedSignerId,
    string? Source);

public sealed class ApiKeyRegistry
{
    private readonly Dictionary<string, (string Principal, HashSet<string> Roles)> _keys;

    private ApiKeyRegistry(Dictionary<string, (string Principal, HashSet<string> Roles)> keys) => _keys = keys;

    public static ApiKeyRegistry Load(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            throw new InvalidOperationException("API keys are required. Set SignatureVerification:ApiKeys or SIGNATURE_API_KEYS.");
        }

        var keys = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal);
        foreach (var part in config.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', 2);
            if (pieces.Length != 2)
            {
                continue;
            }

            var principal = "api-key-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pieces[0])))[..12].ToLowerInvariant();
            keys[pieces[0]] = (principal, pieces[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException("No valid API keys were configured. Use '<key>:Role1,Role2' entries separated by semicolons.");
        }

        return new ApiKeyRegistry(keys);
    }

    public bool TryAuthorize(string key, IEnumerable<string> requiredRoles, out string principal)
    {
        principal = "";
        if (!_keys.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (!requiredRoles.Any(role => entry.Roles.Contains(role)))
        {
            return false;
        }

        principal = entry.Principal;
        return true;
    }
}

public static class DashboardWriter
{
    public static async Task<string> CaseDashboardHtml(string connectionString)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ssv.vwCaseManagementQueue ORDER BY Priority DESC, DueUtc, CreatedUtc DESC;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        var headers = rows.Count == 0 ? "<th>No active cases</th>" : string.Join("", rows[0].Keys.Select(key => $"<th>{System.Net.WebUtility.HtmlEncode(key)}</th>"));
        var body = string.Join("", rows.Select(row => "<tr>" + string.Join("", row.Values.Select(value => $"<td>{System.Net.WebUtility.HtmlEncode(Convert.ToString(value))}</td>")) + "</tr>"));
        return $$"""<!doctype html><html><head><meta charset="utf-8"><title>Case Management Dashboard</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px}table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #d0d7de;padding:7px 9px;text-align:left}th{background:#f6f8fa;position:sticky;top:0}input{padding:8px;width:320px;margin-bottom:14px}</style><script>function f(q){q=q.toLowerCase();document.querySelectorAll('tbody tr').forEach(r=>r.style.display=r.textContent.toLowerCase().includes(q)?'':'none')}</script></head><body><h1>Case Management Dashboard</h1><input oninput="f(this.value)" placeholder="Filter cases"><table><thead><tr>{{headers}}</tr></thead><tbody>{{body}}</tbody></table></body></html>""";
    }
}

public static class AdminData
{
    public static async Task<Dictionary<string, object?>> QuerySingleAsync(string connectionString, string sql)
    {
        var rows = await QueryAsync(connectionString, sql);
        return rows.FirstOrDefault() ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(string connectionString, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
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
}

public static class AdminPortal
{
    public static string Html() => """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Signature Verification Admin</title>
<style>
:root{color-scheme:light;--bg:#f5f7fa;--panel:#fff;--line:#cfd7e2;--text:#1f2933;--muted:#637083;--accent:#145ea8;--ok:#117a37;--warn:#9a5b00;--bad:#b42318}
*{box-sizing:border-box}body{margin:0;font-family:Segoe UI,Arial,sans-serif;background:var(--bg);color:var(--text);font-size:14px}
header{height:58px;display:flex;align-items:center;justify-content:space-between;padding:0 22px;border-bottom:1px solid var(--line);background:#fff}
h1{font-size:18px;margin:0;font-weight:650}main{padding:18px 22px 28px}.toolbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap}
input,select,button{font:inherit}input,select{height:34px;border:1px solid var(--line);border-radius:4px;padding:0 9px;background:#fff;color:var(--text)}
button{height:34px;border:1px solid #9fb4ca;border-radius:4px;background:#fff;color:#153c5d;padding:0 10px;cursor:pointer}button.primary{background:var(--accent);border-color:var(--accent);color:#fff}button:disabled{opacity:.55;cursor:not-allowed}
.tabs{display:flex;gap:2px;margin:16px 0 0;border-bottom:1px solid var(--line)}.tab{border:0;border-radius:0;background:transparent;height:38px;padding:0 14px;color:var(--muted)}.tab.active{color:var(--accent);border-bottom:3px solid var(--accent);font-weight:650}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px;margin-top:14px}.metric{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:12px}.metric span{display:block;color:var(--muted);font-size:12px}.metric b{display:block;font-size:24px;margin-top:4px}
section{display:none;margin-top:14px}section.active{display:block}.band{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:12px;margin-bottom:12px}
.formrow{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;align-items:end}.field label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}.field input,.field select{width:100%}
table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th,td{border:1px solid var(--line);padding:7px 8px;text-align:left;vertical-align:top}th{background:#eef3f8;color:#263849;position:sticky;top:0;z-index:1}
.tablewrap{max-height:520px;overflow:auto;border:1px solid var(--line);border-radius:6px;background:#fff}.tablewrap table{border:0}.tablewrap th:first-child,.tablewrap td:first-child{border-left:0}.tablewrap th:last-child,.tablewrap td:last-child{border-right:0}
.status{font-weight:650}.Pass,.ok{color:var(--ok)}.Warn,.warn{color:var(--warn)}.Fail,.bad{color:var(--bad)}.muted{color:var(--muted)}.message{min-height:22px;margin-top:10px;color:var(--muted)}.filter{width:260px}.secret{width:min(460px,100%)}
</style>
</head>
<body>
<header>
  <h1>Signature Verification Admin</h1>
  <div class="toolbar">
    <input id="apiKey" class="secret" type="password" autocomplete="off" placeholder="X-API-Key">
    <button id="saveKey" title="Save key">Save</button>
    <button id="refresh" class="primary" title="Refresh">Refresh</button>
  </div>
</header>
<main>
  <div class="tabs">
    <button class="tab active" data-tab="overview">Overview</button>
    <button class="tab" data-tab="settings">Settings</button>
    <button class="tab" data-tab="references">References</button>
    <button class="tab" data-tab="cases">Cases</button>
    <button class="tab" data-tab="retention">Retention</button>
    <button class="tab" data-tab="audit">Audit</button>
  </div>

  <section id="overview" class="active">
    <div id="metrics" class="grid"></div>
    <div class="band"><h2>Readiness</h2><div id="readiness"></div></div>
    <div class="band"><h2>Latest Documents</h2><div id="latest"></div></div>
  </section>

  <section id="settings">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Storage mode</label><select id="storageMode"><option>Database</option><option>Hybrid</option></select></div>
        <div><button id="saveStorageMode" class="primary">Apply</button></div>
      </div>
    </div>
    <div id="settingsTable"></div>
  </section>

  <section id="references">
    <div class="toolbar"><input class="filter" data-filter="referencesTable" placeholder="Filter"></div>
    <div id="referencesTable"></div>
  </section>

  <section id="cases">
    <div class="toolbar"><input class="filter" data-filter="casesTable" placeholder="Filter"></div>
    <div id="casesTable"></div>
  </section>

  <section id="retention">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Policy</label><input id="policyName" value="DefaultDocumentRetention"></div>
        <div class="field"><label>Object type</label><input id="targetObjectType" value="DocumentBlob"></div>
        <div class="field"><label>Days</label><input id="retentionDays" type="number" min="1" value="90"></div>
        <div class="field"><label>Status</label><select id="retentionActive"><option value="true">Active</option><option value="false">Inactive</option></select></div>
        <div><button id="saveRetention" class="primary">Save</button></div>
      </div>
    </div>
    <div id="retentionTable"></div>
  </section>

  <section id="audit">
    <div class="toolbar"><input class="filter" data-filter="auditTable" placeholder="Filter"></div>
    <div id="auditTable"></div>
  </section>

  <div id="message" class="message"></div>
</main>
<script>
const state={tab:"overview",key:localStorage.getItem("ssvApiKey")||""};
const qs=s=>document.querySelector(s);
const qsa=s=>Array.from(document.querySelectorAll(s));
qs("#apiKey").value=state.key;
qs("#saveKey").onclick=()=>{state.key=qs("#apiKey").value.trim();localStorage.setItem("ssvApiKey",state.key);msg("Key saved.");loadAll();};
qs("#refresh").onclick=()=>loadAll();
qsa(".tab").forEach(b=>b.onclick=()=>{qsa(".tab").forEach(x=>x.classList.remove("active"));qsa("section").forEach(x=>x.classList.remove("active"));b.classList.add("active");qs("#"+b.dataset.tab).classList.add("active");state.tab=b.dataset.tab;loadTab();});
qsa(".filter").forEach(i=>i.oninput=()=>filterTable(i.dataset.filter,i.value));
qs("#saveStorageMode").onclick=saveStorageMode;
qs("#saveRetention").onclick=saveRetention;
function msg(text,cls=""){const m=qs("#message");m.className="message "+cls;m.textContent=text;}
async function api(path,options={}){
  if(!state.key)throw new Error("API key required.");
  const headers=Object.assign({"X-API-Key":state.key,"X-Request-ID":"admin-"+crypto.randomUUID()},options.headers||{});
  const res=await fetch(path,Object.assign({},options,{headers}));
  if(!res.ok){let body={};try{body=await res.json();}catch{}throw new Error(body.message||body.error||res.statusText);}
  return await res.json();
}
function metric(label,value){return `<div class="metric"><span>${esc(label)}</span><b>${esc(value)}</b></div>`}
function table(rows,id){
  if(!rows||!rows.length)return `<div class="muted">No rows</div>`;
  const keys=Object.keys(rows[0]);
  return `<div class="tablewrap" id="${id||""}"><table><thead><tr>${keys.map(k=>`<th>${esc(k)}</th>`).join("")}</tr></thead><tbody>${rows.map(r=>`<tr>${keys.map(k=>`<td>${esc(format(r[k]))}</td>`).join("")}</tr>`).join("")}</tbody></table></div>`;
}
function filterTable(id,q){q=q.toLowerCase();const root=qs("#"+id);if(!root)return;root.querySelectorAll("tbody tr").forEach(r=>r.style.display=r.textContent.toLowerCase().includes(q)?"":"none");}
function format(v){if(v===null||v===undefined)return "";if(typeof v==="string"&&/^\d{4}-\d{2}-\d{2}T/.test(v))return new Date(v).toLocaleString();return v;}
function esc(v){return String(v??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));}
async function loadAll(){try{await loadOverview();await loadSettings();msg("Loaded.","ok");}catch(e){msg(e.message,"bad");}}
async function loadTab(){try{if(state.tab==="overview")await loadOverview();if(state.tab==="settings")await loadSettings();if(state.tab==="references")await loadReferences();if(state.tab==="cases")await loadCases();if(state.tab==="retention")await loadRetention();if(state.tab==="audit")await loadAudit();}catch(e){msg(e.message,"bad");}}
async function loadOverview(){
  const summary=await api("/api/v1/admin/summary");
  const readiness=await api("/api/v1/readiness");
  qs("#storageMode").value=summary.storageMode||"Database";
  const counts=summary.counts||{};
  qs("#metrics").innerHTML=[metric("Storage mode",summary.storageMode),...Object.keys(counts).map(k=>metric(k,counts[k]))].join("");
  qs("#readiness").innerHTML=table(readiness.map(x=>({Name:x.name,Status:x.status,Message:x.message})),"readinessTable");
  qs("#latest").innerHTML=table(summary.latest||[],"latestTable");
}
async function loadSettings(){const rows=await api("/api/v1/admin/settings");qs("#settingsTable").innerHTML=table(rows,"settingsRows");}
async function loadReferences(){const rows=await api("/api/v1/admin/references");qs("#referencesTable").innerHTML=table(rows,"referencesTable");}
async function loadCases(){const rows=await api("/api/v1/admin/cases");qs("#casesTable").innerHTML=table(rows,"casesTable");}
async function loadRetention(){const rows=await api("/api/v1/admin/retention");qs("#retentionTable").innerHTML=table(rows,"retentionRows");}
async function loadAudit(){const rows=await api("/api/v1/admin/audit");qs("#auditTable").innerHTML=table(rows,"auditTable");}
async function saveStorageMode(){
  try{const storageMode=qs("#storageMode").value;await api("/api/v1/admin/settings/storage-mode",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify({storageMode})});msg("Storage mode saved.","ok");await loadOverview();await loadSettings();}catch(e){msg(e.message,"bad");}
}
async function saveRetention(){
  try{const payload={policyName:qs("#policyName").value.trim(),targetObjectType:qs("#targetObjectType").value.trim(),retentionDays:Number(qs("#retentionDays").value),isActive:qs("#retentionActive").value==="true"};await api("/api/v1/admin/retention",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Retention policy saved.","ok");await loadRetention();}catch(e){msg(e.message,"bad");}
}
loadAll();
</script>
</body>
</html>
""";
}
