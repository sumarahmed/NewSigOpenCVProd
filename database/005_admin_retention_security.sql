SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'ssv.ApiKeyRegistry', N'U') IS NULL
BEGIN
    CREATE TABLE ssv.ApiKeyRegistry
    (
        ApiKeyId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApiKeyRegistry PRIMARY KEY,
        KeyName      nvarchar(128) NOT NULL,
        KeyHash      varchar(64) NOT NULL,
        RolesCsv     nvarchar(512) NOT NULL,
        IsActive     bit NOT NULL CONSTRAINT DF_ApiKeyRegistry_IsActive DEFAULT(1),
        CreatedBy    nvarchar(256) NULL,
        CreatedUtc   datetimeoffset(7) NOT NULL CONSTRAINT DF_ApiKeyRegistry_CreatedUtc DEFAULT(sysutcdatetime()),
        ExpiresUtc   datetimeoffset(7) NULL,
        LastUsedUtc  datetimeoffset(7) NULL,
        RevokedUtc   datetimeoffset(7) NULL,
        Notes        nvarchar(max) NULL,
        CONSTRAINT UQ_ApiKeyRegistry_KeyName UNIQUE(KeyName),
        CONSTRAINT UQ_ApiKeyRegistry_KeyHash UNIQUE(KeyHash)
    );
END
GO

MERGE ssv.RetentionPolicy AS target
USING (VALUES
    (N'DefaultStorageObjectRetention', N'StorageObject', 2555, N'system', N'{"installedDefault":true,"scope":"filesystem metadata and optional file delete"}'),
    (N'DefaultDocumentBlobRetention', N'DocumentBlob', 2555, N'system', N'{"installedDefault":true,"scope":"database-native submitted documents"}'),
    (N'DefaultDebugArtifactBlobRetention', N'DebugArtifactBlob', 90, N'system', N'{"installedDefault":true,"scope":"database-native debug artifacts"}'),
    (N'DefaultReportBlobRetention', N'ReportBlob', 2555, N'system', N'{"installedDefault":true,"scope":"database-native generated reports"}')
) AS source(PolicyName, TargetObjectType, RetentionDays, CreatedBy, PolicyJson)
ON target.PolicyName = source.PolicyName
WHEN NOT MATCHED THEN
    INSERT(PolicyName, TargetObjectType, RetentionDays, IsActive, CreatedBy, PolicyJson)
    VALUES(source.PolicyName, source.TargetObjectType, source.RetentionDays, 1, source.CreatedBy, source.PolicyJson);
GO

MERGE ssv.SystemSetting AS target
USING (VALUES
    (N'FilePurgeEnabled', N'false', N'system'),
    (N'DatabaseBlobPurgeEnabled', N'true', N'system'),
    (N'BackupExportFolder', N'', N'system')
) AS source(SettingKey, SettingValue, UpdatedBy)
ON target.SettingKey = source.SettingKey
WHEN NOT MATCHED THEN
    INSERT(SettingKey, SettingValue, UpdatedBy)
    VALUES(source.SettingKey, source.SettingValue, source.UpdatedBy);
GO
