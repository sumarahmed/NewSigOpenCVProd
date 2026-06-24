SET XACT_ABORT ON;
GO

MERGE ssv.SecurityRole AS target
USING (VALUES
    (N'Verifier', N'Can submit verification requests through the production API.')
) AS source(RoleName, Description)
ON target.RoleName = source.RoleName
WHEN NOT MATCHED THEN
    INSERT(RoleName, Description)
    VALUES(source.RoleName, source.Description);
GO
