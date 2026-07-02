SET XACT_ABORT ON;
GO

-- Registers a retention policy for ssv.VerificationDocument (and everything that cascades
-- from it on delete: SignatureCase, ReferenceComparison, DebugArtifact, ReviewerOutcome,
-- ReviewCase, ResultGovernanceSnapshot). Previously no policy existed for this data at all,
-- so it was retained indefinitely regardless of every other retention/purge setting.
--
-- IsActive is deliberately 0. This table is the primary verification audit record and may be
-- subject to compliance retention obligations that differ per deployment (see docs/SQL-Database.md).
-- An administrator must review the required retention window with Legal/Compliance and explicitly
-- activate this policy (or install a different RetentionDays value) via the Admin UI or
-- POST /api/v1/admin/retention before it will ever be purged.
MERGE ssv.RetentionPolicy AS target
USING (VALUES
    (N'DefaultVerificationDocumentRetention', N'VerificationDocument', 2555, N'system',
     N'{"installedDefault":true,"scope":"case/document audit metadata; cascades to SignatureCase, ReferenceComparison, DebugArtifact, ReviewerOutcome, ReviewCase, ResultGovernanceSnapshot","requiresComplianceReviewBeforeActivation":true}')
) AS source(PolicyName, TargetObjectType, RetentionDays, CreatedBy, PolicyJson)
ON target.PolicyName = source.PolicyName
WHEN NOT MATCHED THEN
    INSERT(PolicyName, TargetObjectType, RetentionDays, IsActive, CreatedBy, PolicyJson)
    VALUES(source.PolicyName, source.TargetObjectType, source.RetentionDays, 0, source.CreatedBy, source.PolicyJson);
GO
