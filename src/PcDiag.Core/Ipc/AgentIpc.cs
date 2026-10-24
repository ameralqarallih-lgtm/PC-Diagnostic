using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PcDiag.Core.Control;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;

namespace PcDiag.Core.Ipc;

/// <summary>The fixed command set the Agent accepts. There is no generic "run" command and nothing takes a path.</summary>
public static class AgentCommands
{
    /// <summary>Recorder and incident status.</summary>
    public const string Status = "status";

    /// <summary>Start recording (also ends a pause).</summary>
    public const string Start = "start";

    /// <summary>Stop recording.</summary>
    public const string Stop = "stop";

    /// <summary>Pause for <see cref="AgentRequestArgs.PauseMinutes"/>, or until resumed when it is null.</summary>
    public const string Pause = "pause";

    /// <summary>End a pause.</summary>
    public const string Resume = "resume";

    /// <summary>Capture an incident (optional quick type and note).</summary>
    public const string Capture = "capture";

    /// <summary>The latest live values (read-only). Also keeps the in-memory live view running while the recorder is idle.</summary>
    public const string Live = "live";

    /// <summary>Recent trend of the headline values (read-only), <see cref="AgentRequestArgs.TrendMinutes"/> long.</summary>
    public const string Trend = "trend";

    /// <summary>Edit an incident: <see cref="AgentRequestArgs.IncidentKey"/> plus title, note (the notes), type or pinned.</summary>
    public const string UpdateIncident = "incident-update";

    /// <summary>Delete an incident (<see cref="AgentRequestArgs.IncidentKey"/>).</summary>
    public const string DeleteIncident = "incident-delete";

    /// <summary>The storage manager's state: the drive's free space, the condition, recent clean-ups.</summary>
    public const string StorageState = "storage-state";

    /// <summary>Apply the storage limits now (<see cref="AgentRequestArgs.Compact"/>: also compact the recording database).</summary>
    public const string StorageClean = "storage-clean";

    /// <summary>The newest check of the hardware against what is expected.</summary>
    public const string HardwareState = "hardware-state";

    /// <summary>Check the hardware now (after the expected setup changed).</summary>
    public const string HardwareCheck = "hardware-check";

    /// <summary>What a custom recording can select on this PC (read-only; samples briefly if nothing was sampled yet).</summary>
    public const string Catalog = "recording-catalog";

    /// <summary>Running processes with what can be done with them (read-only; keeps the live view sampling).</summary>
    public const string ControlProcesses = "control-processes";

    /// <summary>Windows services with what can be done with them (read-only).</summary>
    public const string ControlServices = "control-services";

    /// <summary>
    /// End a process, its tree, or set its priority: <see cref="AgentRequestArgs.Pid"/> with
    /// <see cref="AgentRequestArgs.ProcessStart"/>, <see cref="AgentRequestArgs.ControlAction"/> "end", "end-tree" or "priority".
    /// </summary>
    public const string ProcessAction = "process-action";

    /// <summary>Start, stop or restart a service or change how it starts (<see cref="AgentRequestArgs.ServiceName"/>, <see cref="AgentRequestArgs.ControlAction"/>).</summary>
    public const string ServiceAction = "service-action";

    /// <summary>What the tune-up can clean and the performance settings as they are (read-only).</summary>
    public const string TuneUpScan = "tuneup-scan";

    /// <summary>Run one of <see cref="TuneUpActions"/> (<see cref="AgentRequestArgs.ControlAction"/>, with items and a value).</summary>
    public const string TuneUpApply = "tuneup-apply";

    /// <summary>What each optimization step would do now.</summary>
    public const string TuneUpPlan = "tuneup-plan";

    /// <summary>The files and folders of a clean-up target's folder (Value: target id; Items: [root index, path]).</summary>
    public const string CleanupFiles = "cleanup-files";

    /// <summary>The on-screen display: its monitors and the values it can show (sampling starts, like the live view).</summary>
    public const string OverlayState = "overlay-state";

    /// <summary>Reads overlay.json again.</summary>
    public const string OverlayReload = "overlay-reload";

    /// <summary>Shows each monitor's number on it.</summary>
    public const string OverlayIdentify = "overlay-identify";

