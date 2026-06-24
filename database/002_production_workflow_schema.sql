SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'ssv') IS NULL
    EXEC(N'CREATE SCHEMA ssv');
GO

IF OBJECT_ID(N'ssv.ReferenceSubject', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceSubject
    (
        ReferenceSubjectId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceSubject PRIMARY KEY,
        ExternalPartyId    nvarchar(128) NOT NULL,
        PartyName          nvarchar(256) NULL,
        Status             nvarchar(40) NOT NULL CONSTRAINT DF_ReferenceSubject_Status DEFAULT(N'Active'),
        CreatedBy          nvarchar(256) NULL,
        CreatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceSubject_CreatedUtc DEFAULT(sysutcdatetime()),
        UpdatedBy          nvarchar(256) NULL,
        UpdatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceSubject_UpdatedUtc DEFAULT(sysutcdatetime()),
        MetadataJson       nvarchar(max) NULL,
        CONSTRAINT UQ_ReferenceSubject_ExternalPartyId UNIQUE(ExternalPartyId)
    );
END
GO

IF OBJECT_ID(N'ssv.ReferenceSetRegistry', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceSetRegistry
    (
        ReferenceSetRegistryId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceSetRegistry PRIMARY KEY,
        ReferenceSetId         nvarchar(256) NOT NULL,
        ReferenceSubjectId     bigint NULL,
        SignatureId            nvarchar(128) NOT NULL,
        DisplayName            nvarchar(256) NULL,
        LanguageCode           nvarchar(40) NULL,
        Status                 nvarchar(40) NOT NULL CONSTRAINT DF_ReferenceSetRegistry_Status DEFAULT(N'Draft'),
        ActiveVersion          int NOT NULL CONSTRAINT DF_ReferenceSetRegistry_ActiveVersion DEFAULT(1),
        QualityStatus          nvarchar(40) NULL,
        QualityScore           decimal(9,2) NULL,
        CreatedBy              nvarchar(256) NULL,
        CreatedUtc             datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceSetRegistry_CreatedUtc DEFAULT(sysutcdatetime()),
        SubmittedBy            nvarchar(256) NULL,
        SubmittedUtc           datetimeoffset(7) NULL,
        ApprovedBy             nvarchar(256) NULL,
        ApprovedUtc            datetimeoffset(7) NULL,
        RetiredBy              nvarchar(256) NULL,
        RetiredUtc             datetimeoffset(7) NULL,
        RetirementReason       nvarchar(512) NULL,
        MetadataJson           nvarchar(max) NULL,
        CONSTRAINT FK_ReferenceSetRegistry_ReferenceSubject
            FOREIGN KEY(ReferenceSubjectId) REFERENCES ssv.ReferenceSubject(ReferenceSubjectId),
        CONSTRAINT UQ_ReferenceSetRegistry_ReferenceSetId UNIQUE(ReferenceSetId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReferenceSetRegistry_Status' AND object_id = OBJECT_ID(N'ssv.ReferenceSetRegistry'))
    CREATE INDEX IX_ReferenceSetRegistry_Status ON ssv.ReferenceSetRegistry(Status, SignatureId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReferenceSetRegistry_Subject' AND object_id = OBJECT_ID(N'ssv.ReferenceSetRegistry'))
    CREATE INDEX IX_ReferenceSetRegistry_Subject ON ssv.ReferenceSetRegistry(ReferenceSubjectId) WHERE ReferenceSubjectId IS NOT NULL;
GO

IF OBJECT_ID(N'ssv.ReferenceImageRegistry', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceImageRegistry
    (
        ReferenceImageRegistryId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceImageRegistry PRIMARY KEY,
        ReferenceSetRegistryId   bigint NOT NULL,
        ReferenceId              nvarchar(256) NOT NULL,
        VersionNumber            int NOT NULL CONSTRAINT DF_ReferenceImageRegistry_VersionNumber DEFAULT(1),
        SourceFileName           nvarchar(260) NULL,
        SourceFilePath           nvarchar(1024) NULL,
        StorageUri               nvarchar(1024) NULL,
        Sha256Hash               varchar(64) NULL,
        ImageWidth               int NULL,
        ImageHeight              int NULL,
        QualityStatus            nvarchar(40) NULL,
        QualityScore             decimal(9,2) NULL,
        EnrollmentStatus         nvarchar(40) NOT NULL CONSTRAINT DF_ReferenceImageRegistry_EnrollmentStatus DEFAULT(N'Draft'),
        IsApprovedForMatching    bit NOT NULL CONSTRAINT DF_ReferenceImageRegistry_IsApproved DEFAULT(0),
        EffectiveFromUtc         datetimeoffset(7) NULL,
        EffectiveToUtc           datetimeoffset(7) NULL,
        ReplacedByReferenceImageRegistryId bigint NULL,
        CreatedBy                nvarchar(256) NULL,
        CreatedUtc               datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceImageRegistry_CreatedUtc DEFAULT(sysutcdatetime()),
        ApprovedBy               nvarchar(256) NULL,
        ApprovedUtc              datetimeoffset(7) NULL,
        RetiredBy                nvarchar(256) NULL,
        RetiredUtc               datetimeoffset(7) NULL,
        EnrollmentJson           nvarchar(max) NULL,
        CONSTRAINT FK_ReferenceImageRegistry_Set
            FOREIGN KEY(ReferenceSetRegistryId) REFERENCES ssv.ReferenceSetRegistry(ReferenceSetRegistryId) ON DELETE CASCADE,
        CONSTRAINT FK_ReferenceImageRegistry_ReplacedBy
            FOREIGN KEY(ReplacedByReferenceImageRegistryId) REFERENCES ssv.ReferenceImageRegistry(ReferenceImageRegistryId),
        CONSTRAINT UQ_ReferenceImageRegistry_Reference UNIQUE(ReferenceSetRegistryId, ReferenceId, VersionNumber)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReferenceImageRegistry_Matching' AND object_id = OBJECT_ID(N'ssv.ReferenceImageRegistry'))
    CREATE INDEX IX_ReferenceImageRegistry_Matching ON ssv.ReferenceImageRegistry(IsApprovedForMatching, EnrollmentStatus, EffectiveFromUtc, EffectiveToUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReferenceImageRegistry_Hash' AND object_id = OBJECT_ID(N'ssv.ReferenceImageRegistry'))
    CREATE INDEX IX_ReferenceImageRegistry_Hash ON ssv.ReferenceImageRegistry(Sha256Hash) WHERE Sha256Hash IS NOT NULL;
GO

IF OBJECT_ID(N'ssv.ReferenceApprovalEvent', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceApprovalEvent
    (
        ReferenceApprovalEventId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceApprovalEvent PRIMARY KEY,
        EntityType               nvarchar(60) NOT NULL,
        EntityId                 bigint NOT NULL,
        Action                   nvarchar(60) NOT NULL,
        Actor                    nvarchar(256) NULL,
        ActionUtc                datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceApprovalEvent_ActionUtc DEFAULT(sysutcdatetime()),
        ReasonCode               nvarchar(120) NULL,
        Notes                    nvarchar(max) NULL,
        BeforeJson               nvarchar(max) NULL,
        AfterJson                nvarchar(max) NULL
    );
END
GO

IF OBJECT_ID(N'ssv.ReferenceSimilarityAlert', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReferenceSimilarityAlert
    (
        ReferenceSimilarityAlertId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceSimilarityAlert PRIMARY KEY,
        FirstReferenceImageRegistryId bigint NOT NULL,
        SecondReferenceImageRegistryId bigint NOT NULL,
        SimilarityScore           decimal(9,2) NOT NULL,
        AlertType                 nvarchar(80) NOT NULL,
        AlertStatus               nvarchar(40) NOT NULL CONSTRAINT DF_ReferenceSimilarityAlert_Status DEFAULT(N'Open'),
        CreatedUtc                datetimeoffset(7) NOT NULL CONSTRAINT DF_ReferenceSimilarityAlert_CreatedUtc DEFAULT(sysutcdatetime()),
        ReviewedBy                nvarchar(256) NULL,
        ReviewedUtc               datetimeoffset(7) NULL,
        ReviewNotes               nvarchar(max) NULL,
        CONSTRAINT FK_ReferenceSimilarityAlert_First
            FOREIGN KEY(FirstReferenceImageRegistryId) REFERENCES ssv.ReferenceImageRegistry(ReferenceImageRegistryId),
        CONSTRAINT FK_ReferenceSimilarityAlert_Second
            FOREIGN KEY(SecondReferenceImageRegistryId) REFERENCES ssv.ReferenceImageRegistry(ReferenceImageRegistryId)
    );
END
GO

IF OBJECT_ID(N'ssv.FormTemplate', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.FormTemplate
    (
        FormTemplateId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FormTemplate PRIMARY KEY,
        TemplateKey        nvarchar(128) NOT NULL,
        DocumentType       nvarchar(128) NOT NULL,
        TemplateName       nvarchar(256) NOT NULL,
        VersionNumber      int NOT NULL,
        Status             nvarchar(40) NOT NULL CONSTRAINT DF_FormTemplate_Status DEFAULT(N'Draft'),
        ThresholdProfileId bigint NULL,
        CreatedBy          nvarchar(256) NULL,
        CreatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_FormTemplate_CreatedUtc DEFAULT(sysutcdatetime()),
        ApprovedBy         nvarchar(256) NULL,
        ApprovedUtc        datetimeoffset(7) NULL,
        RetiredBy          nvarchar(256) NULL,
        RetiredUtc         datetimeoffset(7) NULL,
        TemplateJson       nvarchar(max) NULL,
        CONSTRAINT FK_FormTemplate_ThresholdProfile
            FOREIGN KEY(ThresholdProfileId) REFERENCES ssv.ThresholdProfile(ThresholdProfileId),
        CONSTRAINT UQ_FormTemplate_KeyVersion UNIQUE(TemplateKey, VersionNumber)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FormTemplate_DocumentTypeStatus' AND object_id = OBJECT_ID(N'ssv.FormTemplate'))
    CREATE INDEX IX_FormTemplate_DocumentTypeStatus ON ssv.FormTemplate(DocumentType, Status, VersionNumber DESC);
GO

IF OBJECT_ID(N'ssv.FormTemplateZone', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.FormTemplateZone
    (
        FormTemplateZoneId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FormTemplateZone PRIMARY KEY,
        FormTemplateId     bigint NOT NULL,
        SignatureId        nvarchar(128) NOT NULL,
        DisplayName        nvarchar(256) NULL,
        PageIndex          int NOT NULL,
        X                  decimal(18,6) NOT NULL,
        Y                  decimal(18,6) NOT NULL,
        Width              decimal(18,6) NOT NULL,
        Height             decimal(18,6) NOT NULL,
        CoordinateSystem   nvarchar(40) NOT NULL,
        ExpectedLabel      nvarchar(256) NULL,
        IsRequired         bit NOT NULL CONSTRAINT DF_FormTemplateZone_IsRequired DEFAULT(1),
        ZoneJson           nvarchar(max) NULL,
        CONSTRAINT FK_FormTemplateZone_Template
            FOREIGN KEY(FormTemplateId) REFERENCES ssv.FormTemplate(FormTemplateId) ON DELETE CASCADE,
        CONSTRAINT UQ_FormTemplateZone_TemplateSignature UNIQUE(FormTemplateId, SignatureId)
    );
END
GO

IF OBJECT_ID(N'ssv.ResultGovernanceSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ResultGovernanceSnapshot
    (
        ResultGovernanceSnapshotId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ResultGovernanceSnapshot PRIMARY KEY,
        DocumentResultId            nvarchar(64) NOT NULL,
        EngineVersion               nvarchar(40) NULL,
        ThresholdProfileId          bigint NULL,
        FormTemplateId              bigint NULL,
        ReferenceSetRegistryId      bigint NULL,
        ReferenceImageRegistryId    bigint NULL,
        SnapshotJson                nvarchar(max) NULL,
        CreatedUtc                  datetimeoffset(7) NOT NULL CONSTRAINT DF_ResultGovernanceSnapshot_CreatedUtc DEFAULT(sysutcdatetime()),
        CONSTRAINT FK_ResultGovernanceSnapshot_Document
            FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId) ON DELETE CASCADE,
        CONSTRAINT FK_ResultGovernanceSnapshot_Threshold
            FOREIGN KEY(ThresholdProfileId) REFERENCES ssv.ThresholdProfile(ThresholdProfileId),
        CONSTRAINT FK_ResultGovernanceSnapshot_Template
            FOREIGN KEY(FormTemplateId) REFERENCES ssv.FormTemplate(FormTemplateId),
        CONSTRAINT FK_ResultGovernanceSnapshot_ReferenceSet
            FOREIGN KEY(ReferenceSetRegistryId) REFERENCES ssv.ReferenceSetRegistry(ReferenceSetRegistryId),
        CONSTRAINT FK_ResultGovernanceSnapshot_ReferenceImage
            FOREIGN KEY(ReferenceImageRegistryId) REFERENCES ssv.ReferenceImageRegistry(ReferenceImageRegistryId)
    );
END
GO

IF OBJECT_ID(N'ssv.ReviewCase', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReviewCase
    (
        ReviewCaseId       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReviewCase PRIMARY KEY,
        CaseNumber         nvarchar(80) NOT NULL,
        DocumentResultId   nvarchar(64) NULL,
        SignatureCaseId    bigint NULL,
        CaseStatus         nvarchar(40) NOT NULL CONSTRAINT DF_ReviewCase_Status DEFAULT(N'New'),
        Priority           nvarchar(40) NOT NULL CONSTRAINT DF_ReviewCase_Priority DEFAULT(N'Normal'),
        AssignedTo         nvarchar(256) NULL,
        AssignedBy         nvarchar(256) NULL,
        AssignedUtc        datetimeoffset(7) NULL,
        DueUtc             datetimeoffset(7) NULL,
        CompletedUtc       datetimeoffset(7) NULL,
        EscalatedUtc       datetimeoffset(7) NULL,
        Branch             nvarchar(128) NULL,
        CustomerId         nvarchar(128) NULL,
        DocumentType       nvarchar(128) NULL,
        CreatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_ReviewCase_CreatedUtc DEFAULT(sysutcdatetime()),
        UpdatedUtc         datetimeoffset(7) NOT NULL CONSTRAINT DF_ReviewCase_UpdatedUtc DEFAULT(sysutcdatetime()),
        CaseJson           nvarchar(max) NULL,
        CONSTRAINT UQ_ReviewCase_CaseNumber UNIQUE(CaseNumber),
        CONSTRAINT FK_ReviewCase_Document
            FOREIGN KEY(DocumentResultId) REFERENCES ssv.VerificationDocument(DocumentResultId) ON DELETE CASCADE,
        CONSTRAINT FK_ReviewCase_SignatureCase
            FOREIGN KEY(SignatureCaseId) REFERENCES ssv.SignatureCase(SignatureCaseId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReviewCase_Queue' AND object_id = OBJECT_ID(N'ssv.ReviewCase'))
    CREATE INDEX IX_ReviewCase_Queue ON ssv.ReviewCase(CaseStatus, Priority, DueUtc, AssignedTo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReviewCase_Customer' AND object_id = OBJECT_ID(N'ssv.ReviewCase'))
    CREATE INDEX IX_ReviewCase_Customer ON ssv.ReviewCase(CustomerId, DocumentType) WHERE CustomerId IS NOT NULL;
GO

IF OBJECT_ID(N'ssv.ReviewCaseEvent', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ReviewCaseEvent
    (
        ReviewCaseEventId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReviewCaseEvent PRIMARY KEY,
        ReviewCaseId      bigint NOT NULL,
        EventType         nvarchar(80) NOT NULL,
        Actor             nvarchar(256) NULL,
        EventUtc          datetimeoffset(7) NOT NULL CONSTRAINT DF_ReviewCaseEvent_EventUtc DEFAULT(sysutcdatetime()),
        Notes             nvarchar(max) NULL,
        EventJson         nvarchar(max) NULL,
        CONSTRAINT FK_ReviewCaseEvent_Case
            FOREIGN KEY(ReviewCaseId) REFERENCES ssv.ReviewCase(ReviewCaseId) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'ssv.SecurityRole', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.SecurityRole
    (
        SecurityRoleId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SecurityRole PRIMARY KEY,
        RoleName       nvarchar(128) NOT NULL,
        Description    nvarchar(512) NULL,
        IsActive       bit NOT NULL CONSTRAINT DF_SecurityRole_IsActive DEFAULT(1),
        CreatedUtc     datetimeoffset(7) NOT NULL CONSTRAINT DF_SecurityRole_CreatedUtc DEFAULT(sysutcdatetime()),
        CONSTRAINT UQ_SecurityRole_RoleName UNIQUE(RoleName)
    );
END
GO

IF OBJECT_ID(N'ssv.SecurityPrincipalRole', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.SecurityPrincipalRole
    (
        SecurityPrincipalRoleId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SecurityPrincipalRole PRIMARY KEY,
        PrincipalName           nvarchar(256) NOT NULL,
        SecurityRoleId          bigint NOT NULL,
        GrantedBy               nvarchar(256) NULL,
        GrantedUtc              datetimeoffset(7) NOT NULL CONSTRAINT DF_SecurityPrincipalRole_GrantedUtc DEFAULT(sysutcdatetime()),
        RevokedUtc              datetimeoffset(7) NULL,
        CONSTRAINT FK_SecurityPrincipalRole_Role
            FOREIGN KEY(SecurityRoleId) REFERENCES ssv.SecurityRole(SecurityRoleId),
        CONSTRAINT UQ_SecurityPrincipalRole_Active UNIQUE(PrincipalName, SecurityRoleId, RevokedUtc)
    );
END
GO

IF OBJECT_ID(N'ssv.AuditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.AuditEvent
    (
        AuditEventId   bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditEvent PRIMARY KEY,
        EventUtc       datetimeoffset(7) NOT NULL CONSTRAINT DF_AuditEvent_EventUtc DEFAULT(sysutcdatetime()),
        EventType      nvarchar(120) NOT NULL,
        Severity       nvarchar(40) NOT NULL CONSTRAINT DF_AuditEvent_Severity DEFAULT(N'Information'),
        CorrelationId  nvarchar(128) NULL,
        Actor          nvarchar(256) NULL,
        EntityType     nvarchar(80) NULL,
        EntityId       nvarchar(128) NULL,
        Message        nvarchar(1024) NULL,
        PropertiesJson nvarchar(max) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvent_EventUtc' AND object_id = OBJECT_ID(N'ssv.AuditEvent'))
    CREATE INDEX IX_AuditEvent_EventUtc ON ssv.AuditEvent(EventUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvent_Correlation' AND object_id = OBJECT_ID(N'ssv.AuditEvent'))
    CREATE INDEX IX_AuditEvent_Correlation ON ssv.AuditEvent(CorrelationId) WHERE CorrelationId IS NOT NULL;
GO

IF OBJECT_ID(N'ssv.StorageObject', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.StorageObject
    (
        StorageObjectId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StorageObject PRIMARY KEY,
        ObjectType      nvarchar(80) NOT NULL,
        EntityType      nvarchar(80) NULL,
        EntityId        nvarchar(128) NULL,
        StorageUri      nvarchar(1024) NOT NULL,
        Sha256Hash      varchar(64) NULL,
        SizeBytes       bigint NULL,
        IsEncrypted     bit NOT NULL CONSTRAINT DF_StorageObject_IsEncrypted DEFAULT(0),
        EncryptionKeyId nvarchar(256) NULL,
        RetainUntilUtc  datetimeoffset(7) NULL,
        CreatedUtc      datetimeoffset(7) NOT NULL CONSTRAINT DF_StorageObject_CreatedUtc DEFAULT(sysutcdatetime()),
        MetadataJson    nvarchar(max) NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StorageObject_Retention' AND object_id = OBJECT_ID(N'ssv.StorageObject'))
    CREATE INDEX IX_StorageObject_Retention ON ssv.StorageObject(RetainUntilUtc, ObjectType) WHERE RetainUntilUtc IS NOT NULL;
GO

IF OBJECT_ID(N'ssv.RetentionPolicy', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.RetentionPolicy
    (
        RetentionPolicyId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY,
        PolicyName        nvarchar(128) NOT NULL,
        TargetObjectType  nvarchar(80) NOT NULL,
        RetentionDays     int NOT NULL,
        IsActive          bit NOT NULL CONSTRAINT DF_RetentionPolicy_IsActive DEFAULT(1),
        CreatedBy         nvarchar(256) NULL,
        CreatedUtc        datetimeoffset(7) NOT NULL CONSTRAINT DF_RetentionPolicy_CreatedUtc DEFAULT(sysutcdatetime()),
        PolicyJson        nvarchar(max) NULL,
        CONSTRAINT UQ_RetentionPolicy_Name UNIQUE(PolicyName)
    );
END
GO

IF OBJECT_ID(N'ssv.PurgeRun', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.PurgeRun
    (
        PurgeRunId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurgeRun PRIMARY KEY,
        StartedUtc      datetimeoffset(7) NOT NULL CONSTRAINT DF_PurgeRun_StartedUtc DEFAULT(sysutcdatetime()),
        FinishedUtc     datetimeoffset(7) NULL,
        Status          nvarchar(40) NOT NULL CONSTRAINT DF_PurgeRun_Status DEFAULT(N'Running'),
        Actor           nvarchar(256) NULL,
        CandidateCount  int NOT NULL CONSTRAINT DF_PurgeRun_CandidateCount DEFAULT(0),
        PurgedCount     int NOT NULL CONSTRAINT DF_PurgeRun_PurgedCount DEFAULT(0),
        ErrorMessage    nvarchar(max) NULL
    );
END
GO

IF OBJECT_ID(N'ssv.PurgeRunItem', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.PurgeRunItem
    (
        PurgeRunItemId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurgeRunItem PRIMARY KEY,
        PurgeRunId      bigint NOT NULL,
        StorageObjectId bigint NULL,
        EntityType      nvarchar(80) NULL,
        EntityId        nvarchar(128) NULL,
        Action          nvarchar(80) NOT NULL,
        Status          nvarchar(40) NOT NULL,
        Message         nvarchar(1024) NULL,
        CreatedUtc      datetimeoffset(7) NOT NULL CONSTRAINT DF_PurgeRunItem_CreatedUtc DEFAULT(sysutcdatetime()),
        CONSTRAINT FK_PurgeRunItem_Run
            FOREIGN KEY(PurgeRunId) REFERENCES ssv.PurgeRun(PurgeRunId) ON DELETE CASCADE,
        CONSTRAINT FK_PurgeRunItem_StorageObject
            FOREIGN KEY(StorageObjectId) REFERENCES ssv.StorageObject(StorageObjectId)
    );
END
GO

IF OBJECT_ID(N'ssv.ExportPackage', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ExportPackage
    (
        ExportPackageId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExportPackage PRIMARY KEY,
        PackageType     nvarchar(80) NOT NULL,
        PackageStatus   nvarchar(40) NOT NULL CONSTRAINT DF_ExportPackage_Status DEFAULT(N'Created'),
        StorageUri      nvarchar(1024) NULL,
        Sha256Hash      varchar(64) NULL,
        CreatedBy       nvarchar(256) NULL,
        CreatedUtc      datetimeoffset(7) NOT NULL CONSTRAINT DF_ExportPackage_CreatedUtc DEFAULT(sysutcdatetime()),
        CompletedUtc    datetimeoffset(7) NULL,
        ManifestJson    nvarchar(max) NULL
    );
END
GO

CREATE OR ALTER VIEW ssv.vwApprovedReferenceImage
AS
SELECT
    s.ReferenceSetId,
    s.SignatureId,
    s.DisplayName,
    s.LanguageCode,
    subj.ExternalPartyId,
    subj.PartyName,
    i.ReferenceId,
    i.VersionNumber,
    i.SourceFileName,
    i.SourceFilePath,
    i.StorageUri,
    i.QualityStatus,
    i.QualityScore,
    i.EffectiveFromUtc,
    i.EffectiveToUtc
FROM ssv.ReferenceSetRegistry s
LEFT JOIN ssv.ReferenceSubject subj ON subj.ReferenceSubjectId = s.ReferenceSubjectId
JOIN ssv.ReferenceImageRegistry i ON i.ReferenceSetRegistryId = s.ReferenceSetRegistryId
WHERE
    s.Status = N'Approved'
    AND i.EnrollmentStatus = N'Approved'
    AND i.IsApprovedForMatching = 1
    AND (i.EffectiveFromUtc IS NULL OR i.EffectiveFromUtc <= sysutcdatetime())
    AND (i.EffectiveToUtc IS NULL OR i.EffectiveToUtc > sysutcdatetime());
GO

CREATE OR ALTER VIEW ssv.vwCaseManagementQueue
AS
SELECT
    rc.ReviewCaseId,
    rc.CaseNumber,
    rc.CaseStatus,
    rc.Priority,
    rc.AssignedTo,
    rc.DueUtc,
    rc.Branch,
    rc.CustomerId,
    rc.DocumentType,
    d.DocumentName,
    d.DocumentResultId,
    c.SignatureId,
    c.Decision,
    c.Confidence,
    c.ReferenceSetId,
    c.BestReferenceFileName,
    rc.CreatedUtc,
    rc.UpdatedUtc
FROM ssv.ReviewCase rc
LEFT JOIN ssv.VerificationDocument d ON d.DocumentResultId = rc.DocumentResultId
LEFT JOIN ssv.SignatureCase c ON c.SignatureCaseId = rc.SignatureCaseId
WHERE rc.CaseStatus NOT IN (N'Completed', N'Cancelled');
GO

MERGE ssv.SecurityRole AS target
USING (VALUES
    (N'Administrator', N'Can configure system settings, retention policies, templates, and users.'),
    (N'ReferenceApprover', N'Can approve, retire, and replace reference signatures.'),
    (N'Reviewer', N'Can review signature cases and record reviewer outcomes.'),
    (N'Auditor', N'Can inspect audit records and export governance evidence.'),
    (N'Verifier', N'Can submit verification requests through the production API.')
) AS source(RoleName, Description)
ON target.RoleName = source.RoleName
WHEN NOT MATCHED THEN INSERT(RoleName, Description) VALUES(source.RoleName, source.Description);
GO
