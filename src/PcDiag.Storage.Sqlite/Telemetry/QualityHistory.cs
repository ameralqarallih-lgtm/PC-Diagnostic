using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// The quality history (QualityMinutes and GameSessions): from every frame, recorded or only watched, a summary per
/// minute of the game's frame rate, the busiest processor's DPC and interrupt time, the pings and DNS checks, and one
/// row per game session. A frame older than the newest one seen (the watched minutes stored again when they become part
/// of an incident) is not counted twice. Not thread-safe (the storage consumer is sequential).
/// </summary>
internal sealed class QualityHistory
{
    /// <summary>A drop: this far (%) or more below the game's usual frame rate.</summary>
    public const double DropPercent = 40;

    /// <summary>A pause in the game's frames longer than this ends its session.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromSeconds(60);

    private readonly List<MinuteRow> _completed = [];
    private Minute? _minute;
    private Session? _session;
    private bool _sessionChanged;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Counts a frame.</summary>
    public void Add(SampleFrame frame)
    {
        if (frame.Timestamp <= _last)
        {
            return;
        }

        _last = frame.Timestamp;
        var start = Floor(frame.Timestamp);
        if (_minute is { } current && current.Ts != start)
        {
            _completed.Add(current.ToRow());
            _minute = null;
        }

        _minute ??= new Minute(start);
        _minute.Add(frame);

        if (frame.Frames is { } frames && frames.Game is { } game)
        {
            if (_session is null || !string.Equals(_session.Game, game, StringComparison.OrdinalIgnoreCase) || frame.Timestamp - _session.End > SessionGap)
            {
                _session = new Session(game, frame.Timestamp);
            }

            _session.Add(frame.Timestamp, frames);
            _sessionChanged = true;
        }
    }

    /// <summary>Whether a minute has ended since the rows were last taken.</summary>
    public bool HasCompleted => _completed.Count > 0;

    /// <summary>The ended minutes (and with <paramref name="includeCurrent"/> the one under way, as far as it goes).</summary>
    public List<MinuteRow> TakeMinutes(bool includeCurrent)
    {
        var rows = _completed.ToList();
        _completed.Clear();
        if (includeCurrent && _minute is { Seconds: > 0 } current)
        {
            rows.Add(current.ToRow());
        }

        return rows;
    }

    /// <summary>The game session's newest state when it changed since the last call (its Id says whether it is stored yet).</summary>
    public SessionRow? TakeSession()
    {
        if (!_sessionChanged || _session is null)
        {
            return null;
        }

        _sessionChanged = false;
        return _session.ToRow();
    }

    /// <summary>The stored session's row id (after its first insert).</summary>
    public void Stored(SessionRow row, long id)
    {
        if (_session is not null && _session.Start == row.Start && _session.Game == row.Game)
        {
            _session.Id = id;
        }
    }

    private static long Floor(DateTimeOffset at)
    {
        var ms = UnixTime.ToUnixMs(at);
        return ms - (ms % 60_000);
    }

    /// <summary>One minute's summary.</summary>
    internal sealed record MinuteRow(
        long Ts,
        int Seconds,
        string? Game,
        int GameSeconds,
        double? FpsAvg,
        double? FpsMin,
        double? Low1Fps,
        int Stutters,
        double? WorstMs,
        int DropSeconds,
        double? DpcCoreMax,
        double? IsrCoreMax,
        double? DpcAvg,
        Hop Router,
        Hop Provider,
        Hop Internet,
        double? InternetJitter,
        double? DnsMs,
        int DnsChecks,
        int DnsFailed,
        double? WifiMin,
        double? NetBps,
        Shown Shown);

    /// <summary>One hop's pings in a minute: average and highest round trip, and the share lost (%).</summary>
    internal sealed record Hop(double? Ms, double? Max, double? Loss);

    /// <summary>A game session's state.</summary>
    internal sealed record SessionRow(long? Id, string Game, DateTimeOffset Start, DateTimeOffset End, int Seconds, double? FpsAvg, double? FpsP1, double? Low1Fps,
        int Stutters, int DropSeconds, double? WorstMs, Shown Shown, string? Recognized);

