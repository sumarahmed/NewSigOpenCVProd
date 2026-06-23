SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'ssv.SystemSetting', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.SystemSetting
    (
        SettingKey   nvarchar(128) NOT NULL CONSTRAINT PK_SystemSetting PRIMARY KEY,
        SettingValue nvarchar(max) NULL,
        UpdatedBy    nvarchar(256) NULL,
        UpdatedUtc   datetimeoffset(7) NOT NULL CONSTRAINT DF_SystemSetting_UpdatedUtc DEFAULT(sysutcdatetime())
    );
END
GO

IF OBJECT_ID(N'ssv.DocumentBlob', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.DocumentBlob
    (
        DocumentBlobId    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentBlob PRIMARY KEY,
        DocumentBlobKey   nvarchar(128) NOT NULL,
        DocumentResultId  nvarchar(64) NULL,
        CorrelationId     nvarchar(128) NULL,
        DocumentName      nvarchar(260) NULL,
        FileName          nvarchar(260) NULL,
        ContentType       nvarchar(100) NOT NULL,
        Sha256Hash        varchar(64) NOT NULL,
        SizeBytes         bigint NOT NULL,
        ContentBytes      varbinary(max) NOT NULL,
        CreatedBy         nvarchar(256) NULL,
        CreatedUtc        datetimeoffset(7) NOT NULL CONSTRAINT DF_DocumentBlob_CreatedUtc DEFAULT(sysutcdatetime()),
        RetainUntilUtc    datetimeoffset(7) NULL,
        MetadataJson      nvarchar(max) NULL,
        CONSTRAINT UQ_DocumentBlob_Key UNIQUE(DocumentBlobKey),
        CONSTRAINT FK_DocumentBlob_VerificationDocument
            FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId) ON DELETE SET NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DocumentBlob_Correlation' AND object_id = OBJECT_ID(N'ssv.DocumentBlob'))
    CREATE INDEX IX_DocumentBlob_Correlation ON ssv.DocumentBlob(CorrelationId) WHERE CorrelationId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DocumentBlob_Hash' AND object_id = OBJECT_ID(N'ssv.DocumentBlob'))
    CREATE INDEX IX_DocumentBlob_Hash ON ssv.DocumentBlob(Sha256Hash);
GO

IF OBJECT_ID(N'ssv.ReferenceImageBlob', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceImageBlob
    (
        ReferenceImageBlobId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceImageBlob PRIMARY KEY,
        ReferenceImageRegistryId bigint NOT NULL,
        ContentType              nvarchar(100) NOT NULL,
        Sha256Hash               varchar(64) NOT NULL,
        SizeBytes                bigint NOT NULL,
        ContentBytes             varbinary(max) NOT NULL,
        CreatedUtc               datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceImageBlob_CreatedUtc DEFAULT(sysutcdatetime()),
        CONSTRAINT FK_ReferenceImageBlob_ReferenceImageRegistry
            FOREIGN KEY(ReferenceImageRegistryId) REFERENCES ssv.ReferenceImageRegistry(ReferenceImageRegistryId) ON DELETE CASCADE,
        CONSTRAINT UQ_ReferenceImageBlob_Registry UNIQUE(ReferenceImageRegistryId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReferenceImageBlob_Hash' AND object_id = OBJECT_ID(N'ssv.ReferenceImageBlob'))
    CREATE INDEX IX_ReferenceImageBlob_Hash ON ssv.ReferenceImageBlob(Sha256Hash);
GO

IF OBJECT_ID(N'ssv.DebugArtifactBlob', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.DebugArtifactBlob
    (
        DebugArtifactBlobId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DebugArtifactBlob PRIMARY KEY,
        DebugArtifactId     bigint NULL,
        SignatureCaseId     bigint NULL,
        DocumentResultId    nvarchar(64) NULL,
        ArtifactType        nvarchar(100) NOT NULL,
        ContentType         nvarchar(100) NOT NULL,
        Sha256Hash          varchar(64) NOT NULL,
        SizeBytes           bigint NOT NULL,
        ContentBytes        varbinary(max) NOT NULL,
        CreatedUtc          datetimeoffset(7) NOT NULL CONSTRAINT DF_DebugArtifactBlob_CreatedUtc DEFAULT(sysutcdatetime()),
        RetainUntilUtc      datetimeoffset(7) NULL,
        MetadataJson        nvarchar(max) NULL,
        CONSTRAINT FK_DebugArtifactBlob_DebugArtifact
            FOREIGN KEY(DebugArtifactId) REFERENCES ssv.DebugArtifact(DebugArtifactId) ON DELETE SET NULL,
        CONSTRAINT FK_DebugArtifactBlob_SignatureCase
            FOREIGN KEY(SignatureCaseId) REFERENCES ssv.SignatureCase(SignatureCaseId),
        CONSTRAINT FK_DebugArtifactBlob_Document
            FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DebugArtifactBlob_Document' AND object_id = OBJECT_ID(N'ssv.DebugArtifactBlob'))
    CREATE INDEX IX_DebugArtifactBlob_Document ON ssv.DebugArtifactBlob(DocumentResultId, ArtifactType);
GO

IF OBJECT_ID(N'ssv.ReportBlob', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReportBlob
    (
        ReportBlobId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReportBlob PRIMARY KEY,
        ReportType    nvarchar(100) NOT NULL,
        ReportName    nvarchar(260) NOT NULL,
        ContentType   nvarchar(100) NOT NULL,
        Sha256Hash    varchar(64) NOT NULL,
        SizeBytes     bigint NOT NULL,
        ContentBytes  varbinary(max) NOT NULL,
        CreatedBy     nvarchar(256) NULL,
        CreatedUtc    datetimeoffset(7) NOT NULL CONSTRAINT DF_ReportBlob_CreatedUtc DEFAULT(sysutcdatetime()),
        RetainUntilUtc datetimeoffset(7) NULL,
        MetadataJson   nvarchar(max) NULL
    );
END
GO

CREATE OR ALTER VIEW ssv.vwApprovedReferenceImageNative
AS
SELECT
    s.ReferenceSetId,
    s.SignatureId,
    s.DisplayName,
    s.LanguageCode,
    subj.ExternalPartyId,
    subj.PartyName,
    i.ReferenceImageRegistryId,
    i.ReferenceId,
    i.VersionNumber,
    i.SourceFileName,
    i.QualityStatus,
    i.QualityScore,
    b.ContentType,
    b.Sha256Hash,
    b.SizeBytes,
    b.ContentBytes
FROM ssv.ReferenceSetRegistry s
LEFT JOIN ssv.ReferenceSubject subj ON subj.ReferenceSubjectId = s.ReferenceSubjectId
JOIN ssv.ReferenceImageRegistry i ON i.ReferenceSetRegistryId = s.ReferenceSetRegistryId
JOIN ssv.ReferenceImageBlob b ON b.ReferenceImageRegistryId = i.ReferenceImageRegistryId
WHERE
    s.Status = N'Approved'
    AND i.EnrollmentStatus = N'Approved'
    AND i.IsApprovedForMatching = 1
    AND (i.EffectiveFromUtc IS NULL OR i.EffectiveFromUtc <= sysutcdatetime())
    AND (i.EffectiveToUtc IS NULL OR i.EffectiveToUtc > sysutcdatetime());
GO

MERGE ssv.SystemSetting AS target
USING (SELECT N'StorageMode' AS SettingKey, N'Hybrid' AS SettingValue) AS source
ON target.SettingKey = source.SettingKey
WHEN NOT MATCHED THEN INSERT(SettingKey, SettingValue, UpdatedBy)
VALUES(source.SettingKey, source.SettingValue, N'Migration');
GO