    /// <summary>Starts (<see cref="AgentRequestArgs.On"/> true) or ends arranging the panels on the screens themselves.</summary>
    public const string OverlayArrange = "overlay-arrange";

    /// <summary>The USB screens' driver packages (overlay-screen-drivers).</summary>
    public const string OverlayScreenDrivers = "overlay-screen-drivers";

    /// <summary>Approves a driver package by its id and hash, or withdraws it (<see cref="AgentRequestArgs.On"/>).</summary>
    public const string OverlayScreenDriverApprove = "overlay-screen-driver-approve";

    /// <summary>Imports a driver package uploaded to the drivers' uploads folder (Args.Value: the file).</summary>
    public const string DriverImport = "driver-import";

    /// <summary>Removes a driver package (Args.DriverId).</summary>
    public const string DriverRemove = "driver-remove";

    /// <summary>The devices without a driver and the Windows driver packages imported for them.</summary>
    public const string WindowsDrivers = "windows-drivers";

    /// <summary>Imports a Windows driver package (Args.Value: the uploaded zip).</summary>
    public const string WindowsDriverImport = "windows-driver-import";

    /// <summary>Installs a package (Args.DriverId) for a device (Args.Value: its instance id; Args.On: a restore point first), after a confirmation on the PC.</summary>
    public const string WindowsDriverInstall = "windows-driver-install";

    /// <summary>Removes a package (Args.DriverId), uninstalling it from Windows first when it was installed here.</summary>
    public const string WindowsDriverRemove = "windows-driver-remove";

    /// <summary>What the recorder cost in CPU over the last minute, by part.</summary>
    public const string RecorderCost = "recorder-cost";

    /// <summary>All commands.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Status, Start, Stop, Pause, Resume, Capture, Live, Trend, UpdateIncident, DeleteIncident, Catalog,
        ControlProcesses, ControlServices, ProcessAction, ServiceAction, TuneUpScan, TuneUpApply, TuneUpPlan, CleanupFiles, OverlayState, OverlayReload, OverlayIdentify, OverlayArrange, OverlayScreenDrivers, OverlayScreenDriverApprove,
        StorageState, StorageClean, HardwareState, HardwareCheck, RecorderCost, DriverImport, DriverRemove,
        WindowsDrivers, WindowsDriverImport, WindowsDriverInstall, WindowsDriverRemove,
    };
}

/// <summary>A request to the Agent.</summary>
/// <param name="Version">Protocol version (<see cref="AgentIpc.ProtocolVersion"/>).</param>
/// <param name="Id">Caller-chosen ID echoed in the response (at most 64 characters).</param>
/// <param name="Command">One of <see cref="AgentCommands"/>.</param>
/// <param name="Args">Command arguments.</param>
public sealed record AgentRequest(int Version, string Id, string Command, AgentRequestArgs? Args = null);

/// <summary>Command arguments (all optional).</summary>
/// <param name="PauseMinutes">Pause length (1–1440); null pauses until resumed.</param>
/// <param name="IncidentType">Quick type for a capture.</param>
/// <param name="Note">Note for a capture (at most 2000 characters).</param>
/// <param name="TrendMinutes">Trend length (1-10).</param>
/// <param name="IncidentKey">The incident to edit or delete.</param>
/// <param name="Title">New incident title (at most 120 characters).</param>
/// <param name="Pinned">Pin or unpin the incident.</param>
/// <param name="Selection">A custom recording's items.</param>
/// <param name="Pid">A process to act on.</param>
/// <param name="ProcessStart">Its creation time (UTC ticks), so a reused PID is never hit.</param>
/// <param name="ControlAction">What to do with the process or service, or which tune-up action.</param>
/// <param name="Priority">A process priority class.</param>
/// <param name="ServiceName">A service to act on.</param>
/// <param name="StartType">How a service should start.</param>
/// <param name="Items">The tune-up action's items (clean-up ids, processes, services, a startup entry).</param>
/// <param name="Value">The tune-up action's value ("on", "off", a power plan…).</param>
/// <param name="Confirmed">The user confirmed an action on a part of Windows.</param>
/// <param name="Compact">With storage-clean: also compact the recording database.</param>
public sealed record AgentRequestArgs(
    int? PauseMinutes = null,
    UserIncidentType? IncidentType = null,
    string? Note = null,
    int? TrendMinutes = null,
    string? IncidentKey = null,
    string? Title = null,
    bool? Pinned = null,
    IReadOnlyList<string>? Selection = null,
    int? Pid = null,
    long? ProcessStart = null,
    string? ControlAction = null,
    string? Priority = null,
    string? ServiceName = null,
    string? StartType = null,
    IReadOnlyList<string>? Items = null,
    string? Value = null,
    bool? Confirmed = null,
    bool? Compact = null,
    bool? On = null,
    string? DriverId = null,
    string? Sha256 = null,
    bool? Safe = null);

