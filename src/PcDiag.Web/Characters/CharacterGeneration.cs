using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Web.Endpoints;

namespace PcDiag.Web.Characters;

/// <summary>Where characters are generated: nowhere, the generator on this PC, or another service.</summary>
/// <param name="Kind">off, internal (tools/character-gen/serve.py on this PC) or external (a URL, maybe with a key).</param>
public sealed record GeneratorSettings(string Kind = "off", string? Url = null, bool HasKey = false, string? Python = null, string? Folder = null, string? Kit = null);

/// <summary>A character being generated, followed in the background: its stills to choose from, then the character.</summary>
/// <param name="Type">stills (candidates for a prompt) or character (one still animated).</param>
/// <param name="State">queued, running, done or failed.</param>
/// <param name="Stills">How many stills a done stills job has (served by number).</param>
/// <param name="Character">The library id of a done character job (it is imported by itself).</param>
/// <param name="Log">What the generator said last about it (its steps' output), newest last.</param>
public sealed record GenerationJob(
    string Id,
    string Type,
    string Prompt,
    string Kind,
    string? Name,
    string State,
    string Step,
    double Progress,
    string? Error,
    DateTimeOffset Created,
    int Stills = 0,
    string? Character = null,
    string? From = null,
    IReadOnlyList<string>? Log = null);

/// <summary>
/// Character generation, on behalf of the editor: the generator's settings (its key kept with DPAPI), jobs started on
/// the generator and followed in the background (so a generation carries on with the editor closed), stills kept on
/// this PC, and each finished character added to the picture library like an imported one.
/// </summary>
public sealed partial class CharacterGeneration(DataPaths paths, IHttpClientFactory http, PcDiag.Storage.Files.FileStore store, ILogger<CharacterGeneration> logger) : BackgroundService
{
    /// <summary>The internal generator (tools/character-gen/serve.py) on this PC.</summary>
    public const string InternalUrl = "http://127.0.0.1:7865";
    /// <summary>The HTTP client calling the generator.</summary>
    public const string HttpClientName = "character-generator";
    private static readonly byte[] Entropy = "PcDiag.CharacterGenerator"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    private string Folder => Path.Combine(paths.Root, "characters");

    private sealed record Stored(string Kind = "off", string? Url = null, string? Key = null, List<StoredJob>? Jobs = null, string? Python = null, string? Folder = null, string? Kit = null, int? Pid = null, DateTimeOffset? PidStarted = null);

    /// <summary>The generator's output (started here), in the logs folder.</summary>
    private string LogFile => Path.Combine(paths.Root, "logs", "character-generator.log");

    /// <summary>Why the generator started here ended (its exit code and last words), until it is started again.</summary>
    private volatile string? _ended;

    /// <summary>
    /// The generator process started here, also by an earlier run of PC Diagnostic (its id and start time are kept), while
    /// it runs: a generator already running is recognised, not started twice.
    /// </summary>
    private System.Diagnostics.Process? Running()
    {
        if (_started is { HasExited: false } mine)
        {
            return mine;
        }

        var s = Read();
        if (s.Pid is not { } pid || s.PidStarted is not { } since)
        {
            return null;
        }

        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            // The same process (an id is used again after one ends): its start time.
            if (!p.HasExited && Math.Abs((new DateTimeOffset(p.StartTime) - since).TotalSeconds) < 2)
            {
                return p;
            }

            p.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone.
        }