    /// <summary>
    /// The frames that reached the screen over a minute or a session (PresentMon): average frames shown a second, frames
    /// never shown, display latency, the GPU's and CPU's share of each frame, the most common present mode, and the share
    /// of seconds windowed.
    /// </summary>
    internal sealed record Shown(double? DisplayedFps, int? Dropped, double? DisplayLatencyMs, double? GpuBusyPercent, double? CpuBusyPercent, string? PresentMode,
        double? WindowedPercent)
    {
        /// <summary>None measured.</summary>
        public static Shown None { get; } = new(null, null, null, null, null, null, null);
    }

    /// <summary>Sums the frames that reached the screen, second by second.</summary>
    private sealed class ShownSum
    {
        private readonly List<double> _fps = [];
        private readonly List<double> _latency = [];
        private readonly List<double> _gpu = [];
        private readonly List<double> _cpu = [];
        private readonly Dictionary<string, int> _modes = new(StringComparer.Ordinal);
        private int _dropped;
        private int _windowed;
        private int _seconds;

        public void Add(FrameSample f)
        {
            _seconds++;
            if (!f.FullScreen)
            {
                _windowed++;
            }

            if (f.DisplayedFps is { } shown)
            {
                _fps.Add(shown);
                _dropped += f.Dropped ?? 0;
            }

            if (f.DisplayLatencyMs is { } latency)
            {
                _latency.Add(latency);
            }

            if (f.GpuBusyPercent is { } gpu)
            {
                _gpu.Add(gpu);
            }

            if (f.CpuBusyPercent is { } cpu)
            {
                _cpu.Add(cpu);
            }

            if (f.PresentMode is { } mode)
            {
                _modes[mode] = _modes.GetValueOrDefault(mode) + 1;
            }
        }

        public Shown ToShown() => _seconds == 0
            ? Shown.None
            : new Shown(Avg(_fps), _fps.Count > 0 ? _dropped : null, Avg(_latency), Avg(_gpu), Avg(_cpu), _modes.Count > 0 ? _modes.MaxBy(m => m.Value).Key : null,
                Math.Round(100.0 * _windowed / _seconds));

        private static double? Avg(List<double> values) => values.Count > 0 ? Math.Round(values.Average(), 1) : null;
    }

    private sealed class Minute(long ts)
    {
        private readonly Dictionary<string, (int Seconds, List<double> Fps)> _games = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<double> _low1 = [];
        private readonly Pings _router = new();
        private readonly Pings _provider = new();
        private readonly Pings _internet = new();
        private readonly List<double> _dns = [];
        private readonly List<double> _dpcAll = [];
        private readonly List<double> _net = [];
        private double? _dpcCore;
        private double? _isrCore;
        private double? _worst;
        private double? _wifi;
        private readonly ShownSum _shown = new();
        private int _stutters;
        private int _drops;
        private int _dnsChecks;
        private int _dnsFailed;

        public long Ts { get; } = ts;

        public int Seconds { get; private set; }

        public void Add(SampleFrame frame)
        {
            Seconds++;
            if (frame.Frames is { } f && f.Game is { } game)
            {
                if (!_games.TryGetValue(game, out var g))
                {
                    _games[game] = g = (0, []);
                }

                g.Fps.Add(f.Fps);
                _games[game] = (g.Seconds + 1, g.Fps);
                _stutters += f.Stutters;
                _worst = Max(_worst, f.WorstFrameTimeMs);
                if (f.Low1Fps is { } low)
                {
                    _low1.Add(low);
                }

                if (f.DropPercent >= DropPercent)
                {
                    _drops++;
                }

                _shown.Add(f);
            }

            if (frame.Cpu is { } cpu)
            {
                _dpcCore = Max(_dpcCore, cpu.BusiestDpcCore?.Percent);
                _isrCore = Max(_isrCore, cpu.BusiestInterruptCore?.Percent);
                if (cpu.DpcPercent.HasValue)
                {
                    _dpcAll.Add(cpu.DpcPercent.Value!.Value);
                }
            }

            if (frame.Connection is { } c)
            {
                _router.Add(c.Router);
                _provider.Add(c.Provider);
                _internet.Add(c.Internet);
                if (c.DnsFailed is { } failed)
                {
                    _dnsChecks++;
                    if (failed)
                    {
                        _dnsFailed++;
                    }
                    else if (c.DnsMs is { } ms)
                    {
                        _dns.Add(ms);
                    }
                }
            }

            if (frame.Network is { } network)
            {
                double total = 0;
                foreach (var nic in network.Interfaces)
                {
                    total += nic.ReceiveBytesPerSec.GetValueOrDefault(0) + nic.SendBytesPerSec.GetValueOrDefault(0);
                    if (nic.WifiSignalQualityPercent.HasValue)
                    {
                        _wifi = _wifi is { } w ? Math.Min(w, nic.WifiSignalQualityPercent.Value!.Value) : nic.WifiSignalQualityPercent.Value!.Value;
                    }
                }

                _net.Add(total);
            }
        }

