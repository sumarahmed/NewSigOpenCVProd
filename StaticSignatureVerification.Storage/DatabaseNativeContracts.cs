namespace StaticSignatureVerification.Storage;

public enum SignatureStorageMode
{
    Hybrid,
    Database
}

public interface IDatabaseNativeStore
{
    Task SetStorageModeAsync(SignatureStorageMode mode, string actor, CancellationToken cancellationToken = default);
    Task<SignatureStorageMode> GetStorageModeAsync(CancellationToken cancellationToken = default);
    Task<long> SaveDocumentBlobAsync(DocumentBlobRecord blob, CancellationToken cancellationToken = default);
    Task LinkDocumentBlobToResultAsync(string documentBlobKey, string documentResultId, CancellationToken cancellationToken = default);
    Task<long> SaveReferenceImageBlobAsync(long referenceImageRegistryId, string contentType, byte[] bytes, CancellationToken cancellationToken = default);
    Task<string> BuildReferenceSignaturesJsonAsync(IReadOnlyList<DbNativeReferenceRequest> requests, CancellationToken cancellationToken = default);
    Task SaveDebugArtifactBlobsAsync(string documentResultId, CancellationToken cancellationToken = default);
}

public sealed record DocumentBlobRecord(
    string DocumentBlobKey,
    string? CorrelationId,
    string? DocumentName,
    string? FileName,
    string ContentType,
    byte[] ContentBytes,
    string? CreatedBy,
    DateTimeOffset? RetainUntilUtc,
    string? MetadataJson);

public sealed record DbNativeReferenceRequest(
    string SignatureId,
    string? ReferenceSetId,
    string? PartyId);
