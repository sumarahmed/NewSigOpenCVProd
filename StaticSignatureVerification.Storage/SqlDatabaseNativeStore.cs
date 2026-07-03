using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StaticSignatureVerification.Storage;

public sealed class SqlDatabaseNativeStore : IDatabaseNativeStore
{
    private readonly string _connectionString;

    public SqlDatabaseNativeStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task SetStorageModeAsync(SignatureStorageMode mode, string actor, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await NonQueryAsync(connection, """
MERGE ssv.SystemSetting AS target
USING (SELECT N'StorageMode' AS SettingKey) AS source
ON target.SettingKey = source.SettingKey
WHEN MATCHED THEN UPDATE SET SettingValue = @Mode, UpdatedBy = @Actor, UpdatedUtc = sysutcdatetime()
WHEN NOT MATCHED THEN INSERT(SettingKey, SettingValue, UpdatedBy) VALUES(N'StorageMode', @Mode, @Actor);
""", command =>
            {
                AddString(command, "@Mode", mode.ToString());
                AddString(command, "@Actor", actor);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to update storage mode setting to '{mode}'.", ex);
        }
    }

    public async Task<SignatureStorageMode> GetStorageModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var value = Convert.ToString(await ScalarAsync(connection, "SELECT SettingValue FROM ssv.SystemSetting WHERE SettingKey = N'StorageMode';", _ => { }, cancellationToken).ConfigureAwait(false));
            return Enum.TryParse<SignatureStorageMode>(value, true, out var mode) ? mode : SignatureStorageMode.Hybrid;
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException("Failed to read storage mode setting.", ex);
        }
    }

    public async Task<long> SaveDocumentBlobAsync(DocumentBlobRecord blob, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var hash = Sha256(blob.ContentBytes);
            return Convert.ToInt64(await ScalarAsync(connection, """
MERGE ssv.DocumentBlob AS target
USING (SELECT @DocumentBlobKey AS DocumentBlobKey) AS source
ON target.DocumentBlobKey = source.DocumentBlobKey
WHEN MATCHED THEN UPDATE SET
    CorrelationId = @CorrelationId,
    DocumentName = @DocumentName,
    FileName = @FileName,
    ContentType = @ContentType,
    Sha256Hash = @Sha256Hash,
    SizeBytes = @SizeBytes,
    ContentBytes = @ContentBytes,
    MetadataJson = @MetadataJson
WHEN NOT MATCHED THEN INSERT
    (DocumentBlobKey, CorrelationId, DocumentName, FileName, ContentType, Sha256Hash, SizeBytes, ContentBytes, CreatedBy, RetainUntilUtc, MetadataJson)
VALUES
    (@DocumentBlobKey, @CorrelationId, @DocumentName, @FileName, @ContentType, @Sha256Hash, @SizeBytes, @ContentBytes, @CreatedBy, @RetainUntilUtc, @MetadataJson);
SELECT DocumentBlobId FROM ssv.DocumentBlob WHERE DocumentBlobKey = @DocumentBlobKey;
""", command =>
            {
                AddString(command, "@DocumentBlobKey", blob.DocumentBlobKey);
                AddString(command, "@CorrelationId", blob.CorrelationId);
                AddString(command, "@DocumentName", blob.DocumentName);
                AddString(command, "@FileName", blob.FileName);
                AddString(command, "@ContentType", blob.ContentType);
                AddString(command, "@Sha256Hash", hash);
                AddLong(command, "@SizeBytes", blob.ContentBytes.LongLength);
                AddBytes(command, "@ContentBytes", blob.ContentBytes);
                AddString(command, "@CreatedBy", blob.CreatedBy);
                AddDate(command, "@RetainUntilUtc", blob.RetainUntilUtc);
                AddString(command, "@MetadataJson", blob.MetadataJson);
            }, cancellationToken).ConfigureAwait(false));
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to save document blob '{blob.DocumentBlobKey}'.", ex);
        }
    }

    public async Task LinkDocumentBlobToResultAsync(string documentBlobKey, string documentResultId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await NonQueryAsync(connection, "UPDATE ssv.DocumentBlob SET DocumentResultId = @DocumentResultId WHERE DocumentBlobKey = @DocumentBlobKey;", command =>
            {
                AddString(command, "@DocumentResultId", documentResultId);
                AddString(command, "@DocumentBlobKey", documentBlobKey);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to link document blob '{documentBlobKey}' to result '{documentResultId}'.", ex);
        }
    }

    public async Task<long> SaveReferenceImageBlobAsync(long referenceImageRegistryId, string contentType, byte[] bytes, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(await ScalarAsync(connection, """
MERGE ssv.ReferenceImageBlob AS target
USING (SELECT @ReferenceImageRegistryId AS ReferenceImageRegistryId) AS source
ON target.ReferenceImageRegistryId = source.ReferenceImageRegistryId
WHEN MATCHED THEN UPDATE SET
    ContentType = @ContentType,
    Sha256Hash = @Sha256Hash,
    SizeBytes = @SizeBytes,
    ContentBytes = @ContentBytes
WHEN NOT MATCHED THEN INSERT
    (ReferenceImageRegistryId, ContentType, Sha256Hash, SizeBytes, ContentBytes)
VALUES
    (@ReferenceImageRegistryId, @ContentType, @Sha256Hash, @SizeBytes, @ContentBytes);
SELECT ReferenceImageBlobId FROM ssv.ReferenceImageBlob WHERE ReferenceImageRegistryId = @ReferenceImageRegistryId;
""", command =>
            {
                AddLong(command, "@ReferenceImageRegistryId", referenceImageRegistryId);
                AddString(command, "@ContentType", contentType);
                AddString(command, "@Sha256Hash", Sha256(bytes));
                AddLong(command, "@SizeBytes", bytes.LongLength);
                AddBytes(command, "@ContentBytes", bytes);
            }, cancellationToken).ConfigureAwait(false));
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to save reference image blob for reference image registry id {referenceImageRegistryId}.", ex);
        }
    }

    public async Task<string> BuildReferenceSignaturesJsonAsync(IReadOnlyList<DbNativeReferenceRequest> requests, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var referenceSets = new JsonArray();
            foreach (var request in requests)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
SELECT ReferenceSetId, SignatureId, DisplayName, ExternalPartyId, PartyName, ReferenceId, SourceFileName, ContentBytes
FROM ssv.vwApprovedReferenceImageNative
WHERE SignatureId = @SignatureId
  AND (@ReferenceSetId IS NULL OR ReferenceSetId = @ReferenceSetId)
  AND (@PartyId IS NULL OR ExternalPartyId = @PartyId)
ORDER BY VersionNumber DESC, ReferenceId;
""";
                AddString(command, "@SignatureId", request.SignatureId);
                AddString(command, "@ReferenceSetId", request.ReferenceSetId);
                AddString(command, "@PartyId", request.PartyId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                JsonObject? set = null;
                var images = new JsonArray();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    set ??= new JsonObject
                    {
                        ["referenceSetId"] = reader.GetString(0),
                        ["signatureId"] = reader.GetString(1),
                        ["displayName"] = NullableString(reader, 2) ?? reader.GetString(1),
                        ["partyId"] = NullableString(reader, 3),
                        ["partyName"] = NullableString(reader, 4),
                        ["expectedSignerId"] = reader.GetString(0),
                        ["expectedLabels"] = new JsonArray(reader.GetString(1), NullableString(reader, 2) ?? reader.GetString(1)),
                        ["referenceImages"] = images
                    };
                    var bytes = (byte[])reader["ContentBytes"];
                    images.Add(new JsonObject
                    {
                        ["referenceId"] = reader.GetString(5),
                        ["sourceFileName"] = NullableString(reader, 6) ?? reader.GetString(5) + ".png",
                        ["sourceFilePath"] = "sql://ssv.ReferenceImageBlob/" + reader.GetString(5),
                        ["imageBase64"] = Convert.ToBase64String(bytes)
                    });
                }

                if (set is null)
                {
                    throw new InvalidOperationException($"No approved DB-native reference was found for signatureId '{request.SignatureId}', referenceSetId '{request.ReferenceSetId}', partyId '{request.PartyId}'.");
                }

                referenceSets.Add(set);
            }

            return new JsonObject { ["referenceSets"] = referenceSets }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException("Failed to build DB-native reference signatures JSON.", ex);
        }
    }

    public async Task SaveDebugArtifactBlobsAsync(string documentResultId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var artifacts = new List<(long DebugArtifactId, long SignatureCaseId, string Type, string Path)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
SELECT da.DebugArtifactId, da.SignatureCaseId, da.ArtifactType, da.ArtifactPath
FROM ssv.DebugArtifact da
JOIN ssv.SignatureCase sc ON sc.SignatureCaseId = da.SignatureCaseId
WHERE sc.DocumentResultId = @DocumentResultId;
""";
                AddString(command, "@DocumentResultId", documentResultId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    artifacts.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
                }
            }

            foreach (var artifact in artifacts.Where(item => File.Exists(item.Path)))
            {
                var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
                await NonQueryAsync(connection, """
IF NOT EXISTS (SELECT 1 FROM ssv.DebugArtifactBlob WHERE DebugArtifactId = @DebugArtifactId)
BEGIN
    INSERT INTO ssv.DebugArtifactBlob
        (DebugArtifactId, SignatureCaseId, DocumentResultId, ArtifactType, ContentType, Sha256Hash, SizeBytes, ContentBytes)
    VALUES
        (@DebugArtifactId, @SignatureCaseId, @DocumentResultId, @ArtifactType, N'image/png', @Sha256Hash, @SizeBytes, @ContentBytes);
END
UPDATE ssv.DebugArtifact
SET ArtifactPath = @DbUri
WHERE DebugArtifactId = @DebugArtifactId;
""", command =>
                {
                    AddLong(command, "@DebugArtifactId", artifact.DebugArtifactId);
                    AddLong(command, "@SignatureCaseId", artifact.SignatureCaseId);
                    AddString(command, "@DocumentResultId", documentResultId);
                    AddString(command, "@ArtifactType", artifact.Type);
                    AddString(command, "@Sha256Hash", Sha256(bytes));
                    AddLong(command, "@SizeBytes", bytes.LongLength);
                    AddBytes(command, "@ContentBytes", bytes);
                    AddString(command, "@DbUri", "sql://ssv.DebugArtifactBlob/debug-artifact/" + artifact.DebugArtifactId);
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to save debug artifact blobs for document result '{documentResultId}'.", ex);
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        await TransientSqlRetry.OpenConnectionAsync(_connectionString, cancellationToken).ConfigureAwait(false);

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, Action<SqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task NonQueryAsync(SqlConnection connection, string sql, Action<SqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        parameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddString(SqlCommand command, string name, string? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.NVarChar);
        parameter.Size = -1;
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static void AddLong(SqlCommand command, string name, long value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.BigInt);
        parameter.Value = value;
    }

    private static void AddBytes(SqlCommand command, string name, byte[] value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.VarBinary);
        parameter.Size = -1;
        parameter.Value = value;
    }

    private static void AddDate(SqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTimeOffset);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? NullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
