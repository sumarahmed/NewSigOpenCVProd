using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StaticSignatureVerification.Production;

public sealed record StoredArtifact(string StorageUri, string Sha256Hash, long SizeBytes, bool IsEncrypted);

public static class SecureArtifactStore
{
    public static StoredArtifact StoreReferenceImage(byte[] bytes, string rootFolder, string referenceSetId, string referenceId, bool encrypt)
    {
        Directory.CreateDirectory(rootFolder);
        var safeSet = SafeName(referenceSetId);
        var safeRef = SafeName(referenceId);
        var folder = Path.Combine(rootFolder, safeSet);
        Directory.CreateDirectory(folder);
        var extension = encrypt ? ".bin" : ".png";
        var path = Path.Combine(folder, safeRef + "_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..8] + extension);
        var written = encrypt ? Protect(bytes) : bytes;
        File.WriteAllBytes(path, written);
        return new StoredArtifact(path, ReferenceQualityAnalyzer.Sha256Hex(bytes), written.LongLength, encrypt);
    }

    public static string CreateZipExport(string outputFolder, string packageType, IReadOnlyDictionary<string, string> files, object manifest)
    {
        Directory.CreateDirectory(outputFolder);
        var zipPath = Path.Combine(outputFolder, $"{SafeName(packageType)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            if (File.Exists(file.Value))
            {
                archive.CreateEntryFromFile(file.Value, file.Key, CompressionLevel.Optimal);
            }
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(manifestEntry.Open());
        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return zipPath;
    }

    private static byte[] Protect(byte[] bytes) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Encrypted local artifact storage uses Windows DPAPI.");

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "artifact" : safe;
    }
}
