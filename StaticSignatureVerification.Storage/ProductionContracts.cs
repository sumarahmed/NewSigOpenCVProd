namespace StaticSignatureVerification.Storage;

public interface IProductionWorkflowStore
{
    Task<long> EnrollReferenceAsync(ReferenceEnrollmentRecord enrollment, CancellationToken cancellationToken = default);
    Task UpdateReferenceStatusAsync(ReferenceStatusUpdateRecord update, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DuplicateReferenceAlertRecord>> ScanDuplicateReferenceAlertsAsync(double threshold, CancellationToken cancellationToken = default);
    Task<long> CreateReviewCaseAsync(ReviewCaseRecord reviewCase, CancellationToken cancellationToken = default);
    Task UpdateReviewCaseAsync(ReviewCaseUpdateRecord update, CancellationToken cancellationToken = default);
    Task LogAuditEventAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken = default);
    Task<long> UpsertRetentionPolicyAsync(RetentionPolicyRecord policy, CancellationToken cancellationToken = default);
    Task<PurgeRunResultRecord> RunRetentionPurgeAsync(DateTimeOffset nowUtc, string actor, bool deleteFiles, CancellationToken cancellationToken = default);
    Task<long> CreateExportPackageAsync(ExportPackageRecord exportPackage, CancellationToken cancellationToken = default);
}

public sealed record ReferenceEnrollmentRecord(
    string ReferenceSetId,
    string ReferenceId,
    string SignatureId,
    string? DisplayName,
    string? ExternalPartyId,
    string? PartyName,
    string? LanguageCode,
    string? SourceFileName,
    string? SourceFilePath,
    string? StorageUri,
    string? Sha256Hash,
    int? ImageWidth,
    int? ImageHeight,
    string QualityStatus,
    double QualityScore,
    bool PassedQualityGate,
    string Actor,
    string? EnrollmentJson);

public sealed record ReferenceStatusUpdateRecord(
    string ReferenceId,
    string Action,
    string Actor,
    string? ReasonCode,
    string? Notes);

public sealed record DuplicateReferenceAlertRecord(
    long AlertId,
    string FirstReferenceId,
    string SecondReferenceId,
    double SimilarityScore,
    string AlertType,
    string AlertStatus);

public sealed record ReviewCaseRecord(
    string CaseNumber,
    string? DocumentResultId,
    string? SignatureId,
    string Priority,
    string? AssignedTo,
    DateTimeOffset? DueUtc,
    string? Branch,
    string? CustomerId,
    string? DocumentType,
    string? CaseJson);

public sealed record ReviewCaseUpdateRecord(
    string CaseNumber,
    string Action,
    string Actor,
    string? AssignedTo,
    string? Notes);

public sealed record AuditEventRecord(
    string EventType,
    string Severity,
    string? CorrelationId,
    string? Actor,
    string? EntityType,
    string? EntityId,
    string? Message,
    string? PropertiesJson);

public sealed record RetentionPolicyRecord(
    string PolicyName,
    string TargetObjectType,
    int RetentionDays,
    bool IsActive,
    string? CreatedBy,
    string? PolicyJson);

public sealed record PurgeRunResultRecord(
    long PurgeRunId,
    int CandidateCount,
    int PurgedCount,
    string Status,
    string? ErrorMessage);

public sealed record ExportPackageRecord(
    string PackageType,
    string PackageStatus,
    string? StorageUri,
    string? Sha256Hash,
    string? CreatedBy,
    string? ManifestJson);
