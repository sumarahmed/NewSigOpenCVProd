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
var serviceStartedUtc = DateTimeOffset.UtcNow;
var apiKeys = ApiKeyRegistry.Load(app.Configuration["SignatureVerification:ApiKeys"] ?? Environment.GetEnvironmentVariable("SIGNATURE_API_KEYS"), connectionString);

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

app.MapPut("/api/v1/admin/settings/purge", async (HttpContext context, PurgeSettingsRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    await AdminData.UpsertSettingAsync(connectionString, "FilePurgeEnabled", request.FilePurgeEnabled ? "true" : "false", Actor(context));
    await AdminData.UpsertSettingAsync(connectionString, "DatabaseBlobPurgeEnabled", request.DatabaseBlobPurgeEnabled ? "true" : "false", Actor(context));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminPurgeSettingsChanged", "Information", null, Actor(context), "SystemSetting", "RetentionPurge", "Purge settings changed.", JsonSerializer.Serialize(request, SignatureJsonOptions.Compact)));
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/v1/admin/retention/purge", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    var filePurge = string.Equals(await AdminData.GetSettingAsync(connectionString, "FilePurgeEnabled"), "true", StringComparison.OrdinalIgnoreCase);
    var result = await new SqlProductionWorkflowStore(connectionString).RunRetentionPurgeAsync(DateTimeOffset.UtcNow, Actor(context), filePurge);
    return Results.Ok(result);
});

app.MapGet("/api/v1/admin/api-keys", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, """
SELECT ApiKeyId, KeyName, RolesCsv, IsActive, CreatedBy, CreatedUtc, ExpiresUtc, LastUsedUtc, RevokedUtc, Notes
FROM ssv.ApiKeyRegistry
ORDER BY CreatedUtc DESC;
""");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/api-keys", async (HttpContext context, ApiKeyCreateRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.KeyName) || request.Roles is null || request.Roles.Count == 0)
    {
        return BadRequest("INVALID_API_KEY_REQUEST", "Key name and at least one role are required.");
    }

    var rawKey = "ssv_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var requestedRoles = request.Roles ?? [];
    var rolesCsv = string.Join(",", requestedRoles
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Select(role => role.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase));

    var knownRoles = await AdminData.QueryAsync(connectionString, "SELECT RoleName FROM ssv.SecurityRole WHERE IsActive = 1;");
    var validRoles = knownRoles.Select(row => Convert.ToString(row["RoleName"]) ?? string.Empty).Where(role => !string.IsNullOrWhiteSpace(role)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknownRoles = rolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(role => !validRoles.Contains(role)).ToArray();
    if (unknownRoles.Length > 0)
    {
        return BadRequest("INVALID_API_KEY_ROLES", $"Unknown roles: {string.Join(", ", unknownRoles)}.");
    }

    if (string.IsNullOrWhiteSpace(rolesCsv))
    {
        return BadRequest("INVALID_API_KEY_ROLES", "At least one non-empty role is required.");
    }

    var id = await AdminData.ScalarAsync(connectionString, """
INSERT INTO ssv.ApiKeyRegistry(KeyName, KeyHash, RolesCsv, CreatedBy, ExpiresUtc, Notes)
VALUES(@KeyName, @KeyHash, @RolesCsv, @CreatedBy, @ExpiresUtc, @Notes);
SELECT CONVERT(bigint, SCOPE_IDENTITY());
""", command =>
    {
        AdminData.AddString(command, "@KeyName", request.KeyName);
        AdminData.AddString(command, "@KeyHash", ApiKeyRegistry.HashKey(rawKey));
        AdminData.AddString(command, "@RolesCsv", rolesCsv);
        AdminData.AddString(command, "@CreatedBy", Actor(context));
        AdminData.AddDate(command, "@ExpiresUtc", request.ExpiresUtc);
        AdminData.AddString(command, "@Notes", request.Notes);
    });
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminApiKeyCreated", "Information", null, Actor(context), "ApiKeyRegistry", Convert.ToString(id), "API key created.", JsonSerializer.Serialize(new { request.KeyName, RolesCsv = rolesCsv }, SignatureJsonOptions.Compact)));
    return Results.Ok(new { apiKeyId = id, keyName = request.KeyName, apiKey = rawKey, rolesCsv, message = "Store this value now. It will not be shown again." });
});

app.MapPost("/api/v1/admin/api-keys/{apiKeyId:long}/revoke", async (HttpContext context, long apiKeyId) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    await AdminData.ExecuteAsync(connectionString, "UPDATE ssv.ApiKeyRegistry SET IsActive = 0, RevokedUtc = sysutcdatetime() WHERE ApiKeyId = @ApiKeyId;", command => AdminData.AddLong(command, "@ApiKeyId", apiKeyId));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminApiKeyRevoked", "Information", null, Actor(context), "ApiKeyRegistry", Convert.ToString(apiKeyId), "API key revoked.", null));
    return Results.Ok(new { apiKeyId, revoked = true });
});

app.MapGet("/api/v1/admin/roles", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, "SELECT SecurityRoleId, RoleName, Description, IsActive, CreatedUtc FROM ssv.SecurityRole ORDER BY RoleName;");
    return Results.Ok(rows);
});

