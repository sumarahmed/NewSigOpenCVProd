namespace StaticSignatureVerification.Core;

public sealed class SignatureVerificationRuntimeOptions
{
    public string RootFolder { get; set; } = @"C:\Temp\SignatureVerification";
    public string? DatabaseConnectionString { get; set; }
    public string? GhostscriptExecutablePath { get; set; }
    public string StorageMode { get; set; } = "Hybrid";

    public string InputFolder => Path.Combine(RootFolder, "Input");
    public string OutputFolder => Path.Combine(RootFolder, "Output");
    public string ReferenceFolder => Path.Combine(RootFolder, "ReferenceSignatures");
    public string BusinessReportsFolder => Path.Combine(RootFolder, "BusinessReports");
    public string ReferenceStoreFolder => Path.Combine(RootFolder, "ReferenceStore");
}

public interface ISignatureVerificationConfigService
{
    SignatureVerificationRuntimeOptions GetRuntimeOptions();
}

public sealed class EnvironmentSignatureVerificationConfigService : ISignatureVerificationConfigService
{
    public const string RootFolderVariable = "SIGNATURE_VERIFICATION_ROOT";
    public const string DatabaseConnectionVariable = "SIGNATURE_VERIFICATION_DB_CONNECTION";
    public const string GhostscriptVariable = "SIGNATURE_GHOSTSCRIPT_PATH";
    public const string StorageModeVariable = "SIGNATURE_STORAGE_MODE";

    public SignatureVerificationRuntimeOptions GetRuntimeOptions() => new()
    {
        RootFolder = Environment.GetEnvironmentVariable(RootFolderVariable) ?? @"C:\Temp\SignatureVerification",
        DatabaseConnectionString = Environment.GetEnvironmentVariable(DatabaseConnectionVariable),
        GhostscriptExecutablePath = Environment.GetEnvironmentVariable(GhostscriptVariable),
        StorageMode = Environment.GetEnvironmentVariable(StorageModeVariable) ?? "Hybrid"
    };
}

