using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

namespace StaticSignatureVerification.Storage;

public sealed class SqlProductionWorkflowStore : IProductionWorkflowStore
{
    private readonly string _connectionString;

    public SqlProductionWorkflowStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task<long> EnrollReferenceAsync(ReferenceEnrollmentRecord enrollment, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long? subjectId = null;
            if (!string.IsNullOrWhiteSpace(enrollment.ExternalPartyId))
            {
                subjectId = Convert.ToInt64(await ScalarAsync(connection, transaction, """
MERGE ssv.ReferenceSubject AS target
USING (SELECT @ExternalPartyId AS ExternalPartyId) AS source
ON target.ExternalPartyId = source.ExternalPartyId
WHEN MATCHED THEN UPDATE SET PartyName = COALESCE(@PartyName, target.PartyName), UpdatedBy = @Actor, UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT(ExternalPartyId, PartyName, CreatedBy, UpdatedBy)
VALUES(@ExternalPartyId, @PartyName, @Actor, @Actor);
SELECT ReferenceSubjectId FROM ssv.ReferenceSubject WHERE ExternalPartyId = @ExternalPartyId;
""", command =>
                {
                    AddString(command, "@ExternalPartyId", enrollment.ExternalPartyId);
                    AddString(command, "@PartyName", enrollment.PartyName);
                    AddString(command, "@Actor", enrollment.Actor);
                }, cancellationToken).ConfigureAwait(false));
            }

            var referenceSetRegistryId = Convert.ToInt64(await ScalarAsync(connection, transaction, """
MERGE ssv.ReferenceSetRegistry AS target
USING (SELECT @ReferenceSetId AS ReferenceSetId) AS source
ON target.ReferenceSetId = source.ReferenceSetId
WHEN MATCHED THEN UPDATE SET
    ReferenceSubjectId = COALESCE(@ReferenceSubjectId, target.ReferenceSubjectId),
    SignatureId = @SignatureId,
    DisplayName = COALESCE(@DisplayName, target.DisplayName),
    LanguageCode = COALESCE(@LanguageCode, target.LanguageCode),
    Status = CASE WHEN target.Status = N'Approved' THEN target.Status ELSE N'PendingApproval' END,
    QualityStatus = @QualityStatus,
    QualityScore = @QualityScore,
    SubmittedBy = @Actor,
    SubmittedUtc = sysutcdatetime(),
    UpdatedBy = @Actor,
    UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT
    (ReferenceSetId, ReferenceSubjectId, SignatureId, DisplayName, LanguageCode, Status, QualityStatus, QualityScore, CreatedBy, SubmittedBy, SubmittedUtc)
VALUES
    (@ReferenceSetId, @ReferenceSubjectId, @SignatureId, @DisplayName, @LanguageCode, N'PendingApproval', @QualityStatus, @QualityScore, @Actor, @Actor, sysutcdatetime());
SELECT ReferenceSetRegistryId FROM ssv.ReferenceSetRegistry WHERE ReferenceSetId = @ReferenceSetId;
""", command =>
            {
                AddString(command, "@ReferenceSetId", enrollment.ReferenceSetId);
                AddLong(command, "@ReferenceSubjectId", subjectId);
                AddString(command, "@SignatureId", enrollment.SignatureId);
                AddString(command, "@DisplayName", enrollment.DisplayName);
                AddString(command, "@LanguageCode", enrollment.LanguageCode);
                AddString(command, "@QualityStatus", enrollment.QualityStatus);
                AddDecimal(command, "@QualityScore", enrollment.QualityScore);
                AddString(command, "@Actor", enrollment.Actor);
            }, cancellationToken).ConfigureAwait(false));

            var versionNumber = Convert.ToInt32(await ScalarAsync(connection, transaction,
                "SELECT ISNULL(MAX(VersionNumber), 0) + 1 FROM ssv.ReferenceImageRegistry WHERE ReferenceSetRegistryId = @ReferenceSetRegistryId AND ReferenceId = @ReferenceId;",
                command =>
                {
                    AddLong(command, "@ReferenceSetRegistryId", referenceSetRegistryId);
                    AddString(command, "@ReferenceId", enrollment.ReferenceId);
                }, cancellationToken).ConfigureAwait(false));

            var imageId = Convert.ToInt64(await ScalarAsync(connection, transaction, """
INSERT INTO ssv.ReferenceImageRegistry
    (ReferenceSetRegistryId, ReferenceId, VersionNumber, SourceFileName, SourceFilePath, StorageUri, Sha256Hash,
     ImageWidth, ImageHeight, QualityStatus, QualityScore, EnrollmentStatus, IsApprovedForMatching, CreatedBy, EnrollmentJson)
VALUES
    (@ReferenceSetRegistryId, @ReferenceId, @VersionNumber, @SourceFileName, @SourceFilePath, @StorageUri, @Sha256Hash,
     @ImageWidth, @ImageHeight, @QualityStatus, @QualityScore, @EnrollmentStatus, 0, @Actor, @EnrollmentJson);
SELECT CONVERT(bigint, SCOPE_IDENTITY());
""", command =>
            {
                AddLong(command, "@ReferenceSetRegistryId", referenceSetRegistryId);
                AddString(command, "@ReferenceId", enrollment.ReferenceId);
                AddInt(command, "@VersionNumber", versionNumber);
                AddString(command, "@SourceFileName", enrollment.SourceFileName);
                AddString(command, "@SourceFilePath", enrollment.SourceFilePath);
                AddString(command, "@StorageUri", enrollment.StorageUri);
                AddString(command, "@Sha256Hash", enrollment.Sha256Hash);
                AddInt(command, "@ImageWidth", enrollment.ImageWidth);
                AddInt(command, "@ImageHeight", enrollment.ImageHeight);
                AddString(command, "@QualityStatus", enrollment.QualityStatus);
                AddDecimal(command, "@QualityScore", enrollment.QualityScore);
                AddString(command, "@EnrollmentStatus", enrollment.PassedQualityGate ? "PendingApproval" : "Rejected");
                AddString(command, "@Actor", enrollment.Actor);
                AddString(command, "@EnrollmentJson", enrollment.EnrollmentJson);
            }, cancellationToken).ConfigureAwait(false));

            await InsertReferenceEventAsync(connection, transaction, "ReferenceImage", imageId, enrollment.PassedQualityGate ? "Submitted" : "Rejected", enrollment.Actor, enrollment.QualityStatus, "Reference enrollment quality gate completed.", null, enrollment.EnrollmentJson, cancellationToken).ConfigureAwait(false);
            await InsertAuditEventAsync(connection, transaction, new AuditEventRecord("ReferenceEnrollment", "Information", null, enrollment.Actor, "ReferenceImage", imageId.ToString(), "Reference image enrolled.", enrollment.EnrollmentJson), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return imageId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateReferenceStatusAsync(ReferenceStatusUpdateRecord update, CancellationToken cancellationToken = default)
    {
        var normalized = update.Action.Trim().ToLowerInvariant();
        var imageStatus = normalized switch
        {
            "approve" => "Approved",
            "reject" => "Rejected",
            "retire" => "Retired",
            _ => throw new InvalidOperationException("Reference action must be approve, reject, or retire.")
        };
        var setStatus = imageStatus;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var imageId = Convert.ToInt64(await ScalarAsync(connection, transaction, """
SELECT TOP (1) ReferenceImageRegistryId
FROM ssv.ReferenceImageRegistry
WHERE ReferenceId = @ReferenceId
ORDER BY VersionNumber DESC, ReferenceImageRegistryId DESC;
""", command => AddString(command, "@ReferenceId", update.ReferenceId), cancellationToken).ConfigureAwait(false) ?? 0);
            if (imageId == 0)
            {
                throw new InvalidOperationException($"Reference was not found: {update.ReferenceId}");
            }

            await NonQueryAsync(connection, transaction, """
UPDATE ssv.ReferenceImageRegistry
SET EnrollmentStatus = @ImageStatus,
    IsApprovedForMatching = CASE WHEN @ImageStatus = N'Approved' THEN 1 ELSE 0 END,
    EffectiveFromUtc = CASE WHEN @ImageStatus = N'Approved' AND EffectiveFromUtc IS NULL THEN sysutcdatetime() ELSE EffectiveFromUtc END,
    EffectiveToUtc = CASE WHEN @ImageStatus = N'Retired' THEN sysutcdatetime() ELSE EffectiveToUtc END,
    ApprovedBy = CASE WHEN @ImageStatus = N'Approved' THEN @Actor ELSE ApprovedBy END,
    ApprovedUtc = CASE WHEN @ImageStatus = N'Approved' THEN sysutcdatetime() ELSE ApprovedUtc END,
    RetiredBy = CASE WHEN @ImageStatus = N'Retired' THEN @Actor ELSE RetiredBy END,
    RetiredUtc = CASE WHEN @ImageStatus = N'Retired' THEN sysutcdatetime() ELSE RetiredUtc END
WHERE ReferenceImageRegistryId = @ImageId;

UPDATE s
SET Status = @SetStatus,
    ApprovedBy = CASE WHEN @SetStatus = N'Approved' THEN @Actor ELSE s.ApprovedBy END,
    ApprovedUtc = CASE WHEN @SetStatus = N'Approved' THEN sysutcdatetime() ELSE s.ApprovedUtc END,
    RetiredBy = CASE WHEN @SetStatus = N'Retired' THEN @Actor ELSE s.RetiredBy END,
    RetiredUtc = CASE WHEN @SetStatus = N'Retired' THEN sysutcdatetime() ELSE s.RetiredUtc END,
    RetirementReason = CASE WHEN @SetStatus = N'Retired' THEN @Notes ELSE s.RetirementReason END
FROM ssv.ReferenceSetRegistry s
JOIN ssv.ReferenceImageRegistry i ON i.ReferenceSetRegistryId = s.ReferenceSetRegistryId
WHERE i.ReferenceImageRegistryId = @ImageId;
""", command =>
            {
                AddString(command, "@ImageStatus", imageStatus);
                AddString(command, "@SetStatus", setStatus);
                AddLong(command, "@ImageId", imageId);
                AddString(command, "@Actor", update.Actor);
                AddString(command, "@Notes", update.Notes);
            }, cancellationToken).ConfigureAwait(false);

            await InsertReferenceEventAsync(connection, transaction, "ReferenceImage", imageId, imageStatus, update.Actor, update.ReasonCode, update.Notes, null, null, cancellationToken).ConfigureAwait(false);
            await InsertAuditEventAsync(connection, transaction, new AuditEventRecord("ReferenceStatusChanged", "Information", null, update.Actor, "ReferenceImage", imageId.ToString(), $"Reference {imageStatus}.", null), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<DuplicateReferenceAlertRecord>> ScanDuplicateReferenceAlertsAsync(double threshold, CancellationToken cancellationToken = default)
    {
        var references = new List<(long ImageId, string ReferenceId, long SetId, string? Hash, string? AverageHash)>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT i.ReferenceImageRegistryId, i.ReferenceId, i.ReferenceSetRegistryId, i.Sha256Hash, i.EnrollmentJson
FROM ssv.ReferenceImageRegistry i
WHERE i.EnrollmentStatus IN (N'PendingApproval', N'Approved') AND i.IsApprovedForMatching IN (0, 1);
""";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = NullableString(reader, 4);
                references.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), NullableString(reader, 3), ExtractAverageHash(json)));
            }
        }

        var alerts = new List<DuplicateReferenceAlertRecord>();
        for (var i = 0; i < references.Count; i++)
        {
            for (var j = i + 1; j < references.Count; j++)
            {
                if (references[i].SetId == references[j].SetId)
                {
                    continue;
                }

                var score = Similarity(references[i].Hash, references[j].Hash, references[i].AverageHash, references[j].AverageHash);
                if (score < threshold)
                {
                    continue;
                }

                var alertType = references[i].Hash is { Length: > 0 } &&
                                references[i].Hash == references[j].Hash
                    ? "ExactDuplicateHash"
                    : "VisualSimilarity";
                var alertId = Convert.ToInt64(await ScalarAsync(connection, null, """
IF NOT EXISTS (
    SELECT 1 FROM ssv.ReferenceSimilarityAlert
    WHERE ((FirstReferenceImageRegistryId = @FirstId AND SecondReferenceImageRegistryId = @SecondId)
        OR (FirstReferenceImageRegistryId = @SecondId AND SecondReferenceImageRegistryId = @FirstId))
      AND AlertStatus = N'Open')
BEGIN
    INSERT INTO ssv.ReferenceSimilarityAlert
        (FirstReferenceImageRegistryId, SecondReferenceImageRegistryId, SimilarityScore, AlertType)
    VALUES
        (@FirstId, @SecondId, @Score, @AlertType);
    SELECT CONVERT(bigint, SCOPE_IDENTITY());
END
ELSE
BEGIN
    SELECT TOP (1) ReferenceSimilarityAlertId FROM ssv.ReferenceSimilarityAlert
    WHERE ((FirstReferenceImageRegistryId = @FirstId AND SecondReferenceImageRegistryId = @SecondId)
        OR (FirstReferenceImageRegistryId = @SecondId AND SecondReferenceImageRegistryId = @FirstId))
      AND AlertStatus = N'Open';
END
""", command =>
                {
                    AddLong(command, "@FirstId", references[i].ImageId);
                    AddLong(command, "@SecondId", references[j].ImageId);
                    AddDecimal(command, "@Score", score);
                    AddString(command, "@AlertType", alertType);
                }, cancellationToken).ConfigureAwait(false));

                alerts.Add(new DuplicateReferenceAlertRecord(alertId, references[i].ReferenceId, references[j].ReferenceId, score, alertType, "Open"));
            }
        }

        await LogAuditEventAsync(new AuditEventRecord("DuplicateReferenceScan", "Information", null, null, "ReferenceSimilarityAlert", null, $"Duplicate scan completed. Alerts: {alerts.Count}.", $$"""{"threshold":{{threshold}},"alerts":{{alerts.Count}}}"""), cancellationToken).ConfigureAwait(false);
        return alerts;
    }

    public async Task<long> CreateReviewCaseAsync(ReviewCaseRecord reviewCase, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var signatureCaseId = await GetSignatureCaseIdAsync(connection, reviewCase.DocumentResultId, reviewCase.SignatureId, cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(await ScalarAsync(connection, null, """
MERGE ssv.ReviewCase AS target
USING (SELECT @CaseNumber AS CaseNumber) AS source
ON target.CaseNumber = source.CaseNumber
WHEN MATCHED THEN UPDATE SET
    CaseStatus = N'Open',
    Priority = @Priority,
    AssignedTo = @AssignedTo,
    DueUtc = @DueUtc,
    Branch = @Branch,
    CustomerId = @CustomerId,
    DocumentType = @DocumentType,
    UpdatedUtc = sysutcdatetime(),
    CaseJson = @CaseJson
WHEN NOT MATCHED THEN INSERT
    (CaseNumber, DocumentResultId, SignatureCaseId, CaseStatus, Priority, AssignedTo, AssignedUtc, DueUtc, Branch, CustomerId, DocumentType, CaseJson)
VALUES
    (@CaseNumber, @DocumentResultId, @SignatureCaseId, N'Open', @Priority, @AssignedTo, CASE WHEN @AssignedTo IS NULL THEN NULL ELSE sysutcdatetime() END, @DueUtc, @Branch, @CustomerId, @DocumentType, @CaseJson);
SELECT ReviewCaseId FROM ssv.ReviewCase WHERE CaseNumber = @CaseNumber;
""", command =>
        {
            AddString(command, "@CaseNumber", reviewCase.CaseNumber);
            AddString(command, "@DocumentResultId", reviewCase.DocumentResultId);
            AddLong(command, "@SignatureCaseId", signatureCaseId);
            AddString(command, "@Priority", reviewCase.Priority);
            AddString(command, "@AssignedTo", reviewCase.AssignedTo);
            AddDate(command, "@DueUtc", reviewCase.DueUtc);
            AddString(command, "@Branch", reviewCase.Branch);
            AddString(command, "@CustomerId", reviewCase.CustomerId);
            AddString(command, "@DocumentType", reviewCase.DocumentType);
            AddString(command, "@CaseJson", reviewCase.CaseJson);
        }, cancellationToken).ConfigureAwait(false));

        await AddReviewCaseEventAsync(connection, id, "Created", reviewCase.AssignedTo, "Review case created or updated.", reviewCase.CaseJson, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateReviewCaseAsync(ReviewCaseUpdateRecord update, CancellationToken cancellationToken = default)
    {
        var status = update.Action.Trim().ToLowerInvariant() switch
        {
            "assign" => "Open",
            "complete" => "Completed",
            "escalate" => "Escalated",
            "cancel" => "Cancelled",
            _ => throw new InvalidOperationException("Review case action must be assign, complete, escalate, or cancel.")
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = Convert.ToInt64(await ScalarAsync(connection, null, """
UPDATE ssv.ReviewCase
SET CaseStatus = @Status,
    AssignedTo = COALESCE(@AssignedTo, AssignedTo),
    AssignedBy = CASE WHEN @AssignedTo IS NULL THEN AssignedBy ELSE @Actor END,
    AssignedUtc = CASE WHEN @AssignedTo IS NULL THEN AssignedUtc ELSE sysutcdatetime() END,
    CompletedUtc = CASE WHEN @Status = N'Completed' THEN sysutcdatetime() ELSE CompletedUtc END,
    EscalatedUtc = CASE WHEN @Status = N'Escalated' THEN sysutcdatetime() ELSE EscalatedUtc END,
    UpdatedUtc = sysutcdatetime()
WHERE CaseNumber = @CaseNumber;
SELECT ReviewCaseId FROM ssv.ReviewCase WHERE CaseNumber = @CaseNumber;
""", command =>
        {
            AddString(command, "@Status", status);
            AddString(command, "@AssignedTo", update.AssignedTo);
            AddString(command, "@Actor", update.Actor);
            AddString(command, "@CaseNumber", update.CaseNumber);
        }, cancellationToken).ConfigureAwait(false));
        if (id == 0)
        {
            throw new InvalidOperationException($"Review case was not found: {update.CaseNumber}");
        }

        await AddReviewCaseEventAsync(connection, id, status, update.Actor, update.Notes, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogAuditEventAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await InsertAuditEventAsync(connection, null, auditEvent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpsertRetentionPolicyAsync(RetentionPolicyRecord policy, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(await ScalarAsync(connection, null, """
MERGE ssv.RetentionPolicy AS target
USING (SELECT @PolicyName AS PolicyName) AS source
ON target.PolicyName = source.PolicyName
WHEN MATCHED THEN UPDATE SET TargetObjectType = @TargetObjectType, RetentionDays = @RetentionDays, IsActive = @IsActive, PolicyJson = @PolicyJson
WHEN NOT MATCHED THEN INSERT(PolicyName, TargetObjectType, RetentionDays, IsActive, CreatedBy, PolicyJson)
VALUES(@PolicyName, @TargetObjectType, @RetentionDays, @IsActive, @CreatedBy, @PolicyJson);
SELECT RetentionPolicyId FROM ssv.RetentionPolicy WHERE PolicyName = @PolicyName;
""", command =>
        {
            AddString(command, "@PolicyName", policy.PolicyName);
            AddString(command, "@TargetObjectType", policy.TargetObjectType);
            AddInt(command, "@RetentionDays", policy.RetentionDays);
            AddBool(command, "@IsActive", policy.IsActive);
            AddString(command, "@CreatedBy", policy.CreatedBy);
            AddString(command, "@PolicyJson", policy.PolicyJson);
        }, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PurgeRunResultRecord> RunRetentionPurgeAsync(DateTimeOffset nowUtc, string actor, bool deleteFiles, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var purgeRunId = Convert.ToInt64(await ScalarAsync(connection, null, """
INSERT INTO ssv.PurgeRun(Actor) VALUES(@Actor);
SELECT CONVERT(bigint, SCOPE_IDENTITY());
""", command => AddString(command, "@Actor", actor), cancellationToken).ConfigureAwait(false));

        var policies = await LoadRetentionPoliciesAsync(connection, cancellationToken).ConfigureAwait(false);
        var candidates = new List<PurgeCandidate>();
        await AddStorageObjectCandidatesAsync(connection, candidates, nowUtc, policies, cancellationToken).ConfigureAwait(false);
        await AddVerificationDocumentCandidatesAsync(connection, candidates, nowUtc, policies, cancellationToken).ConfigureAwait(false);
        if (await GetSystemSettingAsync(connection, "DatabaseBlobPurgeEnabled", cancellationToken).ConfigureAwait(false) != "false")
        {
            await AddDbBlobCandidatesAsync(connection, candidates, "DocumentBlob", "ssv.DocumentBlob", "DocumentBlobId", "DocumentResultId", nowUtc, policies, cancellationToken).ConfigureAwait(false);
            await AddDbBlobCandidatesAsync(connection, candidates, "DebugArtifactBlob", "ssv.DebugArtifactBlob", "DebugArtifactBlobId", "DocumentResultId", nowUtc, policies, cancellationToken).ConfigureAwait(false);
            await AddDbBlobCandidatesAsync(connection, candidates, "ReportBlob", "ssv.ReportBlob", "ReportBlobId", "ReportName", nowUtc, policies, cancellationToken).ConfigureAwait(false);
        }

        var purged = 0;
        string? error = null;
        foreach (var candidate in candidates)
        {
            string status;
            string message;
            try
            {
                if (dryRun)
                {
                    // Dry run must not mutate anything - neither the filesystem nor the database -
                    // so operators can safely preview a purge before committing to it.
                    status = "WouldPurge";
                    message = candidate.Source switch
                    {
                        "VerificationDocument" => "Dry run: document and cascaded case/audit metadata would be purged.",
                        _ when candidate.IsFilesystem => deleteFiles
                            ? "Dry run: file and metadata would be purged."
                            : "Dry run: metadata would be purged (file deletion disabled).",
                        _ => "Dry run: database blob would be purged."
                    };
                }
                else
                {
                    status = "Purged";
                    message = candidate.Source switch
                    {
                        "VerificationDocument" => "Document and cascaded case/audit metadata purged.",
                        _ when candidate.IsFilesystem => "Metadata purged.",
                        _ => "Database blob purged."
                    };
                    if (candidate.IsFilesystem && deleteFiles && !string.IsNullOrWhiteSpace(candidate.Uri) && File.Exists(candidate.Uri))
                    {
                        File.Delete(candidate.Uri);
                        message = "File and metadata purged.";
                    }

                    await NonQueryAsync(connection, null, candidate.DeleteSql, command =>
                    {
                        if (candidate.StringKey is not null)
                        {
                            AddString(command, "@Id", candidate.StringKey);
                        }
                        else
                        {
                            AddLong(command, "@Id", candidate.Id);
                        }
                    }, cancellationToken).ConfigureAwait(false);
                    purged++;
                }
            }
            catch (Exception ex)
            {
                status = "Failed";
                message = ex.Message;
                error ??= ex.Message;
            }

            await NonQueryAsync(connection, null, """
INSERT INTO ssv.PurgeRunItem(PurgeRunId, StorageObjectId, EntityType, EntityId, Action, Status, Message)
VALUES(@PurgeRunId, @StorageObjectId, @EntityType, @EntityId, N'Purge', @Status, @Message);
""", command =>
            {
                AddLong(command, "@PurgeRunId", purgeRunId);
                AddLong(command, "@StorageObjectId", candidate.Source == "StorageObject" ? candidate.Id : null);
                AddString(command, "@EntityType", candidate.EntityType);
                AddString(command, "@EntityId", candidate.EntityId);
                AddString(command, "@Status", status);
                AddString(command, "@Message", message);
            }, cancellationToken).ConfigureAwait(false);
        }

        var runStatus = dryRun
            ? (error is null ? "CompletedDryRun" : "CompletedDryRunWithErrors")
            : (error is null ? "Completed" : "CompletedWithErrors");
        await NonQueryAsync(connection, null, "UPDATE ssv.PurgeRun SET FinishedUtc = sysutcdatetime(), Status = @Status, CandidateCount = @CandidateCount, PurgedCount = @PurgedCount, ErrorMessage = @ErrorMessage WHERE PurgeRunId = @PurgeRunId;", command =>
        {
            AddString(command, "@Status", runStatus);
            AddInt(command, "@CandidateCount", candidates.Count);
            AddInt(command, "@PurgedCount", purged);
            AddString(command, "@ErrorMessage", error);
            AddLong(command, "@PurgeRunId", purgeRunId);
        }, cancellationToken).ConfigureAwait(false);

        return new PurgeRunResultRecord(purgeRunId, candidates.Count, purged, runStatus, error);
    }

    public async Task<long> RegisterStorageObjectAsync(string objectType, string? entityType, string? entityId, string storageUri, DateTimeOffset createdUtc, DateTimeOffset? retainUntilUtc, long? sizeBytes = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(await ScalarAsync(connection, null, """
INSERT INTO ssv.StorageObject(ObjectType, EntityType, EntityId, StorageUri, SizeBytes, CreatedUtc, RetainUntilUtc)
VALUES(@ObjectType, @EntityType, @EntityId, @StorageUri, @SizeBytes, @CreatedUtc, @RetainUntilUtc);
SELECT CONVERT(bigint, SCOPE_IDENTITY());
""", command =>
        {
            AddString(command, "@ObjectType", objectType);
            AddString(command, "@EntityType", entityType);
            AddString(command, "@EntityId", entityId);
            AddString(command, "@StorageUri", storageUri);
            AddLong(command, "@SizeBytes", sizeBytes);
            AddDate(command, "@CreatedUtc", createdUtc);
            AddDate(command, "@RetainUntilUtc", retainUntilUtc);
        }, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Dictionary<string, int>> LoadRetentionPoliciesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var policies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
SELECT TargetObjectType, MIN(RetentionDays)
FROM ssv.RetentionPolicy
WHERE IsActive = 1
GROUP BY TargetObjectType;
""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            policies[reader.GetString(0)] = reader.GetInt32(1);
        }

        return policies;
    }

    private static async Task<string?> GetSystemSettingAsync(SqlConnection connection, string settingKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = "SELECT SettingValue FROM ssv.SystemSetting WHERE SettingKey = @SettingKey;";
        AddString(command, "@SettingKey", settingKey);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task AddStorageObjectCandidatesAsync(SqlConnection connection, List<PurgeCandidate> candidates, DateTimeOffset nowUtc, IReadOnlyDictionary<string, int> policies, CancellationToken cancellationToken)
    {
        policies.TryGetValue("StorageObject", out var retentionDays);
        var cutoffUtc = retentionDays > 0 ? nowUtc.AddDays(-retentionDays) : (DateTimeOffset?)null;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
SELECT StorageObjectId, StorageUri, EntityType, EntityId
FROM ssv.StorageObject
WHERE (RetainUntilUtc IS NOT NULL AND RetainUntilUtc <= @NowUtc)
   OR (@CutoffUtc IS NOT NULL AND CreatedUtc <= @CutoffUtc);
""";
        AddDate(command, "@NowUtc", nowUtc);
        AddDate(command, "@CutoffUtc", cutoffUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PurgeCandidate(
                "StorageObject",
                reader.GetInt64(0),
                reader.GetString(1),
                NullableString(reader, 2) ?? "StorageObject",
                NullableString(reader, 3),
                true,
                "DELETE FROM ssv.StorageObject WHERE StorageObjectId = @Id;"));
        }
    }

    private static async Task AddVerificationDocumentCandidatesAsync(SqlConnection connection, List<PurgeCandidate> candidates, DateTimeOffset nowUtc, IReadOnlyDictionary<string, int> policies, CancellationToken cancellationToken)
    {
        // Case/document metadata (VerificationDocument, and everything that cascades from it:
        // SignatureCase, ReferenceComparison, DebugArtifact, ReviewerOutcome, ReviewCase,
        // ResultGovernanceSnapshot) is only purged when an administrator has explicitly activated
        // a "VerificationDocument" retention policy. There is no implicit fallback retention window
        // for this data, because it is the primary audit record and may be subject to compliance
        // retention obligations that differ per deployment.
        if (!policies.TryGetValue("VerificationDocument", out var retentionDays) || retentionDays <= 0)
        {
            return;
        }

        var cutoffUtc = nowUtc.AddDays(-retentionDays);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = "SELECT DocumentResultId FROM ssv.VerificationDocument WHERE CreatedUtc <= @CutoffUtc;";
        AddDate(command, "@CutoffUtc", cutoffUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var documentResultId = reader.GetString(0);
            candidates.Add(new PurgeCandidate(
                "VerificationDocument",
                0,
                null,
                "VerificationDocument",
                documentResultId,
                false,
                "DELETE FROM ssv.VerificationDocument WHERE DocumentResultId = @Id;",
                StringKey: documentResultId));
        }
    }

    private static async Task AddDbBlobCandidatesAsync(
        SqlConnection connection,
        List<PurgeCandidate> candidates,
        string targetObjectType,
        string tableName,
        string idColumn,
        string entityIdColumn,
        DateTimeOffset nowUtc,
        IReadOnlyDictionary<string, int> policies,
        CancellationToken cancellationToken)
    {
        policies.TryGetValue(targetObjectType, out var retentionDays);
        var cutoffUtc = retentionDays > 0 ? nowUtc.AddDays(-retentionDays) : (DateTimeOffset?)null;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
SELECT {idColumn}, CONVERT(nvarchar(128), {entityIdColumn}) AS EntityId
FROM {tableName}
WHERE (RetainUntilUtc IS NOT NULL AND RetainUntilUtc <= @NowUtc)
   OR (@CutoffUtc IS NOT NULL AND CreatedUtc <= @CutoffUtc);
""";
        AddDate(command, "@NowUtc", nowUtc);
        AddDate(command, "@CutoffUtc", cutoffUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new PurgeCandidate(
                targetObjectType,
                reader.GetInt64(0),
                null,
                targetObjectType,
                NullableString(reader, 1) ?? reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                false,
                $"DELETE FROM {tableName} WHERE {idColumn} = @Id;"));
        }
    }

    public async Task<long> CreateExportPackageAsync(ExportPackageRecord exportPackage, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(await ScalarAsync(connection, null, """
INSERT INTO ssv.ExportPackage(PackageType, PackageStatus, StorageUri, Sha256Hash, CreatedBy, CompletedUtc, ManifestJson)
VALUES(@PackageType, @PackageStatus, @StorageUri, @Sha256Hash, @CreatedBy, CASE WHEN @PackageStatus = N'Completed' THEN sysutcdatetime() ELSE NULL END, @ManifestJson);
SELECT CONVERT(bigint, SCOPE_IDENTITY());
""", command =>
        {
            AddString(command, "@PackageType", exportPackage.PackageType);
            AddString(command, "@PackageStatus", exportPackage.PackageStatus);
            AddString(command, "@StorageUri", exportPackage.StorageUri);
            AddString(command, "@Sha256Hash", exportPackage.Sha256Hash);
            AddString(command, "@CreatedBy", exportPackage.CreatedBy);
            AddString(command, "@ManifestJson", exportPackage.ManifestJson);
        }, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<long?> GetSignatureCaseIdAsync(SqlConnection connection, string? documentResultId, string? signatureId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentResultId) || string.IsNullOrWhiteSpace(signatureId))
        {
            return null;
        }

        var value = await ScalarAsync(connection, null, "SELECT SignatureCaseId FROM ssv.SignatureCase WHERE DocumentResultId = @DocumentResultId AND SignatureId = @SignatureId;", command =>
        {
            AddString(command, "@DocumentResultId", documentResultId);
            AddString(command, "@SignatureId", signatureId);
        }, cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task AddReviewCaseEventAsync(SqlConnection connection, long reviewCaseId, string eventType, string? actor, string? notes, string? eventJson, CancellationToken cancellationToken)
    {
        await NonQueryAsync(connection, null, "INSERT INTO ssv.ReviewCaseEvent(ReviewCaseId, EventType, Actor, Notes, EventJson) VALUES(@ReviewCaseId, @EventType, @Actor, @Notes, @EventJson);", command =>
        {
            AddLong(command, "@ReviewCaseId", reviewCaseId);
            AddString(command, "@EventType", eventType);
            AddString(command, "@Actor", actor);
            AddString(command, "@Notes", notes);
            AddString(command, "@EventJson", eventJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertReferenceEventAsync(SqlConnection connection, SqlTransaction? transaction, string entityType, long entityId, string action, string? actor, string? reasonCode, string? notes, string? beforeJson, string? afterJson, CancellationToken cancellationToken)
    {
        await NonQueryAsync(connection, transaction, "INSERT INTO ssv.ReferenceApprovalEvent(EntityType, EntityId, Action, Actor, ReasonCode, Notes, BeforeJson, AfterJson) VALUES(@EntityType, @EntityId, @Action, @Actor, @ReasonCode, @Notes, @BeforeJson, @AfterJson);", command =>
        {
            AddString(command, "@EntityType", entityType);
            AddLong(command, "@EntityId", entityId);
            AddString(command, "@Action", action);
            AddString(command, "@Actor", actor);
            AddString(command, "@ReasonCode", reasonCode);
            AddString(command, "@Notes", notes);
            AddString(command, "@BeforeJson", beforeJson);
            AddString(command, "@AfterJson", afterJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditEventAsync(SqlConnection connection, SqlTransaction? transaction, AuditEventRecord auditEvent, CancellationToken cancellationToken)
    {
        await NonQueryAsync(connection, transaction, "INSERT INTO ssv.AuditEvent(EventType, Severity, CorrelationId, Actor, EntityType, EntityId, Message, PropertiesJson) VALUES(@EventType, @Severity, @CorrelationId, @Actor, @EntityType, @EntityId, @Message, @PropertiesJson);", command =>
        {
            AddString(command, "@EventType", auditEvent.EventType);
            AddString(command, "@Severity", auditEvent.Severity);
            AddString(command, "@CorrelationId", auditEvent.CorrelationId);
            AddString(command, "@Actor", auditEvent.Actor);
            AddString(command, "@EntityType", auditEvent.EntityType);
            AddString(command, "@EntityId", auditEvent.EntityId);
            AddString(command, "@Message", auditEvent.Message);
            AddString(command, "@PropertiesJson", auditEvent.PropertiesJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, SqlTransaction? transaction, string sql, Action<SqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task NonQueryAsync(SqlConnection connection, SqlTransaction? transaction, string sql, Action<SqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractAverageHash(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("averageHash", out var hash) ? hash.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static double Similarity(string? firstHash, string? secondHash, string? firstAverageHash, string? secondAverageHash)
    {
        if (!string.IsNullOrWhiteSpace(firstHash) && firstHash == secondHash)
        {
            return 100;
        }

        if (string.IsNullOrWhiteSpace(firstAverageHash) ||
            string.IsNullOrWhiteSpace(secondAverageHash) ||
            firstAverageHash.Length != secondAverageHash.Length)
        {
            return 0;
        }

        var matches = firstAverageHash.Zip(secondAverageHash).Count(pair => pair.First == pair.Second);
        return matches * 100.0 / firstAverageHash.Length;
    }

    private static void AddString(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar);
        parameter.Size = -1;
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static void AddDecimal(SqlCommand command, string name, double? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 2;
        parameter.Value = value.HasValue ? Convert.ToDecimal(value.Value) : DBNull.Value;
    }

    private static void AddInt(SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddLong(SqlCommand command, string name, long? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddBool(SqlCommand command, string name, bool value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Bit);
        parameter.Value = value;
    }

    private static void AddDate(SqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTimeOffset);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private sealed record PurgeCandidate(
        string Source,
        long Id,
        string? Uri,
        string? EntityType,
        string? EntityId,
        bool IsFilesystem,
        string DeleteSql,
        string? StringKey = null);

    private static string? NullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