app.MapGet("/api/v1/admin/users", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, """
SELECT pr.SecurityPrincipalRoleId, pr.PrincipalName, r.RoleName, pr.GrantedBy, pr.GrantedUtc, pr.RevokedUtc
FROM ssv.SecurityPrincipalRole pr
JOIN ssv.SecurityRole r ON r.SecurityRoleId = pr.SecurityRoleId
ORDER BY pr.PrincipalName, r.RoleName;
""");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/users/roles", async (HttpContext context, UserRoleRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.PrincipalName) || string.IsNullOrWhiteSpace(request.RoleName))
    {
        return BadRequest("INVALID_ROLE_GRANT", "Principal name and role name are required.");
    }

    await AdminData.ExecuteAsync(connectionString, """
DECLARE @RoleId bigint = (SELECT SecurityRoleId FROM ssv.SecurityRole WHERE RoleName = @RoleName AND IsActive = 1);
IF @RoleId IS NULL THROW 51000, 'Role not found or inactive.', 1;
IF NOT EXISTS (SELECT 1 FROM ssv.SecurityPrincipalRole WHERE PrincipalName = @PrincipalName AND SecurityRoleId = @RoleId AND RevokedUtc IS NULL)
    INSERT INTO ssv.SecurityPrincipalRole(PrincipalName, SecurityRoleId, GrantedBy) VALUES(@PrincipalName, @RoleId, @GrantedBy);
""", command =>
    {
        AdminData.AddString(command, "@PrincipalName", request.PrincipalName);
        AdminData.AddString(command, "@RoleName", request.RoleName);
        AdminData.AddString(command, "@GrantedBy", Actor(context));
    });
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminRoleGranted", "Information", null, Actor(context), "SecurityPrincipalRole", request.PrincipalName, "Role granted.", JsonSerializer.Serialize(request, SignatureJsonOptions.Compact)));
    return Results.Ok(new { saved = true });
});

app.MapPost("/api/v1/admin/users/roles/{securityPrincipalRoleId:long}/revoke", async (HttpContext context, long securityPrincipalRoleId) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    await AdminData.ExecuteAsync(connectionString, "UPDATE ssv.SecurityPrincipalRole SET RevokedUtc = sysutcdatetime() WHERE SecurityPrincipalRoleId = @Id AND RevokedUtc IS NULL;", command => AdminData.AddLong(command, "@Id", securityPrincipalRoleId));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminRoleRevoked", "Information", null, Actor(context), "SecurityPrincipalRole", Convert.ToString(securityPrincipalRoleId), "Role revoked.", null));
    return Results.Ok(new { securityPrincipalRoleId, revoked = true });
});

app.MapGet("/api/v1/admin/templates", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, """
SELECT TOP (200) t.FormTemplateId, t.TemplateKey, t.DocumentType, t.TemplateName, t.VersionNumber, t.Status, p.ProfileName AS ThresholdProfile, t.CreatedBy, t.CreatedUtc, t.ApprovedBy, t.ApprovedUtc, t.RetiredBy, t.RetiredUtc
FROM ssv.FormTemplate t
LEFT JOIN ssv.ThresholdProfile p ON p.ThresholdProfileId = t.ThresholdProfileId
ORDER BY t.DocumentType, t.TemplateKey, t.VersionNumber DESC;
""");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/templates", async (HttpContext context, TemplateRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.TemplateKey) || string.IsNullOrWhiteSpace(request.DocumentType) || string.IsNullOrWhiteSpace(request.TemplateName) || request.VersionNumber <= 0)
    {
        return BadRequest("INVALID_TEMPLATE", "Template key, document type, template name, and positive version are required.");
    }

    var id = await AdminData.ScalarAsync(connectionString, """
MERGE ssv.FormTemplate AS target
USING (SELECT @TemplateKey AS TemplateKey, @VersionNumber AS VersionNumber) AS source
ON target.TemplateKey = source.TemplateKey AND target.VersionNumber = source.VersionNumber
WHEN MATCHED THEN UPDATE SET DocumentType = @DocumentType, TemplateName = @TemplateName, Status = @Status, ThresholdProfileId = @ThresholdProfileId, TemplateJson = @TemplateJson
WHEN NOT MATCHED THEN INSERT(TemplateKey, DocumentType, TemplateName, VersionNumber, Status, ThresholdProfileId, CreatedBy, TemplateJson)
VALUES(@TemplateKey, @DocumentType, @TemplateName, @VersionNumber, @Status, @ThresholdProfileId, @CreatedBy, @TemplateJson);
SELECT FormTemplateId FROM ssv.FormTemplate WHERE TemplateKey = @TemplateKey AND VersionNumber = @VersionNumber;
""", command =>
    {
        AdminData.AddString(command, "@TemplateKey", request.TemplateKey);
        AdminData.AddString(command, "@DocumentType", request.DocumentType);
        AdminData.AddString(command, "@TemplateName", request.TemplateName);
        AdminData.AddInt(command, "@VersionNumber", request.VersionNumber);
        AdminData.AddString(command, "@Status", request.Status ?? "Draft");
        AdminData.AddLong(command, "@ThresholdProfileId", request.ThresholdProfileId);
        AdminData.AddString(command, "@CreatedBy", Actor(context));
        AdminData.AddString(command, "@TemplateJson", request.TemplateJson);
    });
    return Results.Ok(new { formTemplateId = id });
});

