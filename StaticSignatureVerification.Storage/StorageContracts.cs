namespace StaticSignatureVerification.Storage;

public static class DbStorageDefaults
{
    public const string DefaultDatabaseName = "SignatureVerification";
    public const string DefaultLocalDbConnectionString =
        "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=SignatureVerification;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Application Name=SignatureVerification;";
}

public interface IVerificationResultStore
{
    Task UpsertVerificationResultAsync(VerificationResultRecord result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SignatureCaseRecord>> GetReviewQueueAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default);
}

public interface IReviewerOutcomeStore
{
    Task SaveReviewerOutcomeAsync(ReviewerOutcomeRecord outcome, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewerOutcomeRecord>> GetReviewerOutcomesAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default);
}

public sealed record VerificationResultRecord(
    string DocumentResultId,
    string? CorrelationId,
    string? DocumentName,
    string? SourceDocumentPath,
    string? ResultPath,
    string OverallDecision,
    double OverallConfidence,
    string InputDocumentType,
    int SignatureCountExpected,
    int SignatureCountDetected,
    DateTimeOffset EventUtc,
    double DurationMs,
    string? ErrorCode,
    string ResultJson,
    IReadOnlyList<SignatureCaseRecord> SignatureCases);

public sealed record SignatureCaseRecord(
    string DocumentResultId,
    string SignatureId,
    string? DisplayName,
    string Decision,
    double Confidence,
    bool IsMatched,
    bool ReviewRequired,
    bool SignatureDetected,
    string? SignatureQuality,
    string? PartyId,
    string? PartyName,
    string? ReferenceSetId,
    string? ExpectedSignerId,
    string? ActualSignerId,
    string? ExpectedClass,
    string? MappingSource,
    string? BestReferenceId,
    string? BestReferenceFileName,
    string? BestReferenceFilePath,
    string? ExtractedSignatureImagePath,
    int? DetectedPageIndex,
    int? DetectedX,
    int? DetectedY,
    int? DetectedWidth,
    int? DetectedHeight,
    int WarningCount,
    int CandidateCount,
    int DebugFileCount,
    string SignatureResultJson,
    IReadOnlyList<ReferenceComparisonRecord> ReferenceComparisons,
    IReadOnlyList<DebugArtifactRecord> DebugArtifacts);

public sealed record ReferenceComparisonRecord(
    string? ReferenceId,
    string? ReferenceFileName,
    string? ReferenceFilePath,
    double Confidence,
    double QualityAdjustedScore,
    bool IsBestMatch,
    string ComparisonJson);

public sealed record DebugArtifactRecord(
    string ArtifactType,
    string ArtifactPath);

public sealed record ReviewerOutcomeRecord(
    string DocumentResultId,
    string SignatureId,
    string? CorrelationId,
    string? DocumentName,
    string? PartyId,
    string? ReferenceSetId,
    string EngineDecision,
    double EngineConfidence,
    string ReviewerOutcome,
    string? ReasonCode,
    string? Reviewer,
    string? ReviewNotes,
    DateTimeOffset SavedUtc);
