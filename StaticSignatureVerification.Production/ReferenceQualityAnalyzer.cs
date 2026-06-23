using OpenCvSharp;
using System.Security.Cryptography;
using System.Text.Json;

namespace StaticSignatureVerification.Production;

public sealed record ReferenceQualityResult(
    string Status,
    double Score,
    bool Passed,
    int Width,
    int Height,
    double InkDensityPercent,
    double Sharpness,
    string AverageHash,
    string[] Warnings)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
}

public static class ReferenceQualityAnalyzer
{
    public static ReferenceQualityResult Analyze(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Reference image was not found.", imagePath);
        }

        using var image = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (image.Empty())
        {
            throw new InvalidOperationException("Reference image could not be decoded.");
        }

        return Analyze(image);
    }

    public static ReferenceQualityResult Analyze(byte[] imageBytes)
    {
        using var image = Cv2.ImDecode(imageBytes, ImreadModes.Grayscale);
        if (image.Empty())
        {
            throw new InvalidOperationException("Reference image could not be decoded.");
        }

        return Analyze(image);
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ReferenceQualityResult Analyze(Mat gray)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 245, 255, ThresholdTypes.BinaryInv);
        var inkPixels = Cv2.CountNonZero(binary);
        var totalPixels = Math.Max(1, gray.Width * gray.Height);
        var density = inkPixels * 100.0 / totalPixels;

        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        var sharpness = stddev.Val0 * stddev.Val0;

        var warnings = new List<string>();
        if (gray.Width < 120 || gray.Height < 45) warnings.Add("TOO_SMALL");
        if (density < 0.15) warnings.Add("TOO_BLANK");
        if (density > 45) warnings.Add("TOO_DARK");
        if (sharpness < 12) warnings.Add("BLURRY");

        var score = 100.0;
        if (gray.Width < 120) score -= 20;
        if (gray.Height < 45) score -= 20;
        if (density < 0.15) score -= 40;
        if (density > 45) score -= 30;
        if (sharpness < 12) score -= 25;
        score = Math.Clamp(score, 0, 100);
        var passed = score >= 60 && warnings.Count == 0;

        return new ReferenceQualityResult(
            Status: passed ? "Pass" : "Fail",
            Score: Math.Round(score, 2),
            Passed: passed,
            Width: gray.Width,
            Height: gray.Height,
            InkDensityPercent: Math.Round(density, 4),
            Sharpness: Math.Round(sharpness, 4),
            AverageHash: AverageHash(gray),
            Warnings: warnings.ToArray());
    }

    private static string AverageHash(Mat gray)
    {
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(16, 16), 0, 0, InterpolationFlags.Area);
        var mean = Cv2.Mean(resized).Val0;
        var chars = new char[256];
        var index = 0;
        for (var y = 0; y < resized.Rows; y++)
        {
            for (var x = 0; x < resized.Cols; x++)
            {
                chars[index++] = resized.At<byte>(y, x) >= mean ? '1' : '0';
            }
        }

        return new string(chars);
    }
}