app.MapGet("/api/v1/admin/templates/{formTemplateId:long}/zones", async (HttpContext context, long formTemplateId) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, $"""
SELECT FormTemplateZoneId, FormTemplateId, SignatureId, DisplayName, PageIndex, X, Y, Width, Height, CoordinateSystem, ExpectedLabel, IsRequired
FROM ssv.FormTemplateZone
WHERE FormTemplateId = {formTemplateId}
ORDER BY PageIndex, SignatureId;
""");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/templates/{formTemplateId:long}/zones", async (HttpContext context, long formTemplateId, TemplateZoneRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.SignatureId) || request.Width <= 0 || request.Height <= 0)
    {
        return BadRequest("INVALID_TEMPLATE_ZONE", "Signature id and positive width/height are required.");
    }

    var id = await AdminData.ScalarAsync(connectionString, """
MERGE ssv.FormTemplateZone AS target
USING (SELECT @FormTemplateId AS FormTemplateId, @SignatureId AS SignatureId) AS source
ON target.FormTemplateId = source.FormTemplateId AND target.SignatureId = source.SignatureId
WHEN MATCHED THEN UPDATE SET DisplayName = @DisplayName, PageIndex = @PageIndex, X = @X, Y = @Y, Width = @Width, Height = @Height, CoordinateSystem = @CoordinateSystem, ExpectedLabel = @ExpectedLabel, IsRequired = @IsRequired, ZoneJson = @ZoneJson
WHEN NOT MATCHED THEN INSERT(FormTemplateId, SignatureId, DisplayName, PageIndex, X, Y, Width, Height, CoordinateSystem, ExpectedLabel, IsRequired, ZoneJson)
VALUES(@FormTemplateId, @SignatureId, @DisplayName, @PageIndex, @X, @Y, @Width, @Height, @CoordinateSystem, @ExpectedLabel, @IsRequired, @ZoneJson);
SELECT FormTemplateZoneId FROM ssv.FormTemplateZone WHERE FormTemplateId = @FormTemplateId AND SignatureId = @SignatureId;
""", command =>
    {
        AdminData.AddLong(command, "@FormTemplateId", formTemplateId);
        AdminData.AddString(command, "@SignatureId", request.SignatureId);
        AdminData.AddString(command, "@DisplayName", request.DisplayName);
        AdminData.AddInt(command, "@PageIndex", request.PageIndex);
        AdminData.AddDecimal(command, "@X", request.X);
        AdminData.AddDecimal(command, "@Y", request.Y);
        AdminData.AddDecimal(command, "@Width", request.Width);
        AdminData.AddDecimal(command, "@Height", request.Height);
        AdminData.AddString(command, "@CoordinateSystem", request.CoordinateSystem ?? "PdfPoints");
        AdminData.AddString(command, "@ExpectedLabel", request.ExpectedLabel);
        AdminData.AddBool(command, "@IsRequired", request.IsRequired);
        AdminData.AddString(command, "@ZoneJson", request.ZoneJson);
    });
    return Results.Ok(new { formTemplateZoneId = id });
});

app.MapGet("/api/v1/admin/threshold-profiles", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, "SELECT ThresholdProfileId, ProfileName, MatchThreshold, ReviewThreshold, IsActive, CreatedBy, CreatedUtc, ProfileJson FROM ssv.ThresholdProfile ORDER BY IsActive DESC, ProfileName;");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/threshold-profiles", async (HttpContext context, ThresholdProfileRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator");
    if (auth is not null) return auth;
    if (string.IsNullOrWhiteSpace(request.ProfileName) || request.MatchThreshold <= 0 || request.ReviewThreshold <= 0)
    {
        return BadRequest("INVALID_THRESHOLD_PROFILE", "Profile name and positive thresholds are required.");
    }

    var id = await AdminData.ScalarAsync(connectionString, """
MERGE ssv.ThresholdProfile AS target
USING (SELECT @ProfileName AS ProfileName) AS source
ON target.ProfileName = source.ProfileName
WHEN MATCHED THEN UPDATE SET MatchThreshold = @MatchThreshold, ReviewThreshold = @ReviewThreshold, IsActive = @IsActive, ProfileJson = @ProfileJson
WHEN NOT MATCHED THEN INSERT(ProfileName, MatchThreshold, ReviewThreshold, IsActive, CreatedBy, ProfileJson)
VALUES(@ProfileName, @MatchThreshold, @ReviewThreshold, @IsActive, @CreatedBy, @ProfileJson);
SELECT ThresholdProfileId FROM ssv.ThresholdProfile WHERE ProfileName = @ProfileName;
""", command =>
    {
        AdminData.AddString(command, "@ProfileName", request.ProfileName);
        AdminData.AddDecimal(command, "@MatchThreshold", request.MatchThreshold);
        AdminData.AddDecimal(command, "@ReviewThreshold", request.ReviewThreshold);
        AdminData.AddBool(command, "@IsActive", request.IsActive);
        AdminData.AddString(command, "@CreatedBy", Actor(context));
        AdminData.AddString(command, "@ProfileJson", request.ProfileJson);
    });
    return Results.Ok(new { thresholdProfileId = id });
});

app.MapGet("/api/v1/admin/export-packages", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var rows = await AdminData.QueryAsync(connectionString, "SELECT TOP (100) ExportPackageId, PackageType, PackageStatus, StorageUri, Sha256Hash, CreatedBy, CreatedUtc, CompletedUtc, ManifestJson FROM ssv.ExportPackage ORDER BY CreatedUtc DESC;");
    return Results.Ok(rows);
});

