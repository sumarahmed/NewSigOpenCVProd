SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'ssv.ReferenceSetRegistry', N'UpdatedBy') IS NULL
BEGIN
    ALTER TABLE ssv.ReferenceSetRegistry ADD UpdatedBy nvarchar(256) NULL;
END
GO

IF COL_LENGTH(N'ssv.ReferenceSetRegistry', N'UpdatedUtc') IS NULL
BEGIN
    ALTER TABLE ssv.ReferenceSetRegistry
        ADD UpdatedUtc datetimeoffset(7) NOT NULL
            CONSTRAINT DF_ReferenceSetRegistry_UpdatedUtc DEFAULT(sysutcdatetime());
END
GO