/// <summary>The Agent's answer.</summary>
/// <param name="Id">The request ID.</param>
/// <param name="Ok">Whether the command succeeded.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Status">Status after the command.</param>
/// <param name="Capture">Capture result (capture command).</param>
/// <param name="Live">Live values (live command).</param>
/// <param name="Trend">Recent trend (trend command).</param>
/// <param name="Processes">Running processes (control-processes).</param>
/// <param name="Services">Services (control-services).</param>
/// <param name="TuneUp">The tune-up report (tuneup-scan).</param>
/// <param name="Result">The outcome of a process, service or tune-up action.</param>
/// <param name="Overlay">The on-screen display (overlay-state).</param>
/// <param name="Storage">The storage manager's state (storage-state).</param>
/// <param name="Cleaned">What a clean-up did (storage-clean).</param>
/// <param name="Hardware">The hardware check (hardware-state, hardware-check).</param>
/// <param name="ScreenDrivers">The USB screens' driver packages (overlay-screen-drivers).</param>
/// <param name="Cost">The recorder's CPU over the last minute, by part (recorder-cost).</param>
/// <param name="WindowsDrivers">Devices without a driver and Windows driver packages (windows-driver-*).</param>
public sealed record AgentResponse(
    string Id, bool Ok, string? Error = null, AgentStatus? Status = null, AgentCaptureResult? Capture = null, LiveSnapshot? Live = null, LiveTrend? Trend = null,
    RecordingCatalog? Catalog = null, IReadOnlyList<ControlProcess>? Processes = null, IReadOnlyList<ControlService>? Services = null, TuneUpReport? TuneUp = null,
    ControlResult? Result = null, Overlay.OverlayState? Overlay = null, Storage.StorageState? Storage = null, Storage.StorageCleanResult? Cleaned = null,
    Devices.HardwareStatus? Hardware = null, IReadOnlyList<Overlay.ScreenDriverInfo>? ScreenDrivers = null, Diagnostics.RecorderCost? Cost = null, Devices.WindowsDriversState? WindowsDrivers = null,
    Control.OptimizePlan? Plan = null, Control.CleanupListing? CleanupFiles = null);

/// <summary>Recorder and incident status.</summary>
/// <param name="Version">Agent version.</param>
/// <param name="Elevated">Whether the Agent runs elevated.</param>
/// <param name="Mode">Recording mode.</param>
/// <param name="State">Recorder state.</param>
/// <param name="PausedUntil">End of the pause (<see cref="DateTimeOffset.MaxValue"/> = until resumed).</param>
/// <param name="ActiveIncident">The incident still recording its post-window.</param>
/// <param name="ActiveIncidentUntil">When that post-window ends.</param>
/// <param name="LiveView">Sampling for the dashboard only (in memory, nothing is recorded) because the recorder is idle.</param>
/// <param name="Enhanced">What Enhanced mode is doing; null when it is off.</param>
/// <param name="RecordingItems">How many items a custom recording selects.</param>
/// <param name="Storage">A storage problem (the drive almost full, the space limit exceeded); null when all is well.</param>
/// <param name="Hardware">Expected hardware that is missing; null when everything expected is there.</param>
/// <param name="Watching">Not recording, but watching for problems in memory: a problem starts a recording.</param>
public sealed record AgentStatus(
    string Version,
    bool Elevated,
    RecordingMode Mode,
    RecorderState State,
    DateTimeOffset? PausedUntil,
    string? ActiveIncident,
    DateTimeOffset? ActiveIncidentUntil,
    bool LiveView = false,
    string? Enhanced = null,
    int? RecordingItems = null,
    Storage.StorageNotice? Storage = null,
    Devices.HardwareNotice? Hardware = null,
    bool Watching = false);