        return null;
    }

    /// <summary>The last lines of the generator's output.</summary>
    private string LastWords(int lines = 3)
    {
        try
        {
            return File.Exists(LogFile) ? string.Join(" | ", File.ReadLines(LogFile).TakeLast(lines)) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private System.Diagnostics.Process? _started;

    /// <summary>
    /// The internal generator's folder (tools\character-gen, found above where PC Diagnostic runs) and the Python that runs
    /// it (the kit's .venv named in its settings.py), as found on this PC; null where there is none.
    /// </summary>
    internal static (string? Python, string? Folder) FoundOnThisPc()
    {
        string? folder = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "character-gen");
            if (File.Exists(Path.Combine(candidate, "serve.py")))
            {
                folder = candidate;
                break;
            }
        }

        string? python = null;
        var kit = Environment.GetEnvironmentVariable("KITGEN");
        if (kit is null && folder is not null && File.Exists(Path.Combine(folder, "settings.py")))
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(folder, "settings.py")), "\"KITGEN\",\\s*r?\"([^\"]+)\"");
            kit = m.Success ? m.Groups[1].Value : null;
        }

        // Its own default: kit beside the scripts (a repository), else the data folder's (installed).
        kit ??= folder is not null && Directory.Exists(Path.Combine(folder, "kit")) ? Path.Combine(folder, "kit")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcDiagnostic", "characters", "kit");

        if (kit is not null && Path.Combine(kit, ".venv", "Scripts", "python.exe") is var venv && File.Exists(venv))
        {
            python = venv;
        }

        return (python, folder);
    }

    private sealed record StoredJob(GenerationJob Job, string? Remote, List<string>? RemoteStills);

    private Stored Read()
    {
        try
        {
            var file = Path.Combine(Folder, "generator.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(file), Json) ?? new Stored() : new Stored();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Stored();
        }
    }

    private void Write(Stored s)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "generator.json");
        File.WriteAllBytes(file + ".tmp", JsonSerializer.SerializeToUtf8Bytes(s, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private static string? Protect(string? key) => key is null ? null : Convert.ToBase64String(OperatingSystem.IsWindows()
        ? ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser)
        : System.Text.Encoding.UTF8.GetBytes(key));

    private static string? Unprotect(string? stored) => stored is null ? null : System.Text.Encoding.UTF8.GetString(OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser)
        : Convert.FromBase64String(stored));

    /// <summary>Where characters are generated (the key never shown).</summary>
    public GeneratorSettings Settings()
    {
        var s = Read();
        var (python, folder) = FoundOnThisPc();
        return new GeneratorSettings(s.Kind, s.Kind == "internal" ? s.Url ?? InternalUrl : s.Url, s.Key is not null, s.Python ?? python, s.Folder ?? folder, s.Kit);
    }

    /// <summary>Saves where characters are generated; a null key keeps the one saved, an empty one removes it.</summary>
    public string? Save(string kind, string? url, string? key, string? python = null, string? folder = null, string? kit = null)
    {
        if (kit is { Length: > 0 } && !Directory.Exists(Path.Combine(kit, "hf")) && !Directory.Exists(Path.Combine(kit, "models")))
        {
            return "A kit folder holds the models: its hf and models folders.";
        }

        if (kind == "internal" && folder is { Length: > 0 } && !File.Exists(Path.Combine(folder, "serve.py")))
        {
            return "The generator's folder is the one with serve.py (tools\\character-gen).";
        }

        if (kind is not ("off" or "internal" or "external"))
        {
            return "The generator is off, internal or external.";
        }

        if (kind == "external" && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
        {
            return "An external generator needs its address (http:// or https://).";
        }

        lock (_gate)
        {
            var s = Read();
            Write(s with
            {
                Kind = kind,
                Url = kind == "internal" ? (string.IsNullOrWhiteSpace(url) ? InternalUrl : url) : url,
                Key = key is null ? s.Key : key.Length == 0 ? null : Protect(key),
                Python = string.IsNullOrWhiteSpace(python) ? s.Python : python,
                Folder = string.IsNullOrWhiteSpace(folder) ? s.Folder : folder,
                Kit = kit is null ? s.Kit : kit.Length == 0 ? null : kit,
            });
        }

        return null;
    }

    /// <summary>
    /// The generator to send work to (the internal one started when it is not running), or why there is none: for the
    /// new animations (<see cref="CharacterAnimationService"/>).
    /// </summary>
    internal async Task<(HttpClient? Client, string? Problem)> GeneratorAsync(CancellationToken ct)
    {
        var s = Read();
        if (s.Kind == "off")
        {
            return (null, "Set up a character generator first (Settings › Characters › Generator).");
        }

        if (s.Kind == "internal" && await TestAsync(ct).ConfigureAwait(false) is (false, var problem))
        {
            return (null, problem);
        }

        return (Client(s), null);
    }

    private HttpClient Client(Stored s)
    {
        var client = http.CreateClient(HttpClientName);
        client.BaseAddress = new Uri((s.Kind == "internal" ? s.Url ?? InternalUrl : s.Url!).TrimEnd('/') + "/");
        if (Unprotect(s.Key) is { } key)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    /// <summary>
    /// Whether the generator answers, and what it says about itself: the one saved, or the choice given (not saved yet).
    /// The internal one is started first when it does not answer (and its Python and folder are known).
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct, GeneratorTry? choice = null)
    {
        var s = Read();
        if (choice is not null)
        {
            s = s with
            {
                Kind = choice.Kind,
                Url = choice.Kind == "internal" ? (string.IsNullOrWhiteSpace(choice.Url) ? InternalUrl : choice.Url) : choice.Url,
                Key = string.IsNullOrEmpty(choice.Key) ? s.Key : Protect(choice.Key),
                Python = string.IsNullOrWhiteSpace(choice.Python) ? s.Python : choice.Python,
                Folder = string.IsNullOrWhiteSpace(choice.Folder) ? s.Folder : choice.Folder,
            };
        }

        if (s.Kind == "off")
        {
            return (false, "Character generation is off.");
        }

        if (s.Kind == "external" && !Uri.TryCreate(s.Url, UriKind.Absolute, out _))
        {
            return (false, "An external generator needs its address (http:// or https://).");
        }

        var started = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var health = await Client(s).GetFromJsonAsync<JsonElement>("v1/health", ct).ConfigureAwait(false);
                return (true, $"{health.GetProperty("name").GetString()} is ready{(started ? " (started by PC Diagnostic)" : string.Empty)}{(health.TryGetProperty("busy", out var b) && b.GetBoolean() ? " (busy with another character)" : string.Empty)}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                if (s.Kind == "internal" && !started)
                {
                    if (StartInternal(s) is { } problem)
                    {
                        return (false, problem);
                    }

                    started = true;
                }

                if (s.Kind == "internal" && started && Running() is null)
                {
                    return (false, $"The generator stopped as it started: {_ended ?? LastWords()} (its output: {LogFile})");
                }

                if (s.Kind != "internal" || attempt >= 40)
                {
                    return (false, $"The generator does not answer at {(s.Kind == "internal" ? s.Url ?? InternalUrl : s.Url)}: {ex.Message}");
                }

                // Starting: its models load in the background, it answers within a few seconds.
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The generator's state now: running (and busy) or not, and what it says, asked quickly.</summary>
    public async Task<GeneratorStatus> StatusAsync(CancellationToken ct)
    {
        var s = Read();
        if (s.Kind == "off")
        {
            return new GeneratorStatus(false, false, "Character generation is off.", false);
        }

        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var health = await Client(s).GetFromJsonAsync<JsonElement>("v1/health", quick.Token).ConfigureAwait(false);
            var busy = health.TryGetProperty("busy", out var b) && b.GetBoolean();
            return new GeneratorStatus(true, busy, $"{health.GetProperty("name").GetString()} is running{(busy ? ", making a character" : string.Empty)}.", s.Kind == "internal");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (s.Kind != "internal")
            {
                return new GeneratorStatus(false, false, $"Not answering at {s.Url}.", false);
            }

            if (Running() is { } process)
            {
                return new GeneratorStatus(false, false, $"Starting… (process {process.Id}, since {process.StartTime:HH:mm:ss}: its models load first)", true, Starting: true);
            }

            return new GeneratorStatus(false, false, _ended is { } why ? $"Not running: {why}" : "Not running.", true);
        }
    }

    /// <summary>Starts the internal generator and waits for it to answer; the result as a test gives it.</summary>
    public Task<(bool Ok, string Message)> StartGeneratorAsync(CancellationToken ct) =>
        Read().Kind == "internal" ? TestAsync(ct) : Task.FromResult((false, "Only the generator on this PC is started here."));

    /// <summary>Stops the internal generator (it is asked to, and the one started here is ended if it does not).</summary>
    public async Task<string> StopGeneratorAsync(CancellationToken ct)
    {
        var s = Read();
        if (s.Kind != "internal")
        {
            return "Only the generator on this PC is stopped here.";
        }

        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(3));
            using var stop = await Client(s).PostAsync("v1/stop", null, quick.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Not answering: the one started here is ended below.
        }

        System.Diagnostics.Process? started;
        lock (_gate)
        {
            started = Running();
            _started = null;
            Write(Read() with { Pid = null, PidStarted = null });
        }

        if (started is { HasExited: false })
        {
            try
            {
                if (!started.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    started.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Ended meanwhile.
            }
        }

        started?.Dispose();
        return "Stopped.";
    }

    // ---- Prerequisites: checked one by one (setup.py check) and set up when asked, one at a time, in the background.

    private readonly Lock _setupGate = new();
    private SetupRun? _setup;

    /// <summary>The prerequisites' steps on this PC, as setup.py checks them with the generator's Python (and its kit).</summary>
    public async Task<Prerequisites> PrerequisitesAsync(CancellationToken ct)
    {
        var s = Read();
        var (foundPython, foundFolder) = FoundOnThisPc();
        var python = s.Python ?? foundPython;
        var folder = s.Folder ?? foundFolder;
        if (folder is null || !File.Exists(Path.Combine(folder, "setup.py")))
        {
            return new Prerequisites(s.Kit, [new PrerequisiteStep("folder", "Generator folder", false, false, "tools\\character-gen (with serve.py and setup.py) was not found: set its folder.", ["folder"])], _setup);
        }

        if (python is null || !File.Exists(python))
        {
            return new Prerequisites(s.Kit, [new PrerequisiteStep("python", "Python", false, false, "No Python for the generator: create one (Python 3.12 in its own environment), or choose one on this PC.", ["venv", "choose"])], _setup);
        }

        var start = new System.Diagnostics.ProcessStartInfo(python, ["setup.py", "check"])
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        start.Environment["PYTHONUTF8"] = "1";
        if (s.Kit is { } kit)
        {
            start.Environment["KITGEN"] = kit;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            var errors = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var line = output.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
            if (line is null)
            {
                return new Prerequisites(s.Kit, [new PrerequisiteStep("python", "Python", false, false, $"The check did not run with {python}: {errors.Trim().Split('\n').LastOrDefault()}", ["venv", "choose"])], _setup);
            }

            using var doc = JsonDocument.Parse(line);
            var steps = doc.RootElement.GetProperty("steps").EnumerateArray().Select(e => new PrerequisiteStep(
                e.GetProperty("id").GetString()!, e.GetProperty("title").GetString()!, e.GetProperty("ok").GetBoolean(), e.GetProperty("warn").GetBoolean(),
                e.GetProperty("detail").GetString() ?? string.Empty, [.. e.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!)])).ToList();
            return new Prerequisites(doc.RootElement.GetProperty("kit").GetString(), steps, _setup);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new Prerequisites(s.Kit, [new PrerequisiteStep("python", "Python", false, false, $"The check could not run with {python}: {ex.Message}", ["venv", "choose"])], _setup);
        }
    }

    /// <summary>
    /// Runs a setup step in the background (one at a time): packages, models, cutout, ffmpeg or node with setup.py, or venv (a
    /// Python environment of its own for the generator, made with uv or the Python launcher, then used); null when it
    /// started, else why not.
    /// </summary>
    public string? RunSetup(string action)
    {
        var s = Read();
        var (foundPython, foundFolder) = FoundOnThisPc();
        var python = s.Python ?? foundPython;
        var folder = s.Folder ?? foundFolder;
        if (folder is null)
        {
            return "The generator's folder was not found.";
        }

        System.Diagnostics.ProcessStartInfo start;
        string? madePython = null;
        if (action == "venv")
        {
            var venv = Path.Combine(paths.Root, "characters", "generator-python");
            madePython = Path.Combine(venv, "Scripts", "python.exe");
            var uv = FindOnPath("uv.exe");
            start = uv is not null
                ? new System.Diagnostics.ProcessStartInfo(uv, ["venv", "--python", "3.12", "--seed", venv])
                : new System.Diagnostics.ProcessStartInfo(FindOnPath("py.exe") ?? "py", ["-3.12", "-m", "venv", venv]);
        }
        else if (action is "packages" or "models" or "cutout" or "ffmpeg" or "node")
        {
            if (python is null || !File.Exists(python))
            {
                return "Create or choose the generator's Python first.";
            }

            start = new System.Diagnostics.ProcessStartInfo(python, ["-u", "setup.py", action]);
        }
        else
        {
            return "Unknown step.";
        }

        start.WorkingDirectory = folder;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        start.Environment["PYTHONUTF8"] = "1";
        if (s.Kit is { } kit)
        {
            start.Environment["KITGEN"] = kit;
        }

        lock (_setupGate)
        {
            if (_setup is { State: "running" })
            {
                return $"Wait for \"{_setup.Action}\" to finish.";
            }

            var run = new SetupRun(action, "running", [], DateTimeOffset.UtcNow);
            _setup = run;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var process = System.Diagnostics.Process.Start(start)!;
                    void Add(string? line)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            return;
                        }

                        lock (_setupGate)
                        {
                            run.Log.Add(line.Split('\r')[^1].TrimEnd());
                            if (run.Log.Count > 200)
                            {
                                run.Log.RemoveAt(0);
                            }
                        }
                    }

                    process.OutputDataReceived += (_, e) => Add(e.Data);
                    process.ErrorDataReceived += (_, e) => Add(e.Data);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    var ok = process.ExitCode == 0;
                    if (ok && madePython is not null && File.Exists(madePython))
                    {
                        // The new environment is the generator's Python from now on.
                        lock (_gate)
                        {
                            Write(Read() with { Python = madePython });
                        }

                        Add($"The generator's Python is now {madePython}: install the packages next.");
                    }

                    lock (_setupGate)
                    {
                        _setup = run with { State = ok ? "done" : "failed" };
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    lock (_setupGate)
                    {
                        run.Log.Add(ex.Message);
                        _setup = run with { State = "failed" };
                    }
                }
            });
        }

        return null;
    }

    /// <summary>The setup step under way or last run, with its output.</summary>
    public SetupRun? Setup()
    {
        lock (_setupGate)
        {
            return _setup is null ? null : _setup with { Log = [.. _setup.Log] };
        }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    /// <summary>Starts the internal generator (serve.py) with its Python, hidden; null when started, else why not.</summary>
    private string? StartInternal(Stored s)
    {
        var (foundPython, foundFolder) = FoundOnThisPc();
        var python = s.Python ?? foundPython;
        var folder = s.Folder ?? foundFolder;
        if (folder is null || !File.Exists(Path.Combine(folder, "serve.py")))
        {
            return "The generator's folder (tools\\character-gen, with serve.py) was not found: set it below.";
        }

        if (python is null || !File.Exists(python))
        {
            return "The Python that runs the generator was not found: set it below (the kit's .venv\\Scripts\\python.exe).";
        }

        lock (_gate)
        {
            if (Running() is not null)
            {
                // Already started (also by an earlier run of PC Diagnostic): waited for, not started again.
                return null;
            }

            var port = Uri.TryCreate(s.Url ?? InternalUrl, UriKind.Absolute, out var u) ? u.Port : 7865;
            var start = new System.Diagnostics.ProcessStartInfo(python, ["serve.py", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)])
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONUNBUFFERED"] = "1";
            if (s.Kit is { } kit)
            {
                start.Environment["KITGEN"] = kit;
            }

            // Installed (not a repo's tools\character-gen), it cannot write beside itself: its outputs go in the data folder.
            if (!Directory.Exists(Path.Combine(folder, "..", "..", ".git")))
            {
                start.Environment["CHARGEN_OUT"] = Path.Combine(paths.Root, "characters", "generator-out");
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                var log = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), System.Text.Encoding.UTF8) { AutoFlush = true };
                log.WriteLine($"---- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {python} serve.py --port {port}");
                var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
                void Say(string? line)
                {
                    if (line is not null)
                    {
                        lock (log)
                        {
                            log.WriteLine(line);
                        }
                    }
                }

                process.OutputDataReceived += (_, e) => Say(e.Data);
                process.ErrorDataReceived += (_, e) => Say(e.Data);
                process.Exited += (_, _) =>
                {
                    // Its last words kept for the status (it ended by itself, or was stopped).
                    var code = process.ExitCode;
                    Thread.Sleep(200);
                    _ended = $"it ended (code {code}) {DateTimeOffset.Now:HH:mm:ss}: {LastWords()}";
                    lock (log)
                    {
                        log.WriteLine($"---- ended with code {code}");
                        log.Dispose();
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _started = process;
                _ended = null;
                Write(Read() with { Pid = process.Id, PidStarted = new DateTimeOffset(process.StartTime) });
                LogStarted(logger, python, folder);
                return null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return $"The generator could not be started: {ex.Message}";
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the character generator with {Python} in {Folder}")]
    private static partial void LogStarted(ILogger logger, string python, string folder);

    /// <summary>The generations, newest first.</summary>
    public IReadOnlyList<GenerationJob> Jobs() => [.. (Read().Jobs ?? []).Select(j => j.Job).OrderByDescending(j => j.Created)];

    /// <summary>Starts stills for a prompt (person or animal), or animates a still of a done stills job.</summary>
    public async Task<(GenerationJob? Job, string? Problem)> StartAsync(string type, string prompt, string kind, string? name, string? from, int still, IReadOnlyList<string>? actions, int faces, CancellationToken ct)
    {
        var s = Read();
        if (s.Kind == "off")
        {
            return (null, "Set up a character generator first.");
        }

        // The internal generator started when it is not running.
        if (s.Kind == "internal" && await TestAsync(ct).ConfigureAwait(false) is (false, var problem))
        {
            return (null, problem);
        }

        object body;
        StoredJob? source = null;
        if (type == "stills")
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 400)
            {
                return (null, "Describe the character in 1-400 characters.");
            }

            body = new { prompt, kind, count = 4 };
        }
        else
        {
            source = (s.Jobs ?? []).FirstOrDefault(j => j.Job.Id == from && j.Job.State == "done" && j.Job.Type == "stills");
            if (source?.RemoteStills is not { } remote || still < 0 || still >= remote.Count)
            {
                return (null, "Choose one of the stills of a finished generation.");
            }

            prompt = source.Job.Prompt;
            kind = source.Job.Kind;
            body = new { still = remote[still], name = name ?? "Character", kind, actions = actions ?? ["idle", "walk", "run", "jump", "play", "sit", "sleep"], faces };
        }

        try
        {
            // With its length (not sent in chunks: the generator reads a body by its Content-Length, and saw none).
            using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
            var response = await Client(s).PostAsync(type == "stills" ? "v1/stills" : "v1/characters", content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"The generator refused it: {await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
            }

            var remoteJob = (await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false)).GetProperty("job").GetString();
            var job = new GenerationJob(Guid.NewGuid().ToString("N")[..12], type, prompt, kind, name, "queued", "Sent to the generator", 0, null, DateTimeOffset.UtcNow, From: from);
            lock (_gate)
            {
                var now = Read();
                Write(now with { Jobs = [.. (now.Jobs ?? []).TakeLast(49), new StoredJob(job, remoteJob, null)] });
            }

            return (job, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return (null, $"The generator does not answer at {Settings().Url}: {ex.Message}");
        }
    }

    /// <summary>A still of a done stills generation, by number; null when there is none.</summary>
    public string? StillFile(string id, int n)
    {
        var file = Path.Combine(Folder, "generations", id, $"still-{n}.png");
        return id.All(char.IsAsciiLetterOrDigit) && File.Exists(file) ? file : null;
    }

    /// <summary>Takes a generation off the list (its character stays in the library).</summary>
    public void Forget(string id)
    {
        lock (_gate)
        {
            var s = Read();
            Write(s with { Jobs = [.. (s.Jobs ?? []).Where(j => j.Job.Id != id)] });
        }
    }

    /// <summary>Follows the jobs not finished yet, every few seconds; brings stills here and imports characters.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FollowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Following the character generations failed; trying again");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        var s = Read();
        foreach (var stored in (s.Jobs ?? []).Where(j => j.Job.State is "queued" or "running" && j.Remote is not null))
        {
            var job = stored.Job;
            List<string>? remoteStills = stored.RemoteStills;
            try
            {
                var client = Client(s);
                var remote = await client.GetFromJsonAsync<JsonElement>($"v1/jobs/{stored.Remote}", ct).ConfigureAwait(false);
                var state = remote.GetProperty("state").GetString() ?? "running";
                job = job with
                {
                    State = state == "done" ? "running" : state,
                    Step = remote.GetProperty("step").GetString() ?? job.Step,
                    Progress = remote.GetProperty("progress").GetDouble(),
                    Error = remote.TryGetProperty("error", out var e) ? e.GetString() : null,
                    Log = remote.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array ? [.. log.EnumerateArray().Select(l => l.GetString() ?? string.Empty).TakeLast(40)] : job.Log,
                };
                if (state == "done" && job.Type == "stills")
                {
                    remoteStills = [.. remote.GetProperty("stills").EnumerateArray().Select(x => x.GetString()!)];
                    var dir = Path.Combine(Folder, "generations", job.Id);
                    Directory.CreateDirectory(dir);
                    for (var i = 0; i < remoteStills.Count; i++)
                    {
                        await File.WriteAllBytesAsync(Path.Combine(dir, $"still-{i}.png"), await client.GetByteArrayAsync($"v1/files/{remoteStills[i]}", ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    }

                    job = job with { State = "done", Step = "Choose a still", Progress = 1, Stills = remoteStills.Count };
                }
                else if (state == "done")
                {
                    var bytes = await client.GetByteArrayAsync($"v1/files/{remote.GetProperty("character").GetString()}", ct).ConfigureAwait(false);
                    var pack = CharacterPack.Read(bytes);
                    job = pack is null
                        ? job with { State = "failed", Error = "The generator's character is not one PC Diagnostic plays." }
                        : job with { State = "done", Step = "In the library", Progress = 1, Character = await OverlayImageEndpoints.AddToLibraryAsync(bytes, job.Name ?? pack.Name, pack, paths, store, logger).ConfigureAwait(false) };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException)
            {
                job = job with { Step = "The generator does not answer; trying again" };
            }

            lock (_gate)
            {
                var now = Read();
                Write(now with { Jobs = [.. (now.Jobs ?? []).Select(j => j.Job.Id == job.Id ? new StoredJob(job, stored.Remote, remoteStills) : j)] });
            }
        }
    }
}

/// <summary>A generator choice to test before it is saved.</summary>
/// <param name="Kind">off, internal or external.</param>
/// <param name="Url">The external generator's address.</param>
/// <param name="Key">Its key (empty: the one saved).</param>
/// <param name="Python">The internal generator's Python.</param>
/// <param name="Folder">The internal generator's folder.</param>
public sealed record GeneratorTry(string Kind, string? Url = null, string? Key = null, string? Python = null, string? Folder = null);

/// <summary>The generator's state.</summary>
/// <param name="Running">It answers.</param>
/// <param name="Busy">It is making a character.</param>
/// <param name="Message">What it says, or why it does not answer.</param>
/// <param name="CanStart">It is the generator on this PC (started and stopped here).</param>
public sealed record GeneratorStatus(bool Running, bool Busy, string Message, bool CanStart, bool Starting = false);

/// <summary>The character generator's prerequisites on this PC.</summary>
/// <param name="Kit">The kit folder the models are read from (KITGEN).</param>
/// <param name="Steps">Each prerequisite, in order.</param>
/// <param name="Running">The setup step under way or last run.</param>
public sealed record Prerequisites(string? Kit, IReadOnlyList<PrerequisiteStep> Steps, SetupRun? Running);

/// <summary>One prerequisite.</summary>
/// <param name="Id">python, packages, gpu, models, rife, cutout, ffmpeg, node or folder.</param>
/// <param name="Title">What it is.</param>
/// <param name="Ok">It is in place.</param>
/// <param name="Warn">In place but short of what is needed (a GPU with less memory).</param>
/// <param name="Detail">What was found, or what is missing.</param>
/// <param name="Actions">What can be done: venv, choose, packages, models, kit, cutout, ffmpeg, node, folder.</param>
public sealed record PrerequisiteStep(string Id, string Title, bool Ok, bool Warn, string Detail, IReadOnlyList<string> Actions);

/// <summary>A setup step run in the background.</summary>
/// <param name="Action">What it does.</param>
/// <param name="State">running, done or failed.</param>
/// <param name="Log">Its output, newest last.</param>
/// <param name="Started">When it started.</param>
public sealed record SetupRun(string Action, string State, List<string> Log, DateTimeOffset Started);
