using System.Diagnostics;

namespace PcDiag.Web.Reports;

/// <summary>
/// Prints a report to PDF with Microsoft Edge in headless mode (ARCHITECTURE §11: the only program the web host
/// starts). Edge comes from its installed location, the arguments are fixed, the input is a generated file in a
/// private temporary folder with a throwaway browser profile, and the folder is deleted afterwards. The report has
/// no script and fetches nothing, so Edge only lays it out.
/// </summary>
public sealed partial class PdfPrinter(ILogger<PdfPrinter> logger)
{
    /// <summary>Longest a print may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Edge, when installed.</summary>
    public static string? EdgePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The PDF, or null when Edge is not available or could not print (the reason is logged).</summary>
    public async Task<byte[]?> PrintAsync(string html, CancellationToken cancellationToken)
    {
        if (EdgePath() is not { } edge)
        {
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), "pcdiag-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "report.html");
            var output = Path.Combine(folder, "report.pdf");
            await File.WriteAllTextAsync(input, html, cancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[]
                     {
                         "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
                         "--disable-background-networking", "--no-pdf-header-footer", $"--user-data-dir={Path.Combine(folder, "profile")}",
                         $"--print-to-pdf={output}", new Uri(input).AbsoluteUri,
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Edge did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (!File.Exists(output))
            {
                LogFailed(logger, process.ExitCode);
                return null;
            }

            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DeleteAsync(folder).ConfigureAwait(false);
        }
    }

    private static async Task DeleteAsync(string folder)
    {
        // Edge's helper processes can hold the profile a moment after exit.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Edge did not produce a PDF (exit code {ExitCode})")]
    private static partial void LogFailed(ILogger logger, int exitCode);
}