app.MapPost("/api/v1/admin/export-packages", async (HttpContext context, ExportRequest request) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var id = await new SqlProductionWorkflowStore(connectionString).CreateExportPackageAsync(new ExportPackageRecord(
        string.IsNullOrWhiteSpace(request.PackageType) ? "Governance" : request.PackageType,
        "Requested",
        request.StorageUri ?? await AdminData.GetSettingAsync(connectionString, "BackupExportFolder"),
        null,
        Actor(context),
        JsonSerializer.Serialize(new { requestedUtc = DateTimeOffset.UtcNow, request.IncludeAudit, request.IncludeReferences, request.IncludeTemplates }, SignatureJsonOptions.Compact)));
    await new SqlProductionWorkflowStore(connectionString).LogAuditEventAsync(new AuditEventRecord("AdminExportRequested", "Information", null, Actor(context), "ExportPackage", Convert.ToString(id), "Export package requested.", null));
    return Results.Ok(new { exportPackageId = id, status = "Requested" });
});

app.MapGet("/api/v1/admin/service-status", async (HttpContext context) =>
{
    var auth = RequireRole(context, apiKeys, "Administrator", "Auditor");
    if (auth is not null) return auth;
    var dbStatus = "ok";
    string? dbMessage = null;
    try
    {
        await AdminData.ScalarAsync(connectionString, "SELECT 1;", _ => { });
    }
    catch (Exception ex)
    {
        dbStatus = "failed";
        dbMessage = ex.Message;
    }

    return Results.Ok(new
    {
        status = dbStatus == "ok" ? "ok" : "degraded",
        utc = DateTimeOffset.UtcNow,
        serviceStartedUtc,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow - serviceStartedUtc).TotalSeconds,
        machine = Environment.MachineName,
        processId = Environment.ProcessId,
        runtimeRoot = runtimeOptions.RootFolder,
        database = new { status = dbStatus, message = dbMessage },
        storage = new
        {
            input = Path.Combine(runtimeOptions.RootFolder, "Input"),
            output = Path.Combine(runtimeOptions.RootFolder, "Output"),
            references = runtimeOptions.ReferenceStoreFolder
        }
    });
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

    var existingTarget = Convert.ToString(await AdminData.ScalarAsync(connectionString, "SELECT TargetObjectType FROM ssv.RetentionPolicy WHERE PolicyName = @PolicyName;", command => AdminData.AddString(command, "@PolicyName", request.PolicyName)));
    if (string.IsNullOrWhiteSpace(existingTarget))
    {
        return BadRequest("UNKNOWN_RETENTION_POLICY", "Select an existing retention policy to update.");
    }

    if (!string.Equals(existingTarget, request.TargetObjectType, StringComparison.OrdinalIgnoreCase))
    {
        return BadRequest("RETENTION_POLICY_TARGET_MISMATCH", "The target object type must match the selected retention policy.");
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
public sealed record PurgeSettingsRequest(bool FilePurgeEnabled, bool DatabaseBlobPurgeEnabled);
public sealed record ApiKeyCreateRequest(string KeyName, List<string> Roles, DateTimeOffset? ExpiresUtc, string? Notes);
public sealed record UserRoleRequest(string PrincipalName, string RoleName);
public sealed record TemplateRequest(string TemplateKey, string DocumentType, string TemplateName, int VersionNumber, string? Status, long? ThresholdProfileId, string? TemplateJson);
public sealed record TemplateZoneRequest(string SignatureId, string? DisplayName, int PageIndex, double X, double Y, double Width, double Height, string? CoordinateSystem, string? ExpectedLabel, bool IsRequired, string? ZoneJson);
public sealed record ThresholdProfileRequest(string ProfileName, double MatchThreshold, double ReviewThreshold, bool IsActive, string? ProfileJson);
public sealed record ExportRequest(string? PackageType, string? StorageUri, bool IncludeAudit, bool IncludeReferences, bool IncludeTemplates);
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
    private readonly string _connectionString;

    private ApiKeyRegistry(Dictionary<string, (string Principal, HashSet<string> Roles)> keys, string connectionString)
    {
        _keys = keys;
        _connectionString = connectionString;
    }

    public static ApiKeyRegistry Load(string? config, string connectionString)
    {
        var keys = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal);
        foreach (var part in (config ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
            Console.Error.WriteLine("No config API keys were found. Database-managed API keys must exist in ssv.ApiKeyRegistry.");
        }

        return new ApiKeyRegistry(keys, connectionString);
    }

    public bool TryAuthorize(string key, IEnumerable<string> requiredRoles, out string principal)
    {
        principal = "";
        if (_keys.TryGetValue(key, out var entry))
        {
            if (!requiredRoles.Any(role => entry.Roles.Contains(role)))
            {
                return false;
            }

            principal = entry.Principal;
            return true;
        }

        return TryAuthorizeDatabaseKey(key, requiredRoles, out principal);
    }

    public static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private bool TryAuthorizeDatabaseKey(string key, IEnumerable<string> requiredRoles, out string principal)
    {
        principal = "";
        try
        {
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT TOP (1) ApiKeyId, RolesCsv
FROM ssv.ApiKeyRegistry
WHERE KeyHash = @KeyHash
  AND IsActive = 1
  AND RevokedUtc IS NULL
  AND (ExpiresUtc IS NULL OR ExpiresUtc > sysutcdatetime());
""";
            command.Parameters.AddWithValue("@KeyHash", HashKey(key));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var apiKeyId = reader.GetInt64(0);
            var roles = reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!requiredRoles.Any(role => roles.Contains(role)))
            {
                return false;
            }

            principal = "api-key-db-" + apiKeyId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            reader.Close();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE ssv.ApiKeyRegistry SET LastUsedUtc = sysutcdatetime() WHERE ApiKeyId = @ApiKeyId;";
            update.Parameters.AddWithValue("@ApiKeyId", apiKeyId);
            update.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
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
    public static async Task<object?> ScalarAsync(string connectionString, string sql, Action<Microsoft.Data.SqlClient.SqlCommand> parameters)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        return await command.ExecuteScalarAsync();
    }

    public static async Task ExecuteAsync(string connectionString, string sql, Action<Microsoft.Data.SqlClient.SqlCommand> parameters)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<string?> GetSettingAsync(string connectionString, string settingKey)
    {
        return Convert.ToString(await ScalarAsync(connectionString, "SELECT SettingValue FROM ssv.SystemSetting WHERE SettingKey = @SettingKey;", command => AddString(command, "@SettingKey", settingKey)));
    }

    public static async Task UpsertSettingAsync(string connectionString, string settingKey, string settingValue, string updatedBy)
    {
        await ExecuteAsync(connectionString, """
MERGE ssv.SystemSetting AS target
USING (SELECT @SettingKey AS SettingKey) AS source
ON target.SettingKey = source.SettingKey
WHEN MATCHED THEN UPDATE SET SettingValue = @SettingValue, UpdatedBy = @UpdatedBy, UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT(SettingKey, SettingValue, UpdatedBy) VALUES(@SettingKey, @SettingValue, @UpdatedBy);
""", command =>
        {
            AddString(command, "@SettingKey", settingKey);
            AddString(command, "@SettingValue", settingValue);
            AddString(command, "@UpdatedBy", updatedBy);
        });
    }

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

    public static void AddString(Microsoft.Data.SqlClient.SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.NVarChar);
        parameter.Size = -1;
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    public static void AddLong(Microsoft.Data.SqlClient.SqlCommand command, string name, long? value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.BigInt);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    public static void AddInt(Microsoft.Data.SqlClient.SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    public static void AddDecimal(Microsoft.Data.SqlClient.SqlCommand command, string name, double? value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 6;
        parameter.Value = value.HasValue ? Convert.ToDecimal(value.Value) : DBNull.Value;
    }

    public static void AddBool(Microsoft.Data.SqlClient.SqlCommand command, string name, bool value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.Bit);
        parameter.Value = value;
    }

    public static void AddDate(Microsoft.Data.SqlClient.SqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.DateTimeOffset);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
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
header{min-height:58px;display:flex;align-items:center;justify-content:space-between;gap:14px;padding:10px 22px;border-bottom:1px solid var(--line);background:#fff}
h1{font-size:18px;margin:0;font-weight:650;white-space:nowrap}main{padding:18px 22px 28px}.toolbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;min-width:0}
input,select,button{font:inherit}input,select{height:34px;border:1px solid var(--line);border-radius:4px;padding:0 9px;background:#fff;color:var(--text)}
button{height:34px;border:1px solid #9fb4ca;border-radius:4px;background:#fff;color:#153c5d;padding:0 10px;cursor:pointer}button.primary{background:var(--accent);border-color:var(--accent);color:#fff}button:disabled{opacity:.55;cursor:not-allowed}
.tabs{display:flex;gap:2px;margin:16px 0 0;border-bottom:1px solid var(--line);max-width:100%;overflow-x:auto}.tab{border:0;border-radius:0;background:transparent;height:38px;padding:0 14px;color:var(--muted);flex:0 0 auto}.tab.active{color:var(--accent);border-bottom:3px solid var(--accent);font-weight:650}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px;margin-top:14px}.metric{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:12px}.metric span{display:block;color:var(--muted);font-size:12px}.metric b{display:block;font-size:24px;margin-top:4px}
section{display:none;margin-top:14px}section.active{display:block}.band{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:12px;margin-bottom:12px}
.formrow{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;align-items:end}.field label{display:block;color:var(--muted);font-size:12px;margin-bottom:4px}.field input,.field select{width:100%}
table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th,td{border:1px solid var(--line);padding:7px 8px;text-align:left;vertical-align:top;overflow-wrap:anywhere;word-break:break-word}th{background:#eef3f8;color:#263849;position:sticky;top:0;z-index:1}
.tablewrap{max-height:520px;overflow:auto;border:1px solid var(--line);border-radius:6px;background:#fff}.tablewrap table{border:0}.tablewrap th:first-child,.tablewrap td:first-child{border-left:0}.tablewrap th:last-child,.tablewrap td:last-child{border-right:0}
.status{font-weight:650}.Pass,.ok{color:var(--ok)}.Warn,.warn{color:var(--warn)}.Fail,.bad{color:var(--bad)}.muted{color:var(--muted)}.message{min-height:22px;margin-top:10px;color:var(--muted)}.filter{width:260px}.secret{width:clamp(220px,36vw,460px)}
.roles-field{grid-column:span 2;min-width:min(390px,100%)}.role-options{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:8px;margin-top:4px}.role-option{height:34px;display:flex;align-items:center;gap:8px;border:1px solid var(--line);border-radius:4px;background:#fff;padding:0 10px;white-space:nowrap;overflow:hidden}.role-option input{width:auto;height:auto;flex:0 0 auto;margin:0}.role-option span{overflow:hidden;text-overflow:ellipsis}.token-output{white-space:pre-wrap;overflow-wrap:anywhere;background:#f8fafc;border:1px solid var(--line);border-radius:4px;padding:10px;margin:12px 0 0}
@media(max-width:720px){header{align-items:flex-start;flex-direction:column}.toolbar{width:100%}.secret{width:100%;flex:1 1 100%}.tabs{overflow:auto}.tab{flex:0 0 auto}main{padding:14px 12px 22px}}
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
    <button class="tab" data-tab="keys">API Keys</button>
    <button class="tab" data-tab="users">Users</button>
    <button class="tab" data-tab="templates">Templates</button>
    <button class="tab" data-tab="thresholds">Thresholds</button>
    <button class="tab" data-tab="exports">Backup/Export</button>
    <button class="tab" data-tab="service">Service</button>
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
        <div class="field"><label>File purge</label><select id="filePurgeEnabled"><option value="false">Disabled</option><option value="true">Enabled</option></select></div>
        <div class="field"><label>DB blob purge</label><select id="databaseBlobPurgeEnabled"><option value="true">Enabled</option><option value="false">Disabled</option></select></div>
        <div><button id="savePurgeSettings" class="primary">Save purge</button></div>
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
        <div class="field"><label>Policy</label><select id="policyName"></select></div>
        <div class="field"><label>Object type</label><input id="targetObjectType" readonly></div>
        <div class="field"><label>Days</label><input id="retentionDays" type="number" min="1" value="90"></div>
        <div class="field"><label>Status</label><select id="retentionActive"><option value="true">Active</option><option value="false">Inactive</option></select></div>
        <div><button id="saveRetention" class="primary">Save</button></div>
      </div>
    </div>
    <div id="retentionTable"></div>
  </section>

  <section id="keys">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Key name</label><input id="apiKeyName" value="OperationsAdmin"></div>
        <div class="field roles-field"><label>Roles</label><div id="apiKeyRoles" class="role-options"></div></div>
        <div class="field"><label>Expires UTC</label><input id="apiKeyExpires" placeholder="optional ISO timestamp"></div>
        <div class="field"><label>Notes</label><input id="apiKeyNotes" placeholder="owner / purpose"></div>
        <div><button id="createApiKey" class="primary">Create key</button></div>
      </div>
      <pre id="createdApiKey" class="muted token-output"></pre>
    </div>
    <div id="apiKeysTable"></div>
  </section>

  <section id="users">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Principal</label><input id="principalName" placeholder="domain\\user or service principal"></div>
        <div class="field"><label>Role</label><select id="roleName"></select></div>
        <div><button id="grantRole" class="primary">Grant</button></div>
      </div>
    </div>
    <div id="rolesTable"></div>
    <div id="usersTable"></div>
  </section>

  <section id="templates">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Template key</label><input id="templateKey" value="default-form"></div>
        <div class="field"><label>Document type</label><input id="documentType" value="SignatureForm"></div>
        <div class="field"><label>Name</label><input id="templateName" value="Default Signature Form"></div>
        <div class="field"><label>Version</label><input id="templateVersion" type="number" min="1" value="1"></div>
        <div class="field"><label>Status</label><select id="templateStatus"><option>Draft</option><option>Approved</option><option>Retired</option></select></div>
        <div class="field"><label>Threshold profile id</label><input id="templateThresholdProfileId" type="number" placeholder="optional"></div>
        <div><button id="saveTemplate" class="primary">Save template</button></div>
      </div>
    </div>
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Template id</label><input id="zoneTemplateId" type="number"></div>
        <div class="field"><label>Signature id</label><input id="zoneSignatureId" value="sig1"></div>
        <div class="field"><label>Page</label><input id="zonePageIndex" type="number" value="0"></div>
        <div class="field"><label>X</label><input id="zoneX" type="number" value="0"></div>
        <div class="field"><label>Y</label><input id="zoneY" type="number" value="0"></div>
        <div class="field"><label>Width</label><input id="zoneWidth" type="number" value="100"></div>
        <div class="field"><label>Height</label><input id="zoneHeight" type="number" value="40"></div>
        <div><button id="saveZone" class="primary">Save zone</button></div>
      </div>
    </div>
    <div id="templatesTable"></div>
    <div id="zonesTable"></div>
  </section>

  <section id="thresholds">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Profile</label><input id="thresholdProfileName" value="ProductionDefault"></div>
        <div class="field"><label>Match threshold</label><input id="matchThreshold" type="number" value="92"></div>
        <div class="field"><label>Review threshold</label><input id="reviewThreshold" type="number" value="80"></div>
        <div class="field"><label>Status</label><select id="thresholdActive"><option value="true">Active</option><option value="false">Inactive</option></select></div>
        <div><button id="saveThreshold" class="primary">Save</button></div>
      </div>
    </div>
    <div id="thresholdsTable"></div>
  </section>

  <section id="exports">
    <div class="band">
      <div class="formrow">
        <div class="field"><label>Package type</label><input id="packageType" value="Governance"></div>
        <div class="field"><label>Storage URI</label><input id="exportStorageUri" placeholder="optional path or URI"></div>
        <div class="field"><label>Audit</label><select id="includeAudit"><option value="true">Include</option><option value="false">Skip</option></select></div>
        <div class="field"><label>References</label><select id="includeReferences"><option value="true">Include</option><option value="false">Skip</option></select></div>
        <div class="field"><label>Templates</label><select id="includeTemplates"><option value="true">Include</option><option value="false">Skip</option></select></div>
        <div><button id="requestExport" class="primary">Request export</button></div>
      </div>
    </div>
    <div id="exportsTable"></div>
  </section>

  <section id="service">
    <div id="serviceStatus"></div>
  </section>

  <section id="audit">
    <div class="toolbar"><input class="filter" data-filter="auditTable" placeholder="Filter"></div>
    <div id="auditTable"></div>
  </section>

  <div id="message" class="message"></div>
</main>
<script>
const FIXED_ROLES=["Administrator","Auditor","Reviewer","ReferenceApprover","Verifier"];
const DEFAULT_API_KEY_ROLES=["Administrator","Auditor","Reviewer","ReferenceApprover","Verifier"];
const state={tab:"overview",key:sessionStorage.getItem("ssvApiKey")||""};
const qs=s=>document.querySelector(s);
const qsa=s=>Array.from(document.querySelectorAll(s));
initRoleControls();
qs("#apiKey").value=state.key;
qs("#saveKey").onclick=()=>{state.key=qs("#apiKey").value.trim();sessionStorage.setItem("ssvApiKey",state.key);msg("Key saved for this browser session.");loadAll();};
qs("#refresh").onclick=()=>loadAll();
qsa(".tab").forEach(b=>b.onclick=()=>{qsa(".tab").forEach(x=>x.classList.remove("active"));qsa("section").forEach(x=>x.classList.remove("active"));b.classList.add("active");qs("#"+b.dataset.tab).classList.add("active");state.tab=b.dataset.tab;loadTab();});
qsa(".filter").forEach(i=>i.oninput=()=>filterTable(i.dataset.filter,i.value));
qs("#saveStorageMode").onclick=saveStorageMode;
qs("#savePurgeSettings").onclick=savePurgeSettings;
qs("#saveRetention").onclick=saveRetention;
qs("#policyName").onchange=syncRetentionForm;
qs("#createApiKey").onclick=createApiKey;
qs("#grantRole").onclick=grantRole;
qs("#saveTemplate").onclick=saveTemplate;
qs("#saveZone").onclick=saveZone;
qs("#saveThreshold").onclick=saveThreshold;
qs("#requestExport").onclick=requestExport;
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
function initRoleControls(){
  qs("#apiKeyRoles").innerHTML=FIXED_ROLES.map(role=>`<label class="role-option"><input type="checkbox" value="${esc(role)}" ${DEFAULT_API_KEY_ROLES.includes(role)?"checked":""}><span>${esc(role)}</span></label>`).join("");
  qs("#roleName").innerHTML=FIXED_ROLES.map(role=>`<option value="${esc(role)}" ${role==="Reviewer"?"selected":""}>${esc(role)}</option>`).join("");
}
function selectedApiRoles(){return qsa("#apiKeyRoles input:checked").map(input=>input.value);}
function syncRetentionForm(){
  const selected=state.retentionPolicies?.find(row=>row.PolicyName===qs("#policyName").value);
  if(!selected)return;
  qs("#targetObjectType").value=selected.TargetObjectType||"";
  qs("#retentionDays").value=selected.RetentionDays||"";
  qs("#retentionActive").value=String(Boolean(selected.IsActive)).toLowerCase();
}
async function loadAll(){try{await loadOverview();await loadSettings();msg("Loaded.","ok");}catch(e){msg(e.message,"bad");}}
async function loadTab(){try{if(state.tab==="overview")await loadOverview();if(state.tab==="settings")await loadSettings();if(state.tab==="references")await loadReferences();if(state.tab==="cases")await loadCases();if(state.tab==="retention")await loadRetention();if(state.tab==="keys")await loadApiKeys();if(state.tab==="users")await loadUsers();if(state.tab==="templates")await loadTemplates();if(state.tab==="thresholds")await loadThresholds();if(state.tab==="exports")await loadExports();if(state.tab==="service")await loadService();if(state.tab==="audit")await loadAudit();}catch(e){msg(e.message,"bad");}}
async function loadOverview(){
  const summary=await api("/api/v1/admin/summary");
  const readiness=await api("/api/v1/readiness");
  qs("#storageMode").value=summary.storageMode||"Database";
  const counts=summary.counts||{};
  qs("#metrics").innerHTML=[metric("Storage mode",summary.storageMode),...Object.keys(counts).map(k=>metric(k,counts[k]))].join("");
  qs("#readiness").innerHTML=table(readiness.map(x=>({Name:x.name,Status:x.status,Message:x.message})),"readinessTable");
  qs("#latest").innerHTML=table(summary.latest||[],"latestTable");
}
async function loadSettings(){const rows=await api("/api/v1/admin/settings");const settings=Object.fromEntries((rows||[]).map(r=>[r.SettingKey,r.SettingValue]));if(settings.FilePurgeEnabled)qs("#filePurgeEnabled").value=String(settings.FilePurgeEnabled).toLowerCase();if(settings.DatabaseBlobPurgeEnabled)qs("#databaseBlobPurgeEnabled").value=String(settings.DatabaseBlobPurgeEnabled).toLowerCase();qs("#settingsTable").innerHTML=table(rows,"settingsRows");}
async function loadReferences(){const rows=await api("/api/v1/admin/references");qs("#referencesTable").innerHTML=table(rows,"referencesTable");}
async function loadCases(){const rows=await api("/api/v1/admin/cases");qs("#casesTable").innerHTML=table(rows,"casesTable");}
async function loadRetention(){
  const rows=await api("/api/v1/admin/retention");
  state.retentionPolicies=rows||[];
  const policy=qs("#policyName");
  const current=policy.value;
  policy.innerHTML=state.retentionPolicies.map(row=>`<option value="${esc(row.PolicyName)}">${esc(row.PolicyName)}</option>`).join("");
  if(current&&state.retentionPolicies.some(row=>row.PolicyName===current))policy.value=current;
  syncRetentionForm();
  qs("#retentionTable").innerHTML=table(rows,"retentionRows");
}
async function loadApiKeys(){const rows=await api("/api/v1/admin/api-keys");qs("#apiKeysTable").innerHTML=table(rows,"apiKeysRows");}
async function loadUsers(){const roles=await api("/api/v1/admin/roles");const users=await api("/api/v1/admin/users");qs("#rolesTable").innerHTML="<h2>Roles</h2>"+table(roles,"rolesRows");qs("#usersTable").innerHTML="<h2>Assignments</h2>"+table(users,"usersRows");}
async function loadTemplates(){const rows=await api("/api/v1/admin/templates");qs("#templatesTable").innerHTML=table(rows,"templatesRows");const id=Number(qs("#zoneTemplateId").value);if(id>0){const zones=await api(`/api/v1/admin/templates/${id}/zones`);qs("#zonesTable").innerHTML=table(zones,"zonesRows");}}
async function loadThresholds(){const rows=await api("/api/v1/admin/threshold-profiles");qs("#thresholdsTable").innerHTML=table(rows,"thresholdsRows");}
async function loadExports(){const rows=await api("/api/v1/admin/export-packages");qs("#exportsTable").innerHTML=table(rows,"exportsRows");}
async function loadService(){const status=await api("/api/v1/admin/service-status");qs("#serviceStatus").innerHTML=table([status],"serviceRows")+table([status.database],"serviceDbRows")+table([status.storage],"serviceStorageRows");}
async function loadAudit(){const rows=await api("/api/v1/admin/audit");qs("#auditTable").innerHTML=table(rows,"auditTable");}
async function saveStorageMode(){
  try{const storageMode=qs("#storageMode").value;await api("/api/v1/admin/settings/storage-mode",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify({storageMode})});msg("Storage mode saved.","ok");await loadOverview();await loadSettings();}catch(e){msg(e.message,"bad");}
}
async function savePurgeSettings(){
  try{const payload={filePurgeEnabled:qs("#filePurgeEnabled").value==="true",databaseBlobPurgeEnabled:qs("#databaseBlobPurgeEnabled").value==="true"};await api("/api/v1/admin/settings/purge",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Purge settings saved.","ok");await loadSettings();}catch(e){msg(e.message,"bad");}
}
async function saveRetention(){
  try{const selected=state.retentionPolicies?.find(row=>row.PolicyName===qs("#policyName").value);if(!selected)throw new Error("Select a retention policy.");const payload={policyName:selected.PolicyName,targetObjectType:selected.TargetObjectType,retentionDays:Number(qs("#retentionDays").value),isActive:qs("#retentionActive").value==="true"};await api("/api/v1/admin/retention",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Retention policy saved.","ok");await loadRetention();}catch(e){msg(e.message,"bad");}
}
async function createApiKey(){
  try{const payload={keyName:qs("#apiKeyName").value.trim(),roles:selectedApiRoles(),expiresUtc:qs("#apiKeyExpires").value.trim()||null,notes:qs("#apiKeyNotes").value.trim()||null};const result=await api("/api/v1/admin/api-keys",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});qs("#createdApiKey").textContent=`Key name: ${result.keyName}\nSecret key: ${result.apiKey}\nRoles: ${result.rolesCsv}\nStore the secret key now; it will not be shown again.`;msg("API key created.","ok");await loadApiKeys();}catch(e){msg(e.message,"bad");}
}
async function grantRole(){
  try{const payload={principalName:qs("#principalName").value.trim(),roleName:qs("#roleName").value.trim()};await api("/api/v1/admin/users/roles",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Role granted.","ok");await loadUsers();}catch(e){msg(e.message,"bad");}
}
async function saveTemplate(){
  try{const rawProfile=qs("#templateThresholdProfileId").value.trim();const payload={templateKey:qs("#templateKey").value.trim(),documentType:qs("#documentType").value.trim(),templateName:qs("#templateName").value.trim(),versionNumber:Number(qs("#templateVersion").value),status:qs("#templateStatus").value,thresholdProfileId:rawProfile?Number(rawProfile):null,templateJson:null};const result=await api("/api/v1/admin/templates",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});qs("#zoneTemplateId").value=result.formTemplateId;msg("Template saved.","ok");await loadTemplates();}catch(e){msg(e.message,"bad");}
}
async function saveZone(){
  try{const id=Number(qs("#zoneTemplateId").value);const payload={signatureId:qs("#zoneSignatureId").value.trim(),displayName:null,pageIndex:Number(qs("#zonePageIndex").value),x:Number(qs("#zoneX").value),y:Number(qs("#zoneY").value),width:Number(qs("#zoneWidth").value),height:Number(qs("#zoneHeight").value),coordinateSystem:"PdfPoints",expectedLabel:null,isRequired:true,zoneJson:null};await api(`/api/v1/admin/templates/${id}/zones`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Zone saved.","ok");await loadTemplates();}catch(e){msg(e.message,"bad");}
}
async function saveThreshold(){
  try{const payload={profileName:qs("#thresholdProfileName").value.trim(),matchThreshold:Number(qs("#matchThreshold").value),reviewThreshold:Number(qs("#reviewThreshold").value),isActive:qs("#thresholdActive").value==="true",profileJson:null};await api("/api/v1/admin/threshold-profiles",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Threshold profile saved.","ok");await loadThresholds();}catch(e){msg(e.message,"bad");}
}
async function requestExport(){
  try{const payload={packageType:qs("#packageType").value.trim(),storageUri:qs("#exportStorageUri").value.trim()||null,includeAudit:qs("#includeAudit").value==="true",includeReferences:qs("#includeReferences").value==="true",includeTemplates:qs("#includeTemplates").value==="true"};await api("/api/v1/admin/export-packages",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload)});msg("Export requested.","ok");await loadExports();}catch(e){msg(e.message,"bad");}
}
loadAll();
</script>
</body>
</html>
""";
}
