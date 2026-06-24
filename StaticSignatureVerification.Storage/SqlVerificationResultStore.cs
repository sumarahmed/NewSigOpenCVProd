using Microsoft.Data.SqlClient;
using System.Data;

namespace StaticSignatureVerification.Storage;

public sealed class SqlVerificationResultStore : IVerificationResultStore, IReviewerOutcomeStore
{
    private readonly string _connectionString;

    public SqlVerificationResultStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task UpsertVerificationResultAsync(VerificationResultRecord result, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await UpsertDocumentAsync(connection, transaction, result, cancellationToken).ConfigureAwait(false);

            foreach (var signatureCase in result.SignatureCases)
            {
                var signatureCaseId = await UpsertSignatureCaseAsync(connection, transaction, signatureCase, cancellationToken).ConfigureAwait(false);
                await ReplaceReferenceComparisonsAsync(connection, transaction, signatureCaseId, signatureCase.ReferenceComparisons, cancellationToken).ConfigureAwait(false);
                await ReplaceDebugArtifactsAsync(connection, transaction, signatureCaseId, signatureCase.DebugArtifacts, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SignatureCaseRecord>> GetReviewQueueAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        var results = new List<SignatureCaseRecord>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT DocumentResultId, SignatureId, DisplayName, Decision, Confidence, IsMatched, ReviewRequired, SignatureDetected,
       SignatureQuality, PartyId, PartyName, ReferenceSetId, ExpectedSignerId, ActualSignerId, ExpectedClass, MappingSource,
       BestReferenceId, BestReferenceFileName, BestReferenceFilePath, ExtractedSignatureImagePath, WarningCount, CandidateCount, DebugFileCount
FROM ssv.vwReviewQueue
WHERE (@FromUtc IS NULL OR EventUtc >= @FromUtc)
  AND (@ToUtc IS NULL OR EventUtc <= @ToUtc)
ORDER BY EventUtc DESC, DocumentName, SignatureId;
""";
        AddDate(command, "@FromUtc", fromUtc);
        AddDate(command, "@ToUtc", toUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SignatureCaseRecord(
                DocumentResultId: reader.GetString(0),
                SignatureId: reader.GetString(1),
                DisplayName: NullableString(reader, 2),
                Decision: reader.GetString(3),
                Confidence: Convert.ToDouble(reader.GetDecimal(4)),
                IsMatched: reader.GetBoolean(5),
                ReviewRequired: reader.GetBoolean(6),
                SignatureDetected: reader.GetBoolean(7),
                SignatureQuality: NullableString(reader, 8),
                PartyId: NullableString(reader, 9),
                PartyName: NullableString(reader, 10),
                ReferenceSetId: NullableString(reader, 11),
                ExpectedSignerId: NullableString(reader, 12),
                ActualSignerId: NullableString(reader, 13),
                ExpectedClass: NullableString(reader, 14),
                MappingSource: NullableString(reader, 15),
                BestReferenceId: NullableString(reader, 16),
                BestReferenceFileName: NullableString(reader, 17),
                BestReferenceFilePath: NullableString(reader, 18),
                ExtractedSignatureImagePath: NullableString(reader, 19),
                DetectedPageIndex: null,
                DetectedX: null,
                DetectedY: null,
                DetectedWidth: null,
                DetectedHeight: null,
                WarningCount: reader.GetInt32(20),
                CandidateCount: reader.GetInt32(21),
                DebugFileCount: reader.GetInt32(22),
                SignatureResultJson: string.Empty,
                ReferenceComparisons: Array.Empty<ReferenceComparisonRecord>(),
                DebugArtifacts: Array.Empty<DebugArtifactRecord>()));
        }

        return results;
    }

    public async Task SaveReviewerOutcomeAsync(ReviewerOutcomeRecord outcome, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM ssv.ReviewerOutcome WHERE DocumentResultId = @DocumentResultId AND SignatureId = @SignatureId;";
                AddString(delete, "@DocumentResultId", outcome.DocumentResultId);
                AddString(delete, "@SignatureId", outcome.SignatureId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
INSERT INTO ssv.ReviewerOutcome
    (DocumentResultId, SignatureId, ReviewerOutcome, ReasonCode, Reviewer, ReviewNotes, SavedUtc)
VALUES
    (@DocumentResultId, @SignatureId, @ReviewerOutcome, @ReasonCode, @Reviewer, @ReviewNotes, @SavedUtc);
""";
                AddString(insert, "@DocumentResultId", outcome.DocumentResultId);
                AddString(insert, "@SignatureId", outcome.SignatureId);
                AddString(insert, "@ReviewerOutcome", outcome.ReviewerOutcome);
                AddString(insert, "@ReasonCode", outcome.ReasonCode);
                AddString(insert, "@Reviewer", outcome.Reviewer);
                AddString(insert, "@ReviewNotes", outcome.ReviewNotes);
                AddDate(insert, "@SavedUtc", outcome.SavedUtc);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ReviewerOutcomeRecord>> GetReviewerOutcomesAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<ReviewerOutcomeRecord>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT r.DocumentResultId, r.SignatureId, d.CorrelationId, d.DocumentName, c.PartyId, c.ReferenceSetId,
       c.Decision, c.Confidence, r.ReviewerOutcome, r.ReasonCode, r.Reviewer, r.ReviewNotes, r.SavedUtc
FROM ssv.ReviewerOutcome r
JOIN ssv.VerificationDocument d ON d.DocumentResultId = r.DocumentResultId
LEFT JOIN ssv.SignatureCase c ON c.DocumentResultId = r.DocumentResultId AND c.SignatureId = r.SignatureId
WHERE (@FromUtc IS NULL OR r.SavedUtc >= @FromUtc)
  AND (@ToUtc IS NULL OR r.SavedUtc <= @ToUtc)
ORDER BY r.SavedUtc DESC;
""";
        AddDate(command, "@FromUtc", fromUtc);
        AddDate(command, "@ToUtc", toUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            outcomes.Add(new ReviewerOutcomeRecord(
                DocumentResultId: reader.GetString(0),
                SignatureId: reader.GetString(1),
                CorrelationId: NullableString(reader, 2),
                DocumentName: NullableString(reader, 3),
                PartyId: NullableString(reader, 4),
                ReferenceSetId: NullableString(reader, 5),
                EngineDecision: NullableString(reader, 6) ?? string.Empty,
                EngineConfidence: reader.IsDBNull(7) ? 0 : Convert.ToDouble(reader.GetDecimal(7)),
                ReviewerOutcome: reader.GetString(8),
                ReasonCode: NullableString(reader, 9),
                Reviewer: NullableString(reader, 10),
                ReviewNotes: NullableString(reader, 11),
                SavedUtc: reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return outcomes;
    }

    private static async Task UpsertDocumentAsync(SqlConnection connection, SqlTransaction transaction, VerificationResultRecord result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
MERGE ssv.VerificationDocument AS target
USING (SELECT @DocumentResultId AS DocumentResultId) AS source
ON target.DocumentResultId = source.DocumentResultId
WHEN MATCHED THEN UPDATE SET
    CorrelationId = @CorrelationId,
    DocumentName = @DocumentName,
    SourceDocumentPath = @SourceDocumentPath,
    ResultPath = @ResultPath,
    InputDocumentType = @InputDocumentType,
    OverallDecision = @OverallDecision,
    OverallConfidence = @OverallConfidence,
    SignatureCountExpected = @SignatureCountExpected,
    SignatureCountDetected = @SignatureCountDetected,
    EventUtc = @EventUtc,
    DurationMs = @DurationMs,
    ErrorCode = @ErrorCode,
    ResultJson = @ResultJson,
    UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT
    (DocumentResultId, CorrelationId, DocumentName, SourceDocumentPath, ResultPath, InputDocumentType, OverallDecision, OverallConfidence,
     SignatureCountExpected, SignatureCountDetected, EventUtc, DurationMs, ErrorCode, ResultJson)
VALUES
    (@DocumentResultId, @CorrelationId, @DocumentName, @SourceDocumentPath, @ResultPath, @InputDocumentType, @OverallDecision, @OverallConfidence,
     @SignatureCountExpected, @SignatureCountDetected, @EventUtc, @DurationMs, @ErrorCode, @ResultJson);
""";
        AddString(command, "@DocumentResultId", result.DocumentResultId);
        AddString(command, "@CorrelationId", result.CorrelationId);
        AddString(command, "@DocumentName", result.DocumentName);
        AddString(command, "@SourceDocumentPath", result.SourceDocumentPath);
        AddString(command, "@ResultPath", result.ResultPath);
        AddString(command, "@InputDocumentType", result.InputDocumentType);
        AddString(command, "@OverallDecision", result.OverallDecision);
        AddDecimal(command, "@OverallConfidence", result.OverallConfidence);
        AddInt(command, "@SignatureCountExpected", result.SignatureCountExpected);
        AddInt(command, "@SignatureCountDetected", result.SignatureCountDetected);
        AddDate(command, "@EventUtc", result.EventUtc);
        AddDecimal(command, "@DurationMs", result.DurationMs);
        AddString(command, "@ErrorCode", result.ErrorCode);
        AddString(command, "@ResultJson", result.ResultJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> UpsertSignatureCaseAsync(SqlConnection connection, SqlTransaction transaction, SignatureCaseRecord result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
MERGE ssv.SignatureCase AS target
USING (SELECT @DocumentResultId AS DocumentResultId, @SignatureId AS SignatureId) AS source
ON target.DocumentResultId = source.DocumentResultId AND target.SignatureId = source.SignatureId
WHEN MATCHED THEN UPDATE SET
    DisplayName = @DisplayName,
    Decision = @Decision,
    Confidence = @Confidence,
    IsMatched = @IsMatched,
    ReviewRequired = @ReviewRequired,
    SignatureDetected = @SignatureDetected,
    SignatureQuality = @SignatureQuality,
    PartyId = @PartyId,
    PartyName = @PartyName,
    ReferenceSetId = @ReferenceSetId,
    ExpectedSignerId = @ExpectedSignerId,
    ActualSignerId = @ActualSignerId,
    ExpectedClass = @ExpectedClass,
    MappingSource = @MappingSource,
    BestReferenceId = @BestReferenceId,
    BestReferenceFileName = @BestReferenceFileName,
    BestReferenceFilePath = @BestReferenceFilePath,
    ExtractedSignatureImagePath = @ExtractedSignatureImagePath,
    DetectedPageIndex = @DetectedPageIndex,
    DetectedX = @DetectedX,
    DetectedY = @DetectedY,
    DetectedWidth = @DetectedWidth,
    DetectedHeight = @DetectedHeight,
    WarningCount = @WarningCount,
    CandidateCount = @CandidateCount,
    DebugFileCount = @DebugFileCount,
    SignatureResultJson = @SignatureResultJson,
    UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT
    (DocumentResultId, SignatureId, DisplayName, Decision, Confidence, IsMatched, ReviewRequired, SignatureDetected, SignatureQuality,
     PartyId, PartyName, ReferenceSetId, ExpectedSignerId, ActualSignerId, ExpectedClass, MappingSource,
     BestReferenceId, BestReferenceFileName, BestReferenceFilePath, ExtractedSignatureImagePath,
     DetectedPageIndex, DetectedX, DetectedY, DetectedWidth, DetectedHeight,
     WarningCount, CandidateCount, DebugFileCount, SignatureResultJson)
VALUES
    (@DocumentResultId, @SignatureId, @DisplayName, @Decision, @Confidence, @IsMatched, @ReviewRequired, @SignatureDetected, @SignatureQuality,
     @PartyId, @PartyName, @ReferenceSetId, @ExpectedSignerId, @ActualSignerId, @ExpectedClass, @MappingSource,
     @BestReferenceId, @BestReferenceFileName, @BestReferenceFilePath, @ExtractedSignatureImagePath,
     @DetectedPageIndex, @DetectedX, @DetectedY, @DetectedWidth, @DetectedHeight,
     @WarningCount, @CandidateCount, @DebugFileCount, @SignatureResultJson);
SELECT SignatureCaseId FROM ssv.SignatureCase WHERE DocumentResultId = @DocumentResultId AND SignatureId = @SignatureId;
""";
        AddString(command, "@DocumentResultId", result.DocumentResultId);
        AddString(command, "@SignatureId", result.SignatureId);
        AddString(command, "@DisplayName", result.DisplayName);
        AddString(command, "@Decision", result.Decision);
        AddDecimal(command, "@Confidence", result.Confidence);
        AddBool(command, "@IsMatched", result.IsMatched);
        AddBool(command, "@ReviewRequired", result.ReviewRequired);
        AddBool(command, "@SignatureDetected", result.SignatureDetected);
        AddString(command, "@SignatureQuality", result.SignatureQuality);
        AddString(command, "@PartyId", result.PartyId);
        AddString(command, "@PartyName", result.PartyName);
        AddString(command, "@ReferenceSetId", result.ReferenceSetId);
        AddString(command, "@ExpectedSignerId", result.ExpectedSignerId);
        AddString(command, "@ActualSignerId", result.ActualSignerId);
        AddString(command, "@ExpectedClass", result.ExpectedClass);
        AddString(command, "@MappingSource", result.MappingSource);
        AddString(command, "@BestReferenceId", result.BestReferenceId);
        AddString(command, "@BestReferenceFileName", result.BestReferenceFileName);
        AddString(command, "@BestReferenceFilePath", result.BestReferenceFilePath);
        AddString(command, "@ExtractedSignatureImagePath", result.ExtractedSignatureImagePath);
        AddInt(command, "@DetectedPageIndex", result.DetectedPageIndex);
        AddInt(command, "@DetectedX", result.DetectedX);
        AddInt(command, "@DetectedY", result.DetectedY);
        AddInt(command, "@DetectedWidth", result.DetectedWidth);
        AddInt(command, "@DetectedHeight", result.DetectedHeight);
        AddInt(command, "@WarningCount", result.WarningCount);
        AddInt(command, "@CandidateCount", result.CandidateCount);
        AddInt(command, "@DebugFileCount", result.DebugFileCount);
        AddString(command, "@SignatureResultJson", result.SignatureResultJson);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task ReplaceReferenceComparisonsAsync(SqlConnection connection, SqlTransaction transaction, long signatureCaseId, IReadOnlyList<ReferenceComparisonRecord> comparisons, CancellationToken cancellationToken)
    {
        await ExecuteDeleteAsync(connection, transaction, "DELETE FROM ssv.ReferenceComparison WHERE SignatureCaseId = @SignatureCaseId;", signatureCaseId, cancellationToken).ConfigureAwait(false);

        foreach (var comparison in comparisons)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO ssv.ReferenceComparison
    (SignatureCaseId, ReferenceId, ReferenceFileName, ReferenceFilePath, Confidence, QualityAdjustedScore, IsBestMatch, ComparisonJson)
VALUES
    (@SignatureCaseId, @ReferenceId, @ReferenceFileName, @ReferenceFilePath, @Confidence, @QualityAdjustedScore, @IsBestMatch, @ComparisonJson);
""";
            AddLong(command, "@SignatureCaseId", signatureCaseId);
            AddString(command, "@ReferenceId", comparison.ReferenceId);
            AddString(command, "@ReferenceFileName", comparison.ReferenceFileName);
            AddString(command, "@ReferenceFilePath", comparison.ReferenceFilePath);
            AddDecimal(command, "@Confidence", comparison.Confidence);
            AddDecimal(command, "@QualityAdjustedScore", comparison.QualityAdjustedScore);
            AddBool(command, "@IsBestMatch", comparison.IsBestMatch);
            AddString(command, "@ComparisonJson", comparison.ComparisonJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceDebugArtifactsAsync(SqlConnection connection, SqlTransaction transaction, long signatureCaseId, IReadOnlyList<DebugArtifactRecord> artifacts, CancellationToken cancellationToken)
    {
        await ExecuteDeleteAsync(connection, transaction, "DELETE FROM ssv.DebugArtifact WHERE SignatureCaseId = @SignatureCaseId;", signatureCaseId, cancellationToken).ConfigureAwait(false);

        foreach (var artifact in artifacts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO ssv.DebugArtifact
    (SignatureCaseId, ArtifactType, ArtifactPath)
VALUES
    (@SignatureCaseId, @ArtifactType, @ArtifactPath);
""";
            AddLong(command, "@SignatureCaseId", signatureCaseId);
            AddString(command, "@ArtifactType", artifact.ArtifactType);
            AddString(command, "@ArtifactPath", artifact.ArtifactPath);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteDeleteAsync(SqlConnection connection, SqlTransaction transaction, string sql, long signatureCaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddLong(command, "@SignatureCaseId", signatureCaseId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddString(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar);
        parameter.Size = -1;
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static void AddDecimal(SqlCommand command, string name, double value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 2;
        parameter.Value = Convert.ToDecimal(value);
    }

    private static void AddInt(SqlCommand command, string name, int? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Int);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddLong(SqlCommand command, string name, long value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value;
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

    private static string? NullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
