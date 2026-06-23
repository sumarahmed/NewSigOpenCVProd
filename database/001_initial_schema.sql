SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'ssv') IS NULL
    EXEC(N'CREATE SCHEMA ssv');
GO

CREATE TABLE ssv.VerificationDocument
(
    DocumentResultId      nvarchar(64)  NOT NULL CONSTRAINT PK_VerificationDocument PRIMARY KEY,
    CorrelationId         nvarchar(128) NULL,
    DocumentName          nvarchar(260) NULL,
    SourceDocumentPath    nvarchar(1024) NULL,
    ResultPath            nvarchar(1024) NULL,
    InputDocumentType     nvarchar(40)  NOT NULL CONSTRAINT DF_VerificationDocument_InputDocumentType DEFAULT(N'Unknown'),
    OverallDecision       nvarchar(40)  NOT NULL,
    OverallConfidence     decimal(9,2)  NOT NULL CONSTRAINT DF_VerificationDocument_OverallConfidence DEFAULT(0),
    SignatureCountExpected int          NOT NULL CONSTRAINT DF_VerificationDocument_SignatureCountExpected DEFAULT(0),
    SignatureCountDetected int          NOT NULL CONSTRAINT DF_VerificationDocument_SignatureCountDetected DEFAULT(0),
    EventUtc              datetimeoffset(7) NOT NULL,
    DurationMs            decimal(18,2) NOT NULL CONSTRAINT DF_VerificationDocument_DurationMs DEFAULT(0),
    ErrorCode             nvarchar(100) NULL,
    ResultJson            nvarchar(max) NOT NULL,
    CreatedUtc            datetimeoffset(7) NOT NULL CONSTRAINT DF_VerificationDocument_CreatedUtc DEFAULT(sysutcdatetime()),
    UpdatedUtc            datetimeoffset(7) NOT NULL CONSTRAINT DF_VerificationDocument_UpdatedUtc DEFAULT(sysutcdatetime())
);
GO

CREATE INDEX IX_VerificationDocument_EventUtc ON ssv.VerificationDocument(EventUtc DESC);
CREATE INDEX IX_VerificationDocument_CorrelationId ON ssv.VerificationDocument(CorrelationId) WHERE CorrelationId IS NOT NULL;
CREATE INDEX IX_VerificationDocument_OverallDecision ON ssv.VerificationDocument(OverallDecision);
GO

CREATE TABLE ssv.SignatureCase
(
    SignatureCaseId        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SignatureCase PRIMARY KEY,
    DocumentResultId       nvarchar(64) NOT NULL,
    SignatureId            nvarchar(128) NOT NULL,
    DisplayName            nvarchar(256) NULL,
    Decision               nvarchar(40) NOT NULL,
    Confidence             decimal(9,2) NOT NULL CONSTRAINT DF_SignatureCase_Confidence DEFAULT(0),
    IsMatched              bit NOT NULL CONSTRAINT DF_SignatureCase_IsMatched DEFAULT(0),
    ReviewRequired         bit NOT NULL CONSTRAINT DF_SignatureCase_ReviewRequired DEFAULT(0),
    SignatureDetected      bit NOT NULL CONSTRAINT DF_SignatureCase_SignatureDetected DEFAULT(0),
    SignatureQuality       nvarchar(60) NULL,
    PartyId                nvarchar(128) NULL,
    PartyName              nvarchar(256) NULL,
    ReferenceSetId         nvarchar(256) NULL,
    ExpectedSignerId       nvarchar(256) NULL,
    ActualSignerId         nvarchar(256) NULL,
    ExpectedClass          nvarchar(60) NULL,
    MappingSource          nvarchar(128) NULL,
    BestReferenceId        nvarchar(256) NULL,
    BestReferenceFileName  nvarchar(260) NULL,
    BestReferenceFilePath  nvarchar(1024) NULL,
    ExtractedSignatureImagePath nvarchar(1024) NULL,
    DetectedPageIndex      int NULL,
    DetectedX              int NULL,
    DetectedY              int NULL,
    DetectedWidth          int NULL,
    DetectedHeight         int NULL,
    WarningCount           int NOT NULL CONSTRAINT DF_SignatureCase_WarningCount DEFAULT(0),
    CandidateCount         int NOT NULL CONSTRAINT DF_SignatureCase_CandidateCount DEFAULT(0),
    DebugFileCount         int NOT NULL CONSTRAINT DF_SignatureCase_DebugFileCount DEFAULT(0),
    SignatureResultJson    nvarchar(max) NOT NULL,
    CreatedUtc             datetimeoffset(7) NOT NULL CONSTRAINT DF_SignatureCase_CreatedUtc DEFAULT(sysutcdatetime()),
    UpdatedUtc             datetimeoffset(7) NOT NULL CONSTRAINT DF_SignatureCase_UpdatedUtc DEFAULT(sysutcdatetime()),
    CONSTRAINT FK_SignatureCase_VerificationDocument
        FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId) ON DELETE CASCADE,
    CONSTRAINT UQ_SignatureCase_Document_Signature UNIQUE(DocumentResultId, SignatureId)
);
GO

CREATE INDEX IX_SignatureCase_ReviewQueue ON ssv.SignatureCase(ReviewRequired, Decision, SignatureDetected);
CREATE INDEX IX_SignatureCase_PartyId ON ssv.SignatureCase(PartyId) WHERE PartyId IS NOT NULL;
CREATE INDEX IX_SignatureCase_ReferenceSetId ON ssv.SignatureCase(ReferenceSetId) WHERE ReferenceSetId IS NOT NULL;
CREATE INDEX IX_SignatureCase_BestReferenceId ON ssv.SignatureCase(BestReferenceId) WHERE BestReferenceId IS NOT NULL;
GO

