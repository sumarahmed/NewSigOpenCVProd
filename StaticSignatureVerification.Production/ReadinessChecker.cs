using StaticSignatureVerification.Storage;

namespace StaticSignatureVerification.Production;

public sealed record ReadinessCheck(string Name, string Status, string Message);

public static class ReadinessChecker
{
    public static IReadOnlyList<ReadinessCheck> Run(string inputFolder, string outputFolder, string referenceFolder, string? ghostscriptPath, string? connectionString)
    {
        var checks = new List<ReadinessCheck>
        {
            Folder("Input folder", inputFolder, mustExist: false),
            Folder("Output folder", outputFolder, mustExist: false),
            Folder("Reference folder", referenceFolder, mustExist: false),
            Ghostscript(ghostscriptPath),
            Database(connectionString)
        };

        return checks;
    }

    private static ReadinessCheck Folder(string name, string path, bool mustExist)
    {
        try
        {
            if (mustExist && !Directory.Exists(path))
            {
                return new ReadinessCheck(name, "Fail", "Folder does not exist: " + path);
            }

            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new ReadinessCheck(name, "Pass", path);
        }
        catch (Exception ex)
        {
            return new ReadinessCheck(name, "Fail", ex.Message);
        }
    }

    private static ReadinessCheck Ghostscript(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = FindGhostscript();
        }

        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? new ReadinessCheck("Ghostscript", "Pass", path)
            : new ReadinessCheck("Ghostscript", "Fail", "Ghostscript executable was not found.");
    }

    private static ReadinessCheck Database(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ReadinessCheck("Database", "Warn", "No database connection string configured.");
        }

        try
        {
            var store = new SqlProductionWorkflowStore(connectionString);
            store.LogAuditEventAsync(new AuditEventRecord("ReadinessCheck", "Information", null, "ReadinessChecker", "System", null, "Database readiness check succeeded.", null)).GetAwaiter().GetResult();
            return new ReadinessCheck("Database", "Pass", "Connected and wrote audit event.");
        }
        catch (Exception ex)
        {
            return new ReadinessCheck("Database", "Fail", ex.Message);
        }
    }

    private static string? FindGhostscript()
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
