using System.Diagnostics;
using StaticSignatureVerification.Core;

namespace StaticSignatureVerification.PdfRendering;

/// <summary>
/// Renders PDF pages by invoking an installed Ghostscript executable.
/// </summary>
public sealed class GhostscriptCommandLinePdfRenderer : IPdfPageRenderer
{
    /// <summary>
    /// Renders selected PDF pages to PNG image bytes using an externally configured Ghostscript executable.
    /// </summary>
    public IReadOnlyList<RenderedPage> RenderPdfToImages(byte[] pdfBytes, PdfRenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GhostscriptExecutablePath))
        {
            throw new SignatureVerificationException("GHOSTSCRIPT_PATH_MISSING", "Ghostscript executable path is required for PDF rendering.");
        }

        var executable = Path.GetFullPath(options.GhostscriptExecutablePath);
        if (!File.Exists(executable) || !Path.GetFileName(executable).Contains("gs", StringComparison.OrdinalIgnoreCase))
        {
            throw new SignatureVerificationException("GHOSTSCRIPT_PATH_MISSING", "The configured Ghostscript executable path was not found or did not look like a Ghostscript executable.");
        }

        var rootTemp = CreateTempRoot(options.TempFolder);
        var workFolder = Path.Combine(rootTemp, "ssv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        var inputPath = Path.Combine(workFolder, "input.pdf");
        var outputPattern = Path.Combine(workFolder, "page_%03d.png");

        try
        {
            File.WriteAllBytes(inputPath, pdfBytes);
            var startPage = options.PageIndex.HasValue ? options.PageIndex.Value + 1 : 1;
            var endPage = options.PageIndex.HasValue ? options.PageIndex.Value + 1 : Math.Max(1, options.MaxPages);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = workFolder
            };

            psi.ArgumentList.Add("-dSAFER");
            psi.ArgumentList.Add("-dBATCH");
            psi.ArgumentList.Add("-dNOPAUSE");
            psi.ArgumentList.Add("-dQUIET");
            psi.ArgumentList.Add(options.RenderGrayscale ? "-sDEVICE=pnggray" : "-sDEVICE=png16m");
            psi.ArgumentList.Add($"-r{Math.Clamp(options.Dpi, 72, 600)}");
            psi.ArgumentList.Add($"-dFirstPage={startPage}");
            psi.ArgumentList.Add($"-dLastPage={endPage}");
            psi.ArgumentList.Add("-sOutputFile=" + outputPattern);
            psi.ArgumentList.Add(inputPath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new SignatureVerificationException("PDF_RENDERING_FAILED", "Ghostscript could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var timeoutMs = Math.Max(1, options.TimeoutSeconds) * 1000;
            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                throw new SignatureVerificationException("PDF_RENDERING_FAILED", "PDF rendering timed out.");
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5));
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            if (process.ExitCode != 0)
            {
                throw new SignatureVerificationException("PDF_RENDERING_FAILED", SafeGhostscriptMessage(stderr));
            }

            var rendered = Directory.GetFiles(workFolder, "page_*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, options.MaxPages))
                .Select((path, index) =>
                {
                    var bytes = File.ReadAllBytes(path);
                    var pageIndex = options.PageIndex ?? index;
                    return new RenderedPage
                    {
                        PageIndex = pageIndex,
                        Dpi = options.Dpi,
                        ImageBytes = bytes,
                        ImageFormat = "png"
                    };
                })
                .ToList();

            if (rendered.Count == 0)
            {
                throw new SignatureVerificationException("PDF_RENDERING_FAILED", "Ghostscript completed but did not produce any page images.");
            }

            return rendered;
        }
        catch (SignatureVerificationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SignatureVerificationException("PDF_RENDERING_FAILED", "The PDF could not be rendered. It may be encrypted, corrupt, or unreadable.");
        }
        finally
        {
            if (!options.KeepTempFiles)
            {
                TryDelete(workFolder);
            }
        }
    }

    private static string CreateTempRoot(string? configured)
    {
        var root = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : Path.GetFullPath(configured);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string SafeGhostscriptMessage(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "Ghostscript failed to render the PDF.";
        }

        var oneLine = stderr.ReplaceLineEndings(" ").Trim();
        return oneLine.Length > 220 ? oneLine[..220] : oneLine;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup failure must not break verification.
        }
    }
}