CREATE TABLE ssv.ReferenceComparison
(
    ReferenceComparisonId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceComparison PRIMARY KEY,
    SignatureCaseId       bigint NOT NULL,
    ReferenceId           nvarchar(256) NULL,
    ReferenceFileName     nvarchar(260) NULL,
    ReferenceFilePath     nvarchar(1024) NULL,
    Confidence            decimal(9,2) NOT NULL CONSTRAINT DF_ReferenceComparison_Confidence DEFAULT(0),
    QualityAdjustedScore  decimal(9,2) NOT NULL CONSTRAINT DF_ReferenceComparison_QualityAdjustedScore DEFAULT(0),
    IsBestMatch           bit NOT NULL CONSTRAINT DF_ReferenceComparison_IsBestMatch DEFAULT(0),
    ComparisonJson        nvarchar(max) NOT NULL,
    CONSTRAINT FK_ReferenceComparison_SignatureCase
        FOREIGN KEY(SignatureCaseId) REFERENCES ssv.SignatureCase(SignatureCaseId) ON DELETE CASCADE
);
GO

CREATE INDEX IX_ReferenceComparison_SignatureCase ON ssv.ReferenceComparison(SignatureCaseId);
CREATE INDEX IX_ReferenceComparison_ReferenceId ON ssv.ReferenceComparison(ReferenceId) WHERE ReferenceId IS NOT NULL;
GO

CREATE TABLE ssv.DebugArtifact
(
    DebugArtifactId   bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DebugArtifact PRIMARY KEY,
    SignatureCaseId   bigint NOT NULL,
    ArtifactType      nvarchar(100) NOT NULL,
    ArtifactPath      nvarchar(1024) NOT NULL,
    CreatedUtc        datetimeoffset(7) NOT NULL CONSTRAINT DF_DebugArtifact_CreatedUtc DEFAULT(sysutcdatetime()),
    CONSTRAINT FK_DebugArtifact_SignatureCase
        FOREIGN KEY(SignatureCaseId) REFERENCES ssv.SignatureCase(SignatureCaseId) ON DELETE CASCADE
);
GO

CREATE INDEX IX_DebugArtifact_SignatureCase ON ssv.DebugArtifact(SignatureCaseId);
GO

CREATE TABLE ssv.ReviewerOutcome
(
    ReviewerOutcomeId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReviewerOutcome PRIMARY KEY,
    DocumentResultId  nvarchar(64) NOT NULL,
    SignatureId       nvarchar(128) NOT NULL,
    ReviewerOutcome   nvarchar(80) NOT NULL,
    ReasonCode        nvarchar(120) NULL,
    Reviewer          nvarchar(256) NULL,
    ReviewNotes       nvarchar(max) NULL,
    SavedUtc          datetimeoffset(7) NOT NULL,
    ImportedUtc       datetimeoffset(7) NOT NULL CONSTRAINT DF_ReviewerOutcome_ImportedUtc DEFAULT(sysutcdatetime()),
    SourceCsvPath     nvarchar(1024) NULL,
    CONSTRAINT FK_ReviewerOutcome_VerificationDocument
        FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId) ON DELETE CASCADE
);
GO

CREATE INDEX IX_ReviewerOutcome_DocumentSignature ON ssv.ReviewerOutcome(DocumentResultId, SignatureId);
CREATE INDEX IX_ReviewerOutcome_SavedUtc ON ssv.ReviewerOutcome(SavedUtc DESC);
GO

CREATE TABLE ssv.ThresholdProfile
(
    ThresholdProfileId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ThresholdProfile PRIMARY KEY,
    ProfileName        nvarchar(128) NOT NULL,
    MatchedThreshold   decimal(9,2) NOT NULL,
    ProbableThreshold  decimal(9,2) NOT NULL,
    ReviewThreshold    decimal(9,2) NOT NULL,
    IsActive           bit NOT NULL CONSTRAINT DF_ThresholdProfile_IsActive DEFAULT(0),
    ProfileJson        nvarchar(max) NULL,
    CreatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_ThresholdProfile_CreatedUtc DEFAULT(sysutcdatetime()),
    CONSTRAINT UQ_ThresholdProfile_ProfileName UNIQUE(ProfileName)
);
GO

CREATE OR ALTER VIEW ssv.vwReviewQueue
AS
SELECT
    d.EventUtc,
    d.DocumentName,
    d.DocumentResultId,
    d.CorrelationId,
    d.ResultPath,
    d.SourceDocumentPath,
    d.OverallDecision,
    c.SignatureCaseId,
    c.SignatureId,
    c.DisplayName,
    c.PartyId,
    c.PartyName,
    c.ReferenceSetId,
    c.ExpectedSignerId,
    c.ActualSignerId,
    c.ExpectedClass,
    c.MappingSource,
    c.Decision,
    c.Confidence,
    c.IsMatched,
    c.ReviewRequired,
    c.SignatureDetected,
    c.SignatureQuality,
    c.BestReferenceId,
    c.BestReferenceFileName,
    c.BestReferenceFilePath,
    c.ExtractedSignatureImagePath,
    c.WarningCount,
    c.CandidateCount,
    c.DebugFileCount
FROM ssv.VerificationDocument d
JOIN ssv.SignatureCase c ON c.DocumentResultId = d.DocumentResultId
WHERE
    d.OverallDecision = N'Error'
    OR c.ReviewRequired = 1
    OR c.SignatureDetected = 0
    OR c.Decision IN (N'ProbableMatch', N'ReviewRequired', N'InsufficientQuality', N'NotMatched', N'NoSignatureDetected');
GO