/// <summary>Result of a capture.</summary>
/// <param name="IncidentKey">The incident, e.g. INC-20261006-00001.</param>
/// <param name="Merged">Added to an incident that was still recording.</param>
/// <param name="SnapshotOnly">The recorder was not recording: no pre-window.</param>
/// <param name="RecordingUntil">When the post-window ends.</param>
public sealed record AgentCaptureResult(string IncidentKey, bool Merged, bool SnapshotOnly, DateTimeOffset RecordingUntil);

/// <summary>Serialization metadata generated at compile time (no reflection-based or polymorphic deserialization).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
public sealed partial class AgentIpcJsonContext : JsonSerializerContext;

/// <summary>
/// Framing for the Agent pipe: each message is a 4-byte little-endian length followed by that many bytes of UTF-8
/// JSON. Requests are at most <see cref="MaxMessageBytes"/>; responses, which can carry a trend, at most
/// <see cref="MaxResponseBytes"/>. One request gets one response, in order.
/// </summary>
public static class AgentIpc
{
    /// <summary>Current protocol version.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Largest accepted request (what the Agent reads from a client).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Largest accepted response (what a client reads from the Agent).</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>The pipe name for a user: one Agent pipe per Windows account.</summary>
    public static string PipeName(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!userSid.All(c => char.IsAsciiDigit(c) || c is 'S' or '-'))
        {
            throw new ArgumentException("Not a SID.", nameof(userSid));
        }

        return "PcDiagnostic.Agent." + userSid;
    }

    /// <summary>Writes one message.</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, int maxBytes = MaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        if (payload.Length > maxBytes)
        {
            throw new InvalidDataException($"Message of {payload.Length} bytes exceeds the {maxBytes}-byte limit.");
        }

        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length);
        payload.CopyTo(buffer, 4);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null when the other side closed the connection before a new message.</summary>
    /// <exception cref="InvalidDataException">The length is out of range or the JSON is not a valid message.</exception>
    public static async Task<T?> ReadAsync<T>(Stream stream, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, int maxBytes = MaxMessageBytes)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
        {
            throw new InvalidDataException($"Message length {length} is outside 1–{maxBytes} bytes.");
        }

        var payload = new byte[length];
        if (!await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The connection closed in the middle of a message.");
        }

        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo) ?? throw new InvalidDataException("Empty message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The message is not valid JSON for this protocol.", ex);
        }
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new InvalidDataException("The connection closed in the middle of a message.");
            }

            read += n;
        }

        return true;
    }
}

/// <summary>
/// A client for the Agent pipe (used by the Web host and tests). <see cref="PipeOptions.CurrentUserOnly"/> makes
/// Windows check that the pipe belongs to the current user, so another account cannot impersonate the Agent.
/// </summary>
public sealed class AgentIpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private long _nextId;

    private AgentIpcClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Connects to the Agent pipe.</summary>
    /// <exception cref="TimeoutException">No Agent answered within <paramref name="timeout"/>.</exception>
    public static async Task<AgentIpcClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new AgentIpcClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a command and returns the Agent's answer.</summary>
    public async Task<AgentResponse> SendAsync(string command, AgentRequestArgs? args, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await AgentIpc.WriteAsync(_pipe, new AgentRequest(AgentIpc.ProtocolVersion, id, command, args), AgentIpcJsonContext.Default.AgentRequest, cancellationToken).ConfigureAwait(false);
        var response = await AgentIpc.ReadAsync(_pipe, AgentIpcJsonContext.Default.AgentResponse, cancellationToken, AgentIpc.MaxResponseBytes).ConfigureAwait(false)
            ?? throw new IOException("The Agent closed the connection.");
        return response.Id == id ? response : throw new InvalidDataException("The response does not match the request.");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
