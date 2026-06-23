namespace StaticSignatureVerification.Core;

public sealed class DetectionProfile
{
    public double KnownZoneSourceScore { get; set; } = 100;
    public double OcrBelowLabelSourceScore { get; set; } = 85;
    public double OcrRightOfLabelSourceScore { get; set; } = 78;
    public double BoxDetectionSourceScore { get; set; } = 70;
    public double InkRegionSourceScore { get; set; } = 55;
    public double DuplicateCandidateOverlapThreshold { get; set; } = 0.85;
    public double UsedRegionOverlapThreshold { get; set; } = 0.25;
    public double CandidateRegionConfidenceWeight { get; set; } = 0.05;
    public double ReusedRegionPenalty { get; set; } = 25;
    public int CannyLowThreshold { get; set; } = 60;
    public int CannyHighThreshold { get; set; } = 180;
    public int InkRegionJoinKernelWidth { get; set; } = 18;
    public int InkRegionJoinKernelHeight { get; set; } = 8;
}

public sealed class PreprocessingProfile
{
    public int MedianBlurKernelSize { get; set; } = 3;
    public double MinimumDeskewDegrees { get; set; } = 0.5;
    public double MaximumDeskewDegrees { get; set; } = 10;
    public int InkCropPaddingPixels { get; set; } = 12;
    public double NormalizedCanvasWidthUsage { get; set; } = 0.90;
    public double NormalizedCanvasHeightUsage { get; set; } = 0.85;
}

public sealed class ScoringProfile
{
    public double StructuralMismatchGeometryLimit { get; set; } = 55;
    public double StructuralMismatchContourLimit { get; set; } = 62;
    public double StructuralMismatchComponentLimit { get; set; } = 65;
    public double StructuralMismatchPenalty { get; set; } = 8;
    public double PixelShiftMinimumOverlap { get; set; } = 0.75;
    public int[] PixelShiftOffsets { get; set; } = new[] { -12, -6, 0, 6, 12 };
}

public sealed class VerificationProfiles
{
    public DetectionProfile Detection { get; set; } = new();
    public PreprocessingProfile Preprocessing { get; set; } = new();
    public ScoringProfile Scoring { get; set; } = new();
}

