using System.Reflection;

namespace PcDiag.Hosting;

/// <summary>Product name and version, read from assembly metadata.</summary>
public static class ProductInfo
{
    /// <summary>Product display name.</summary>
    public const string Name = "PC Diagnostic & Incident Recorder";

    /// <summary>Informational version of the running build (without source-control metadata).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(ProductInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
