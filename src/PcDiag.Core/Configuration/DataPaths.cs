namespace PcDiag.Core.Configuration;

/// <summary>
/// Resolves every on-disk location the application uses. All paths derive from one validated data directory;
/// no path is ever taken from untrusted input.
/// </summary>
public sealed class DataPaths
{
    /// <summary>Folder name under <c>%LOCALAPPDATA%</c> used when no data directory is configured.</summary>
    public const string DefaultFolderName = "PcDiagnostic";

    /// <summary>Creates the paths for a configured data directory (null or empty uses the default).</summary>
    /// <exception cref="ArgumentException">The directory is not an acceptable local path.</exception>
    public DataPaths(string? dataDirectory)
    {
        if (ValidateDirectory(dataDirectory) is { } error)
        {
            throw new ArgumentException(error, nameof(dataDirectory));
        }

        Root = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultFolderName)
            : Path.GetFullPath(dataDirectory);
    }

    /// <summary>The data directory.</summary>
    public string Root { get; }

    /// <summary>Rolling telemetry database (disposable; rebuilt if corrupt).</summary>
    public string TelemetryDatabase => Path.Combine(Root, "telemetry.db");

    /// <summary>Incident index database (precious; never deleted automatically).</summary>
    public string IncidentIndexDatabase => Path.Combine(Root, "incidents.db");

    /// <summary>Folder holding one sub-folder per incident.</summary>
    public string IncidentsDirectory => Path.Combine(Root, "incidents");

    /// <summary>Log folder.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>User settings file (overrides the shipped defaults).</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Optional user event classification rules (override the shipped rules by id).</summary>
    public string EventRulesFile => Path.Combine(Root, "event-rules.json");

    /// <summary>The user's dashboard layout and widget settings (written by the Web host).</summary>
    public string DashboardFile => Path.Combine(Root, "dashboard.json");

    /// <summary>The readings shown on the screens (written by the Web host, read by the recorder).</summary>
    public string OverlayFile => Path.Combine(Root, "overlay.json");

    /// <summary>Background pictures of the on-screen panels, by id (see <c>OverlayStyles.ImagePattern</c>).</summary>
    public string OverlayImagesDirectory => Path.Combine(Root, "overlay-images");

    /// <summary>
    /// Screen driver packages, one folder each, and their approvals (approved.json): under ProgramData, writable by
    /// administrators only (the recorder makes it so), readable by the screen host that loads them.
    /// </summary>
    public static string ScreenDriversDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcDiagnostic", "screen-drivers");

    /// <summary>
    /// Driver packages (screens, sensors), one folder each, and their approvals (approved.json): under ProgramData,
    /// writable by administrators only (the recorder makes it so, and checks it each time it starts), readable by the
    /// screen host that loads them. The screen drivers' folder of earlier versions is moved here.
    /// </summary>
    public static string DriversDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcDiagnostic", "drivers");

    /// <summary>Where imported driver packages are copied and checked before they join the drivers (administrators only).</summary>
    public static string DriverStagingDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcDiagnostic", "driver-staging");

    /// <summary>
    /// Windows driver packages (an INF with its files) imported for devices without a driver, one folder each, and the
    /// drivers installed from them (installed.json): administrators only.
    /// </summary>
    public static string WindowsDriversDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcDiagnostic", "windows-drivers");

    /// <summary>Driver packages uploaded in the dashboard, until the recorder has taken its copy (written by the Web host).</summary>
    public string DriverUploadsDirectory => Path.Combine(Root, "driver-uploads");

    /// <summary>The services the recorder paused to use a USB screen (started again if the recorder stopped before it could).</summary>
    public string ScreenHoldersFile => Path.Combine(Root, "screen-holders.json");

    /// <summary>The hardware the user expects (written by the Web host, read by the recorder).</summary>
    public string ExpectedHardwareFile => Path.Combine(Root, "expected-hardware.json");

    /// <summary>The recent clean-ups of the storage manager (written by the recorder).</summary>
    public string StorageLogFile => Path.Combine(Root, "storage-log.json");

    /// <summary>Written by the Web host: its current port and process ID.</summary>
    public string WebStateFile => Path.Combine(Root, "web.json");

    /// <summary>Devices paired for access from the local network (hashed tokens only).</summary>
    public string PairedDevicesFile => Path.Combine(Root, "devices.json");

    /// <summary>The HTTPS certificate for access from the local network (private key protected for the user).</summary>
    public string LanCertificateFile => Path.Combine(Root, "lan-certificate.bin");

    /// <summary>Finished period diagnostics (written by the Web host; the most recent are kept).</summary>
    public string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    /// <summary>Problem reports for the makers of PC Diagnostic (outbox and sent).</summary>
    public string ReportsDirectory => Path.Combine(Root, "reports");

    /// <summary>One-time dashboard launch tickets written by the Agent's tray (see <see cref="Ipc.LaunchTickets"/>).</summary>
    public string LaunchDirectory => Path.Combine(Root, "launch");

    /// <summary>The folder for one incident. The folder name is generated from the key, never from input.</summary>
    public string GetIncidentDirectory(Incidents.IncidentKey key) => Path.Combine(IncidentsDirectory, key.ToString());

    /// <summary>Creates the data, incidents and logs folders if they do not exist.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(IncidentsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Checks a configured data directory. Returns null when acceptable, otherwise a description of the problem.
    /// Null or empty is acceptable (the default is used).
    /// </summary>
    public static string? ValidateDirectory(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return null;
        }

        if (dataDirectory.StartsWith(@"\\", StringComparison.Ordinal) || dataDirectory.StartsWith("//", StringComparison.Ordinal))
        {
            return $"Storage:DataDirectory must be a local path; UNC and device paths are not allowed (was '{dataDirectory}').";
        }

        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            return $"Storage:DataDirectory must be a fully qualified path such as 'D:\\PcDiagnostic' (was '{dataDirectory}').";
        }

        if (dataDirectory.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return $"Storage:DataDirectory contains invalid characters (was '{dataDirectory}').";
        }

        return null;
    }
}