        public MinuteRow ToRow()
        {
            var game = _games.Count == 0 ? default : _games.MaxBy(g => g.Value.Seconds);
            var fps = game.Value.Fps;
            return new MinuteRow(
                Ts,
                Seconds,
                game.Key,
                game.Value.Seconds,
                fps is { Count: > 0 } ? Math.Round(fps.Average(), 1) : null,
                fps is { Count: > 0 } ? fps.Min() : null,
                _low1.Count > 0 ? _low1.Min() : null,
                _stutters,
                _worst,
                _drops,
                Round(_dpcCore),
                Round(_isrCore),
                _dpcAll.Count > 0 ? Math.Round(_dpcAll.Average(), 2) : null,
                _router.ToHop(),
                _provider.ToHop(),
                _internet.ToHop(),
                _internet.Jitter(),
                _dns.Count > 0 ? Math.Round(_dns.Average(), 1) : null,
                _dnsChecks,
                _dnsFailed,
                _wifi,
                _net.Count > 0 ? Math.Round(_net.Average()) : null,
                _shown.ToShown());
        }
    }

    private sealed class Pings
    {
        private readonly List<double> _ms = [];
        private int _sent;
        private int _lost;

        public void Add(PingResult ping)
        {
            if (!ping.Sent)
            {
                return;
            }

            _sent++;
            if (ping.Ms is { } ms)
            {
                _ms.Add(ms);
            }
            else
            {
                _lost++;
            }
        }

        public Hop ToHop() => _sent == 0
            ? new Hop(null, null, null)
            : new Hop(_ms.Count > 0 ? Math.Round(_ms.Average(), 2) : null, _ms.Count > 0 ? _ms.Max() : null, Math.Round(100.0 * _lost / _sent, 1));

        /// <summary>The average change between consecutive round trips (null with fewer than two).</summary>
        public double? Jitter()
        {
            if (_ms.Count < 2)
            {
                return null;
            }

            double sum = 0;
            for (var i = 1; i < _ms.Count; i++)
            {
                sum += Math.Abs(_ms[i] - _ms[i - 1]);
            }

            return Math.Round(sum / (_ms.Count - 1), 2);
        }
    }

    private sealed class Session(string game, DateTimeOffset start)
    {
        private readonly List<double> _fps = [];
        private readonly List<double> _low1 = [];
        private int _stutters;
        private int _drops;
        private double _worst;
        private readonly ShownSum _shown = new();
        private string? _recognized;

        public long? Id { get; set; }

        public string Game { get; } = game;

        public DateTimeOffset Start { get; } = start;

        public DateTimeOffset End { get; private set; } = start;

        public void Add(DateTimeOffset at, FrameSample frames)
        {
            End = at;
            _fps.Add(frames.Fps);
            _stutters += frames.Stutters;
            _worst = Math.Max(_worst, frames.WorstFrameTimeMs);
            if (frames.Low1Fps is { } low)
            {
                _low1.Add(low);
            }

            if (frames.DropPercent >= DropPercent)
            {
                _drops++;
            }

            _shown.Add(frames);
            _recognized = frames.Recognized ?? _recognized;
        }

        public SessionRow ToRow()
        {
            var sorted = _fps.Order().ToList();
            var lows = _low1.Order().ToList();
            return new SessionRow(Id, Game, Start, End, _fps.Count, Math.Round(_fps.Average(), 1), sorted[(int)Math.Floor((sorted.Count - 1) * 0.01)],
                lows.Count > 0 ? lows[lows.Count / 2] : null, _stutters, _drops, _worst, _shown.ToShown(), _recognized);
        }
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;
}
