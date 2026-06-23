using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using StaticSignatureVerification.TotalAgilityWrapper;

const string DefaultInputFolder = @"C:\Temp\SignatureVerification\Input";
const string DefaultOutputFolder = @"C:\Temp\SignatureVerification\Output";
const string DefaultReferenceFolder = @"C:\Temp\SignatureVerification\ReferenceSignatures";

var arguments = args.Length == 0
    ? CliArguments.CreateLocalFolderDefaults(DefaultInputFolder, DefaultOutputFolder, DefaultReferenceFolder)
    : CliArguments.Parse(args);

if (arguments.Has("--help"))
{
    PrintUsage();
    return;
}

try
{
    if (arguments.Has("--localFolderMode"))
    {
        var inputDocuments = FileDiscovery.FindInputDocuments(DefaultInputFolder).ToList();
        if (inputDocuments.Count == 0)
        {
            throw new InvalidOperationException($"No PDF, image, or Base64 document files were found in {DefaultInputFolder}.");
        }

        Console.WriteLine($"Processing {inputDocuments.Count} document(s).");
        foreach (var documentPath in inputDocuments)
        {
            var documentArguments = arguments.Clone();
            SetDocumentPath(documentArguments, documentPath);
            ApplyDocumentCompanionFiles(documentArguments, DefaultInputFolder, documentPath);

            var outputFolder = Path.Combine(DefaultOutputFolder, FileDiscovery.MakeOutputFolderName(DefaultInputFolder, documentPath));
            documentArguments.Set("--outputJsonFile", Path.Combine(outputFolder, "result.json"));
            documentArguments.Set("--debugOutputFolder", Path.Combine(outputFolder, "debug"));

            ProcessOneDocument(documentArguments);
        }

        Console.WriteLine($"Input folder: {DefaultInputFolder}");
        Console.WriteLine($"Reference folder: {DefaultReferenceFolder}");
        Console.WriteLine($"Output folder: {DefaultOutputFolder}");
    }
    else
    {
        ProcessOneDocument(arguments);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static void ProcessOneDocument(CliArguments arguments)
{
    var documentBytes = ReadDocument(arguments);
    var documentBase64 = Convert.ToBase64String(documentBytes);
    var ocrJson = ReadOptionalText(arguments.Get("--ocrJsonFile"));
    var referencesJson = ReadReferencesJson(arguments);
    var optionsJson = MergeOptions(ReadOptionalText(arguments.Get("--optionsJsonFile")), arguments);

    var wrapper = new SignatureVerificationWrapper();
    var resultJson = wrapper.VerifySignatures(documentBase64, ocrJson, referencesJson, optionsJson);

    var output = arguments.Get("--outputJsonFile");
    if (string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(resultJson);
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
    File.WriteAllText(output, resultJson);
    Console.WriteLine($"Result JSON written to {output}");
}

static void SetDocumentPath(CliArguments arguments, string documentPath)
{
    arguments.Remove("--documentPdfFile");
    arguments.Remove("--documentImageFile");
    arguments.Remove("--documentBase64File");

    var extension = Path.GetExtension(documentPath);
    if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        arguments.Set("--documentPdfFile", documentPath);
    }
    else if (extension.Equals(".base64", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".b64", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(documentPath).Equals("documentBase64.txt", StringComparison.OrdinalIgnoreCase))
    {
        arguments.Set("--documentBase64File", documentPath);
    }
    else
    {
        arguments.Set("--documentImageFile", documentPath);
    }
}

static void ApplyDocumentCompanionFiles(CliArguments arguments, string inputRoot, string documentPath)
{
    arguments.Remove("--ocrJsonFile");
    var ocrJson = FileDiscovery.FindCompanionJson(inputRoot, documentPath, new[] { "ocr", "msdi", "layout" });
    if (!string.IsNullOrWhiteSpace(ocrJson))
    {
        arguments.Set("--ocrJsonFile", ocrJson);
    }
}

static byte[] ReadDocument(CliArguments arguments)
{
    var pdf = arguments.Get("--documentPdfFile");
    var image = arguments.Get("--documentImageFile");
    var base64 = arguments.Get("--documentBase64File");

    if (!string.IsNullOrWhiteSpace(pdf))
    {
        return File.ReadAllBytes(pdf);
    }

    if (!string.IsNullOrWhiteSpace(image))
    {
        return File.ReadAllBytes(image);
    }

    if (!string.IsNullOrWhiteSpace(base64))
    {
        return Convert.FromBase64String(File.ReadAllText(base64).Trim());
    }

    throw new InvalidOperationException("One of --documentPdfFile, --documentImageFile, or --documentBase64File is required.");
}

static string ReadOptionalText(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : File.ReadAllText(path);

static string ReadReferencesJson(CliArguments arguments)
{
    var referencesFile = arguments.Get("--referencesJsonFile");
    if (!string.IsNullOrWhiteSpace(referencesFile))
    {
        return ReadRequiredText(referencesFile, "--referencesJsonFile is required.");
    }

    var referencesFolder = arguments.Get("--referencesFolder");
    if (string.IsNullOrWhiteSpace(referencesFolder))
    {
        throw new InvalidOperationException("--referencesJsonFile or --referencesFolder is required.");
    }

    Directory.CreateDirectory(referencesFolder);
    var jsonFile = FileDiscovery.FindFirstFile(referencesFolder, new[] { "reference-signatures.json", "references.json", "*.json" });
    if (!string.IsNullOrWhiteSpace(jsonFile))
    {
        return File.ReadAllText(jsonFile);
    }

    var generated = BuildReferenceJsonFromImages(referencesFolder, GetDocumentPath(arguments));
    if (generated is null)
    {
        var documentName = Path.GetFileNameWithoutExtension(GetDocumentPath(arguments) ?? string.Empty);
        throw new InvalidOperationException($"No reference JSON or reference image file exactly matching '{documentName}' was found in {referencesFolder}.");
    }

    return generated;
}

static string ReadRequiredText(string? path, string error)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException(error);
    }

    return File.ReadAllText(path);
}

static string MergeOptions(string json, CliArguments arguments)
{
    var root = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

    if (arguments.Get("--debugOutputFolder") is { Length: > 0 } debugFolder)
    {
        root["debugOutputFolder"] = debugFolder;
        root["saveDebugImages"] = true;
    }

    if (arguments.Get("--pageIndex") is { Length: > 0 } pageIndex && int.TryParse(pageIndex, out var page))
    {
        root["pageIndex"] = page;
    }

    if (arguments.Get("--dpi") is { Length: > 0 } dpiText && int.TryParse(dpiText, out var dpi))
    {
        root["dpi"] = dpi;
        EnsureObject(root, "pdfRendering")["dpi"] = dpi;
    }

    if (arguments.Get("--maxPages") is { Length: > 0 } maxPagesText && int.TryParse(maxPagesText, out var maxPages))
    {
        EnsureObject(root, "pdfRendering")["maxPages"] = Math.Max(1, maxPages);
    }
    else if (arguments.Has("--localFolderMode"))
    {
        root["pageIndex"] = null;
        var pdfRendering = EnsureObject(root, "pdfRendering");
        var existingMaxPages = 0;
        if (pdfRendering["maxPages"] is JsonValue maxPagesNode && maxPagesNode.TryGetValue<int>(out var configuredMaxPages))
        {
            existingMaxPages = configuredMaxPages;
        }

        if (existingMaxPages < 100)
        {
            pdfRendering["maxPages"] = 100;
        }
    }

    if (arguments.Get("--ghostscriptPath") is { Length: > 0 } gs)
    {
        EnsureObject(root, "pdfRendering")["ghostscriptExecutablePath"] = FileDiscovery.CleanPath(gs);
    }

    return root.ToJsonString(CreateIndentedJsonOptions());
}

static string? BuildReferenceJsonFromImages(string referencesFolder, string? documentPath)
{
    var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff",
        ".bmp"
    };

    var documentName = Path.GetFileNameWithoutExtension(documentPath ?? string.Empty);
    if (string.IsNullOrWhiteSpace(documentName))
    {
        return null;
    }

    var sets = new JsonArray();
    var rootImages = Directory.EnumerateFiles(referencesFolder)
        .Where(path => imageExtensions.Contains(Path.GetExtension(path)))
        .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), documentName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (rootImages.Count > 0)
    {
        sets.Add(BuildReferenceSet("signature", "Signature", rootImages));
    }

    foreach (var folder in Directory.EnumerateDirectories(referencesFolder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
    {
        var images = Directory.EnumerateFiles(folder)
            .Where(path => imageExtensions.Contains(Path.GetExtension(path)))
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), documentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (images.Count == 0)
        {
            continue;
        }

        var signatureId = MakeId(Path.GetFileName(folder));
        sets.Add(BuildReferenceSet(signatureId, Path.GetFileName(folder), images));
    }

    if (sets.Count == 0)
    {
        return null;
    }

    var root = new JsonObject
    {
        ["referenceSets"] = sets
    };
    return root.ToJsonString(CreateIndentedJsonOptions());
}

static JsonSerializerOptions CreateIndentedJsonOptions() => new(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    WriteIndented = true
};

static string? GetDocumentPath(CliArguments arguments) =>
    arguments.Get("--documentPdfFile") ??
    arguments.Get("--documentImageFile") ??
    arguments.Get("--documentBase64File");

static JsonObject BuildReferenceSet(string signatureId, string displayName, IReadOnlyList<string> images)
{
    var referenceImages = new JsonArray();
    for (var i = 0; i < images.Count; i++)
    {
        referenceImages.Add(new JsonObject
        {
            ["referenceId"] = $"{signatureId}_ref_{i + 1}",
            ["sourceFileName"] = Path.GetFileName(images[i]),
            ["sourceFilePath"] = images[i],
            ["imageBase64"] = Convert.ToBase64String(File.ReadAllBytes(images[i]))
        });
    }

    return new JsonObject
    {
        ["signatureId"] = signatureId,
        ["displayName"] = displayName,
        ["expectedLabels"] = BuildExpectedLabels(signatureId),
        ["referenceImages"] = referenceImages
    };
}

static string MakeId(string value)
{
    var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
    var id = new string(chars).Trim('_');
    while (id.Contains("__", StringComparison.Ordinal))
    {
        id = id.Replace("__", "_", StringComparison.Ordinal);
    }

    var compact = new string(id.Where(char.IsLetterOrDigit).ToArray());
    if (compact.Contains("applicant", StringComparison.OrdinalIgnoreCase) ||
        compact.Contains("customer", StringComparison.OrdinalIgnoreCase))
    {
        return "applicant_signature";
    }

    if (compact.Contains("witness", StringComparison.OrdinalIgnoreCase))
    {
        return "witness_signature";
    }

    if (compact.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
        compact.Contains("authorised", StringComparison.OrdinalIgnoreCase) ||
        compact.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        compact.Contains("authorisation", StringComparison.OrdinalIgnoreCase))
    {
        return "authorised_signature";
    }

    return string.IsNullOrWhiteSpace(id) ? "signature" : id;
}

static JsonArray BuildExpectedLabels(string signatureId)
{
    var compact = new string(signatureId.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    var labels = compact switch
    {
        var value when value.Contains("applicant", StringComparison.OrdinalIgnoreCase) || value.Contains("customer", StringComparison.OrdinalIgnoreCase) =>
            new[] { "Applicant Signature", "Customer Signature", "Signed By" },
        var value when value.Contains("witness", StringComparison.OrdinalIgnoreCase) =>
            new[] { "Witness Signature", "Witness" },
        var value when value.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("authorised", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("authorisation", StringComparison.OrdinalIgnoreCase) =>
            new[] { "Authorised Signature", "Authorized Signature", "Authorised By", "Authorized By" },
        _ => new[] { "Signature", "Signed By", "Applicant Signature", "Customer Signature" }
    };

    var array = new JsonArray();
    foreach (var label in labels)
    {
        array.Add(label);
    }

    return array;
}

static JsonObject EnsureObject(JsonObject root, string name)
{
    if (root[name] is JsonObject existing)
    {
        return existing;
    }

    var created = new JsonObject();
    root[name] = created;
    return created;
}

static void PrintUsage()
{
    Console.WriteLine("""
StaticSignatureVerification.ConsoleTest

Required:
  --referencesJsonFile <path>
  one of --documentPdfFile <path>, --documentImageFile <path>, --documentBase64File <path>

Optional:
  --ocrJsonFile <path>
  --optionsJsonFile <path>
  --outputJsonFile <path>
  --referencesFolder <path>
  --debugOutputFolder <path>
  --ghostscriptPath <path>
  --pageIndex <number>
  --maxPages <number>
  --dpi <number>

No-argument local folder mode:
  Input:               C:\Temp\SignatureVerification\Input
  Output:              C:\Temp\SignatureVerification\Output
  ReferenceSignatures: C:\Temp\SignatureVerification\ReferenceSignatures

Put PDF/image files anywhere under Input. Local folder mode searches recursively.
For PDFs, local folder mode searches up to 100 PDF pages by default.
It clears fixed pageIndex values so back pages are searched too.
Optionally add options.json and OCR/MSDI JSON in Input.
Put reference-signatures.json in ReferenceSignatures for advanced/manual mapping.
Otherwise, reference image filenames must exactly match the input document filename.
Example: Input\Customer123.pdf uses ReferenceSignatures\Customer123.png.
Subfolders under ReferenceSignatures become separate signature sets, but the image
filename must still exactly match the input document filename.
For PDF tests, put ghostscript-path.txt in Input or C:\Temp\SignatureVerification,
or let the app auto-detect Ghostscript under C:\Program Files\gs.
""");
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            parsed._values[key] = value;
        }

        return parsed;
    }

    public static CliArguments CreateLocalFolderDefaults(string inputFolder, string outputFolder, string referenceFolder)
    {
        Directory.CreateDirectory(inputFolder);
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(referenceFolder);

        var parsed = new CliArguments();
        parsed.Set("--localFolderMode", "true");
        parsed.Set("--referencesFolder", referenceFolder);
        parsed.Set("--outputJsonFile", Path.Combine(outputFolder, "result.json"));
        parsed.Set("--debugOutputFolder", Path.Combine(outputFolder, "debug"));

        var optionsFile = FileDiscovery.FindFirstFile(inputFolder, new[] { "options.json", "*options*.json" });
        if (!string.IsNullOrWhiteSpace(optionsFile))
        {
            parsed.Set("--optionsJsonFile", optionsFile);
        }

        var rootFolder = Directory.GetParent(inputFolder)?.FullName ?? inputFolder;
        var gsPathFile = new[]
            {
                Path.Combine(inputFolder, "ghostscript-path.txt"),
                Path.Combine(rootFolder, "ghostscript-path.txt")
            }
            .FirstOrDefault(File.Exists);

        if (!string.IsNullOrWhiteSpace(gsPathFile))
        {
            var gsPath = FileDiscovery.CleanPath(File.ReadAllText(gsPathFile));
            if (!string.IsNullOrWhiteSpace(gsPath))
            {
                parsed.Set("--ghostscriptPath", gsPath);
            }
        }
        else if (FileDiscovery.TryFindGhostscript() is { Length: > 0 } detectedGhostscript)
        {
            parsed.Set("--ghostscriptPath", detectedGhostscript);
        }

        return parsed;
    }

    public void Set(string key, string value) => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public CliArguments Clone()
    {
        var clone = new CliArguments();
        foreach (var item in _values)
        {
            clone._values[item.Key] = item.Value;
        }

        return clone;
    }
}

internal static class FileDiscovery
{
    public static string CleanPath(string value) => value.Trim().Trim('"').Trim('\'').Trim();

    public static string? FindFirstFile(string folder, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }

    public static IEnumerable<string> FindInputDocuments(string folder)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg",
            ".tif",
            ".tiff",
            ".bmp",
            ".base64",
            ".b64"
        };

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                if (fileName.Equals("documentBase64.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (fileName.Equals("options.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("ghostscript-path.txt", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("ocr", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("msdi", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("layout", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return supported.Contains(Path.GetExtension(path));
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    public static string MakeOutputFolderName(string inputRoot, string documentPath)
    {
        var relativePath = Path.GetRelativePath(inputRoot, documentPath);
        var withoutExtension = Path.Combine(
            Path.GetDirectoryName(relativePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(relativePath));
        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }).ToHashSet();
        var safeChars = withoutExtension.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
        var safe = new string(safeChars).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? Path.GetFileNameWithoutExtension(documentPath) : safe;
    }

    public static string? FindCompanionJson(string inputRoot, string documentPath, IReadOnlyList<string> purposeTokens)
    {
        var directory = Path.GetDirectoryName(documentPath) ?? inputRoot;
        var fileName = Path.GetFileName(documentPath);
        var stem = Path.GetFileNameWithoutExtension(documentPath);
        var candidates = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(inputRoot, "*.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
            {
                var jsonName = Path.GetFileName(path);
                if (jsonName.Equals("options.json", StringComparison.OrdinalIgnoreCase) ||
                    jsonName.Equals("reference-signatures.json", StringComparison.OrdinalIgnoreCase) ||
                    jsonName.Equals("references.json", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var jsonStem = Path.GetFileNameWithoutExtension(path);
                var nameMatchesDocument = jsonStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                                          jsonStem.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                                          jsonStem.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ||
                                          jsonStem.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) ||
                                          jsonStem.StartsWith(fileName + ".", StringComparison.OrdinalIgnoreCase);
                if (!nameMatchesDocument)
                {
                    return false;
                }

                return purposeTokens.Count == 0 ||
                       purposeTokens.Any(token => jsonName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                       jsonStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                       jsonStem.Equals(fileName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetDirectoryName(path)?.Equals(directory, StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        return candidates;
    }

    public static string? TryFindGhostscript()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "gs")
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "gswin64c.exe", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, "gswin32c.exe", SearchOption.AllDirectories)))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
