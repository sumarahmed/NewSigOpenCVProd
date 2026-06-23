using OpenCvSharp;

namespace StaticSignatureVerification.Core;

public interface IVerificationApplicationService
{
    VerificationResult Verify(SignatureVerificationRequest request);
    string VerifyToJson(SignatureVerificationRequest request);
}

public sealed record SignatureVerificationRequest(
    string DocumentBase64,
    string? OcrLayoutJson,
    string ReferenceSignaturesJson,
    string? OptionsJson,
    IPdfPageRenderer? PdfRenderer = null);

public interface IDocumentInputDetector
{
    InputDocumentType Detect(byte[] bytes, string? requestedType);
}

public interface IOcrLayoutParser
{
    OcrLayout Parse(string? json, IReadOnlyList<PageImage> renderedPages, List<string> warnings);
}

public interface ISignatureDetector
{
    List<DetectedCandidate> DetectCandidates(IReadOnlyList<PageImage> pages, ReferenceSignatureSet set, OcrLayout ocr, SignatureVerificationOptions options);
}

public interface ISignaturePreprocessor
{
    ProcessedSignature Preprocess(Mat gray, SignatureVerificationOptions options, string debugName);
}

public interface IFeatureExtractor
{
    SignatureMetrics Extract(ProcessedSignature processed, Mat binary, double cropConfidence);
}

public interface ISimilarityScorer
{
    ReferenceComparison Compare(SignatureMetrics query, ProcessedSignature queryImage, SignatureMetrics reference, ReferenceFeature referenceImage, SignatureVerificationOptions options);
}

public interface IDecisionEngine
{
    SignatureDecision GetSignatureDecision(double confidence, SignatureMetrics metrics, SignatureVerificationOptions options);
    string GetOverallDecision(IReadOnlyList<SignatureResult> results);
}

public interface IReasoningBuilder
{
    string Build(string decision, ReferenceComparison? comparison);
}

public interface IDebugImageWriter
{
    Dictionary<string, string>? TryWrite(
        SignatureVerificationOptions options,
        IReadOnlyList<PageImage> pages,
        string signatureId,
        DetectedCandidate candidate,
        ProcessedSignature processed,
        IReadOnlyList<DetectedRegion> allCandidates,
        List<string> warnings);
}

public interface IResultBuilder
{
    VerificationResult CreateEmpty(string engineVersion);
    VerificationResult CreateError(string engineVersion, string code, string message);
}

