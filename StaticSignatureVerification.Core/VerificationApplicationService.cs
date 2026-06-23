namespace StaticSignatureVerification.Core;

public sealed class VerificationApplicationService : IVerificationApplicationService
{
    private readonly StaticSignatureVerificationEngine _engine;

    public VerificationApplicationService(
        StaticSignatureVerificationEngine? engine = null,
        IVerificationLogger? logger = null,
        VerificationProfiles? profiles = null)
    {
        _engine = engine ?? new StaticSignatureVerificationEngine(logger: logger, profiles: profiles);
    }

    public VerificationResult Verify(SignatureVerificationRequest request) =>
        _engine.Verify(
            request.DocumentBase64,
            request.OcrLayoutJson,
            request.ReferenceSignaturesJson,
            request.OptionsJson,
            request.PdfRenderer);

    public string VerifyToJson(SignatureVerificationRequest request) =>
        StaticSignatureVerificationEngine.ToJson(Verify(request));
}

public sealed class ResultBuilder : IResultBuilder
{
    public VerificationResult CreateEmpty(string engineVersion) =>
        ResultJsonBuilder.CreateEmpty(engineVersion);

    public VerificationResult CreateError(string engineVersion, string code, string message) =>
        ResultJsonBuilder.CreateError(engineVersion, code, message);
}

