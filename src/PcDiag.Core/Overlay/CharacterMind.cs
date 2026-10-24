using System.Globalization;

namespace PcDiag.Core.Overlay;

/// <summary>
/// What a character's mind adds at a moment, over its reaction: an action and an effect in place of the reaction's (a
/// relief dance, a jump at a fast rise, asleep while the user is away, a hello when they are back), a speech bubble
/// (its text, tone and age) and how much it shakes (0-1).
/// </summary>
/// <param name="Action">What it does now in place of its reaction's action (null: the reaction's).</param>
/// <param name="Effect">The effect in place of its reaction's (null: the reaction's).</param>
/// <param name="Bubble">What it says now (null: nothing).</param>
/// <param name="Tone">The bubble's tone: critical, high, relief or info.</param>
/// <param name="BubbleAge">How long the bubble has been shown, in seconds.</param>
/// <param name="BubbleLife">How long it is shown, in seconds (it fades out at the end).</param>
/// <param name="Shake">How much it shakes: 1 while what it reacts to is critical, 0 otherwise.</param>
/// <param name="Burst">A burst of particles from it now (confetti on a happy moment; null: none).</param>
/// <param name="BurstAge">How long the burst has been going, in seconds.</param>
/// <param name="Prop">A prop in place of its reaction's (a trophy on relief, a toolbox for an incident; null: the reaction's).</param>
/// <param name="Toward">Where along its line it goes or stays now (the mouse resting near it, a friend; null: its route).</param>
/// <param name="Facing">The way it faces when it is not walking (1 right, -1 left, 0: its own).</param>
/// <param name="Weather">The weather's effect, under its reaction's (rain, snow, wind; null: none).</param>
/// <param name="WeatherProp">The weather's prop, under its reaction's (an umbrella, a desk fan, a coffee; null: none).</param>
public sealed record CharacterThought(string? Action, string? Effect, string? Bubble, string Tone, double BubbleAge, double BubbleLife, double Shake, string? Burst = null, double BurstAge = 0, string? Prop = null,
    double? Toward = null, int Facing = 0, string? Weather = null, string? WeatherProp = null)
{
    /// <summary>Nothing to add.</summary>
    public static readonly CharacterThought None = new(null, null, null, "info", 0, 0, 0);
}

/// <summary>Something that happened that characters react to once: an incident saved, recording turned on or off, a game come to the front.</summary>
/// <param name="Id">Its number, one more for each (a mind reacts to each once).</param>
/// <param name="Kind">incident, recording-on, recording-off or game.</param>
/// <param name="Text">The incident's title or the game's name.</param>
public sealed record CharacterEvent(long Id, string Kind, string? Text = null);

/// <summary>
/// A character's mind, moment to moment (the editor has the same rules in characters/mind.ts, checked against each
/// other by mind-parity.json): it watches the reading it reacts to and the user, and decides
/// <list type="bullet">
/// <item>a bubble when the reading turns bad ("CPU at 100 %!", "GPU temp 88 °C, getting hot") or good again ("Phew, CPU
/// back to 40 %", with a little dance);</item>
/// <item>a jump with "!" when the reading climbs fast while still normal (it warns before it is critical);</item>
/// <item>asleep after <see cref="AwaySeconds"/> without the keyboard or mouse (unless something is bad), and a hello when
/// the user is back;</item>
/// <item>a shake while the reading is critical.</item>
/// </list>
/// With its <see cref="CharacterSenses"/> it also
/// <list type="bullet">
/// <item>notices the mouse near it (a hop, then it watches it), and comes over when it rests near it;</item>
/// <item>enjoys being petted (the mouse rubbed back and forth over it: hearts and a dance) and answers clicks (a jump
/// and a word; dizzy after many);</item>
/// <item>meets another character on its line now and then (a hello each way, then they dance together), the first of
/// the two walking over to the other;</item>
/// <item>says good morning once a day and yawns late at night;</item>
/// <item>says what the weather is when it changes, and shows it now and then (an umbrella in the rain, snowflakes, wind).</item>
/// </list>
/// An attention budget keeps it from chattering: a bubble at most every <see cref="BubbleGap"/> seconds (a reading
/// turning critical or back from critical, a hello, a click, a petting and a meeting excepted) and a fast-rise warning
/// at most every <see cref="TrendGap"/> seconds.
/// </summary>
public sealed class CharacterMind
{
    /// <summary>How far it notices the mouse: this many of its heights to either side, and <see cref="NoticeRise"/> up or down from its middle.</summary>
    public const double NoticeReach = 4;

    /// <summary>How far up or down from its middle it notices the mouse, in its heights.</summary>
    public const double NoticeRise = 2.5;

    /// <summary>The mouse resting near it this long (seconds) brings it over.</summary>
    public const double RestSeconds = 1.2;

    /// <summary>It stops this many of its heights from the mouse.</summary>
    public const double ComeGap = 0.7;

    /// <summary>The least time between two hops on noticing the mouse, in seconds.</summary>
    public const double HopGap = 20;

    /// <summary>A petting: this many turns of the mouse rubbed over it (each a stroke of <see cref="PetStroke"/> of its height) within <see cref="PetWindow"/> seconds.</summary>
    public const int PetStrokes = 3;

    /// <summary>A stroke of a petting, in its heights.</summary>
    public const double PetStroke = 0.06;

    /// <summary>How long the strokes of a petting may take, in seconds.</summary>
    public const double PetWindow = 1.5;

    /// <summary>Clicks within this many seconds count together (many make it dizzy).</summary>
    public const double ClickWindow = 4;

    /// <summary>Two characters this close (in their heights) meet.</summary>
    public const double MeetReach = 1.3;

    /// <summary>A meeting lasts this long, in seconds: a hello each way, then a dance together.</summary>
    public const double MeetSeconds = 5;

    /// <summary>The same two meet again after this long at the least, in seconds.</summary>
    public const double MeetGap = 45;

    /// <summary>A character this close (in heights) may be walked over to, at most every <see cref="SeekEvery"/> seconds.</summary>
    public const double SeekReach = 5;

    /// <summary>The least time between two walks over to another character, in seconds.</summary>
    public const double SeekEvery = 60;

    /// <summary>Late at night it yawns this often, in seconds.</summary>
    public const double YawnEvery = 1200;

    /// <summary>The weather is shown for <see cref="WeatherSpell"/> seconds of every this many, from when it changed.</summary>
    public const double WeatherEvery = 300;

    /// <summary>How long the weather is shown each time, in seconds.</summary>
    public const double WeatherSpell = 40;

    private static readonly string[] ClickLines = ["Hi there!", "Hehe!", "That tickles!"];
    private static readonly string[] PetLines = ["Hehe, that tickles!", "Aww, thank you!", "More, please!"];

    /// <summary>Without input for this long (seconds), the user is away.</summary>
    public const double AwaySeconds = 180;

    /// <summary>The least time between two bubbles, in seconds (critical and hello excepted).</summary>
    public const double BubbleGap = 8;

    /// <summary>The least time between two fast-rise warnings, in seconds.</summary>
    public const double TrendGap = 15;

    /// <summary>How far back a rise is looked at, in seconds.</summary>
    public const double TrendWindow = 6;

    /// <summary>A rise of this share of the reading's range (from its quiet limit to its critical one) within <see cref="TrendWindow"/> is fast.</summary>
    public const double TrendShare = 0.35;

    private readonly Queue<(double At, double Value)> _samples = new();
    private string? _source;
    private string? _status;
    private (string? Action, string? Effect, double Until, string? Prop)? _moment;
    private (string Text, string Tone, double Since, double Life)? _bubble;
    private double _lastBubble = double.NegativeInfinity;
    private double _lastTrend = double.NegativeInfinity;
    private bool _away;
    private long? _seenEvent;
    private double _confetti = double.NegativeInfinity;
    private long? _seenTouch;
    private readonly Queue<double> _clicks = new();
    private double _holdUntil = double.NegativeInfinity;
    private bool _near;
    private double _watchUntil = double.NegativeInfinity;
    private double _lastHop = double.NegativeInfinity;
    private (double X, double Y, double Since) _rest = (double.NaN, double.NaN, 0);
    private int _strokeWay;
    private double _anchor = double.NaN;
    private readonly Queue<double> _strokes = new();
    private int _pets;
    private double _petUntil = double.NegativeInfinity;
    private (string Friend, double Since, double At, bool Speaker, bool Answered)? _meeting;
    private readonly Dictionary<string, double> _met = new(StringComparer.Ordinal);
    private (string Friend, double Since)? _seeking;
    private double _lastSeek = double.NegativeInfinity;
    private int? _greeted;
    private double? _lastYawn;
    private long? _weatherSeen;
    private string? _weatherSaid;
    private double _weatherSince;

    /// <summary>It had nothing else to do at its last moment: nothing bad, no moment, not meeting, not watching the mouse (another may meet it).</summary>
    public bool Free { get; private set; }

    /// <summary>Whom it was meeting at its last moment (null: no one).</summary>
    public string? MeetingWith => _meeting?.Friend;

    /// <summary>
    /// What the mind adds at <paramref name="now"/> (seconds): <paramref name="judged"/> is what the character reacts to
    /// (null: nothing) and <paramref name="status"/> its status as held; <paramref name="idle"/> how long the user has
    /// not touched the keyboard or mouse, in seconds (null: not known, as in the editor); <paramref name="plays"/> the
    /// animations it uses (null: all).
    /// </summary>
    /// <param name="happened">The latest event (null: none yet): one it has not seen is reacted to once (those before
    /// the first moment it thinks, and while the user is away, are let go).</param>
    /// <param name="senses">What it senses beyond its reading: the mouse, clicks, another character, the weather, the time (null: nothing).</param>
    public CharacterThought Think(double now, ReadingJudged? judged, string? status, double? idle, IReadOnlyList<string>? plays, CharacterEvent? happened = null, CharacterSenses? senses = null)
    {
        var bad = judged is not null && status is not null && IsBad(judged.Kind, status);
        if (judged is not null && status is not null)
        {
            Watch(now, judged, status, plays);
        }

        if (happened is { } e && _seenEvent is { } seen && e.Id > seen && !_away)
        {
            React(now, e, plays);
        }

        if (happened is not null)
        {
            _seenEvent = happened.Id;
        }
        else
        {
            _seenEvent ??= 0;
        }

        // Away: asleep (unless something is bad); back: a hello.
        if (idle is { } quiet)
        {
            if (!_away && quiet >= AwaySeconds && !bad)
            {
                _away = true;
                _bubble = null;
            }
            else if (_away && (quiet < 5 || bad))
            {
                _away = false;
                if (!bad)
                {
                    _moment = (Pick(plays, "cheer", "jump", "play", "dance"), null, now + 2.5, "coffee");
                    _confetti = now;
                    // Back in the morning, not greeted yet today: a good morning in place of the hello.
                    var morning = senses is { Hour: >= 5 and < 11 } && _greeted != senses.Day;
                    if (morning)
                    {
                        _greeted = senses!.Day;
                    }

                    Say(now, morning ? "Good morning!" : "Welcome back!", "info", force: true);
                }
            }
        }

        if (_away)
        {
            Free = false;
            _meeting = null;
            return CharacterThought.None with { Action = Pick(plays, "sleep", "sit", "idle"), Effect = "zzz" };
        }

        if (_moment is { } done && now >= done.Until)
        {
            _moment = null;
        }

        double? toward = null;
        var facing = 0;
        (string? Effect, string? Prop) weather = (null, null);
        if (senses is not null)
        {
            Touch(now, senses, plays);
            var watching = Cursor(now, senses, plays, bad, ref toward, ref facing);
            Company(now, senses, plays, bad, watching, ref toward, ref facing);
            var free = !bad && _moment is null && _meeting is null && !watching;
            Clock(now, senses, plays, free);
            weather = Weather(now, senses, free && _moment is null);
            if (now < _holdUntil && senses.Line)
            {
                // Clicked, petted or noticing: it stays where it is meanwhile.
                toward ??= senses.X;
            }

            Free = !bad && _moment is null && _meeting is null && !watching;
        }
        else
        {
            Free = !bad && _moment is null;
        }

        string? action = null, effect = null, prop = null;
        if (_moment is { } m)
        {
            (action, effect, prop) = (m.Action, m.Effect, m.Prop);
        }
        else if (_meeting is { } meeting)
        {
            // A hello (hearts), then a dance together (notes).
            var together = now - meeting.Since >= 1.5;
            (action, effect) = together ? (Pick(plays, "dance", "play", "cheer", "jump"), "notes") : (null, "hearts");
        }

        var thought = new CharacterThought(action, effect, null, "info", 0, 0, status == "critical" && judged is not null ? 1 : 0, Prop: prop,
            Toward: toward, Facing: facing, Weather: weather.Effect, WeatherProp: weather.Prop);
        if (now - _confetti < CharacterParticles.ConfettiSeconds)
        {
            thought = thought with { Burst = "confetti", BurstAge = now - _confetti };
        }

        if (_bubble is { } b)
        {
            if (now - b.Since < b.Life)
            {
                thought = thought with { Bubble = b.Text, Tone = b.Tone, BubbleAge = now - b.Since, BubbleLife = b.Life };
            }
            else
            {
                _bubble = null;
            }
        }

        return thought;
    }

    /// <summary>A new click on it: a jump and a word (the first, the second, the third within <see cref="ClickWindow"/>), dizzy after more.</summary>
    private void Touch(double now, CharacterSenses s, IReadOnlyList<string>? plays)
    {
        if (_seenTouch is { } seen && s.Touches > seen)
        {
            _clicks.Enqueue(now);
            while (_clicks.Peek() < now - ClickWindow)
            {
                _clicks.Dequeue();
            }

            if (_clicks.Count > ClickLines.Length)
            {
                _moment = (Pick(plays, "sit", "idle"), "stars", now + 3, null);
                Say(now, "Whoa, I'm dizzy…", "high", force: true);
            }
            else
            {
                _moment = (Pick(plays, "jump", "play", "cheer"), null, now + 1.2, null);
                Say(now, ClickLines[_clicks.Count - 1], "info", force: true);
            }

            _holdUntil = _moment.Value.Until;
            _meeting = null;
            _seeking = null;
        }

        _seenTouch = s.Touches;
    }

    /// <summary>
    /// The mouse: petted when it is rubbed back and forth over it (whatever its reading says); otherwise, unless its
    /// reading is bad, a hop when the mouse comes near, then it faces it and stays, and comes over when it rests. True
    /// while it watches the mouse.
    /// </summary>
    private bool Cursor(double now, CharacterSenses s, IReadOnlyList<string>? plays, bool bad, ref double? toward, ref int facing)
    {
        if (s.CursorX is not { } cx || s.CursorY is not { } cy)
        {
            _near = false;
            _strokeWay = 0;
            _strokes.Clear();
            _watchUntil = double.NegativeInfinity;
            return false;
        }

        var h = Math.Max(1, s.Size);
        var dx = cx - s.X;
        var dy = cy - (s.Y - (h / 2));
        var way = Math.Abs(dx) > 0.15 * h ? Math.Sign(dx) : 0;

        // Petting: each turn of the mouse over it (a stroke one way, then back) counts.
        var over = Math.Abs(dx) <= 0.45 * h && cy >= s.Y - h && cy <= s.Y + (0.1 * h);
        if (!over)
        {
            _strokeWay = 0;
            _anchor = cx;
        }
        else if (double.IsNaN(_anchor))
        {
            _anchor = cx;
        }
        else if (_strokeWay == 0)
        {
            if (Math.Abs(cx - _anchor) >= PetStroke * h)
            {
                _strokeWay = Math.Sign(cx - _anchor);
                _anchor = cx;
            }
        }
        else if (Math.Sign(cx - _anchor) == _strokeWay)
        {
            _anchor = cx;
        }
        else if (Math.Abs(cx - _anchor) >= PetStroke * h)
        {
            _strokes.Enqueue(now);
            _strokeWay = -_strokeWay;
            _anchor = cx;
        }

        while (_strokes.Count > 0 && _strokes.Peek() < now - PetWindow)
        {
            _strokes.Dequeue();
        }

        if (_strokes.Count >= PetStrokes)
        {
            if (now >= _petUntil)
            {
                // A new petting (not the same one going on).
                Say(now, PetLines[_pets % PetLines.Length], "relief", force: true);
                _pets++;
            }

            _petUntil = now + 1.5;
            _holdUntil = _petUntil;
            _moment = (Pick(plays, "cheer", "dance", "play", "jump"), "hearts", _petUntil, null);
            _meeting = null;
            _seeking = null;
        }

        var near = Math.Abs(dx) <= NoticeReach * h && Math.Abs(dy) <= NoticeRise * h;
        if (Math.Abs(cx - _rest.X) > 0.25 * h || Math.Abs(cy - _rest.Y) > 0.25 * h || double.IsNaN(_rest.X))
        {
            _rest = (cx, cy, now);
        }

        if (bad)
        {
            // Its reading comes first.
            _near = near;
            _watchUntil = double.NegativeInfinity;
            facing = _moment is not null ? way : facing;
            return false;
        }

        if (near)
        {
            if (!_near && _moment is null && _meeting is null && now - _lastHop >= HopGap)
            {
                _lastHop = now;
                _moment = (Pick(plays, "jump", "play"), null, now + 1, null);
                _holdUntil = now + 1;
            }

            _watchUntil = now + 2;
        }

        _near = near;
        if (now >= _watchUntil && _moment is null)
        {
            return false;
        }

        facing = way;
        if (s.Line && now < _watchUntil)
        {
            var comes = near && now - _rest.Since >= RestSeconds && Math.Abs(dx) > (ComeGap * h) + 2 && Pick(plays, "walk", "run") is not null;
            toward = comes ? cx - (Math.Sign(dx) * ComeGap * h) : s.X;
        }

        return now < _watchUntil;
    }

    /// <summary>
    /// Another character on its line: two free ones this close meet (a hello each way, then a dance together); the first
    /// of the two (by id) walks over to the other now and then.
    /// </summary>
    private void Company(double now, CharacterSenses s, IReadOnlyList<string>? plays, bool bad, bool watching, ref double? toward, ref int facing)
    {
        var f = s.Friend;
        if (_meeting is { } m)
        {
            if (bad || f is null || f.Id != m.Friend || now - m.Since >= MeetSeconds || _moment is not null)
            {
                _met[m.Friend] = now;
                _meeting = null;
            }
            else
            {
                facing = f.X > s.X ? 1 : f.X < s.X ? -1 : facing;
                if (s.Line)
                {
                    toward = m.At;
                }

                if (!m.Speaker && !m.Answered && now - m.Since >= 1.2)
                {
                    Say(now, $"Hey {f.Name}!", "info", force: true);
                    _meeting = m with { Answered = true };
                }

                return;
            }
        }

        if (f is null || bad || _moment is not null || watching || !s.Line)
        {
            _seeking = null;
            return;
        }

        var h = (s.Size + f.Size) / 2;
        var gap = Math.Abs(f.X - s.X);
        var rested = !_met.TryGetValue(f.Id, out var last) || now - last >= MeetGap;
        var speaker = string.CompareOrdinal(s.Self, f.Id) < 0;
        if (gap <= MeetReach * h && rested && (f.Free || f.MeetingWith == s.Self))
        {
            _meeting = (f.Id, now, s.X, speaker, false);
            _seeking = null;
            facing = f.X > s.X ? 1 : f.X < s.X ? -1 : facing;
            toward = s.X;
            if (speaker)
            {
                Say(now, $"Hi {f.Name}!", "info", force: true);
            }

            return;
        }

        if (_seeking is { } k && (k.Friend != f.Id || now - k.Since >= 10 || !rested || !f.Free))
        {
            _seeking = null;
        }

        if (_seeking is null && speaker && rested && f.Free && gap <= SeekReach * h && now - _lastSeek >= SeekEvery && Pick(plays, "walk", "run") is not null)
        {
            _seeking = (f.Id, now);
            _lastSeek = now;
        }

        if (_seeking is not null)
        {
            toward = f.X - ((f.X > s.X ? 1 : -1) * h);
        }
    }

    /// <summary>The time of day: a good morning once a day (5 to 11), a yawn every <see cref="YawnEvery"/> seconds late at night (midnight to 5).</summary>
    private void Clock(double now, CharacterSenses s, IReadOnlyList<string>? plays, bool free)
    {
        if (s.Hour is not { } hour)
        {
            return;
        }

        _lastYawn ??= now;
        if (!free)
        {
            return;
        }

        if (hour is >= 5 and < 11 && _greeted != s.Day)
        {
            _greeted = s.Day;
            _moment = (Pick(plays, "cheer", "jump", "play", "dance"), null, now + 3, "coffee");
            Say(now, "Good morning!", "info", force: true);
        }
        else if (hour < 5 && now - _lastYawn >= YawnEvery)
        {
            _lastYawn = now;
            _moment = (Pick(plays, "tired", "sleep", "sit", "idle"), "zzz", now + 3, null);
            Say(now, "It's late… bedtime?", "info");
        }
    }

    /// <summary>The weather: said when it changes (while it is free), shown for <see cref="WeatherSpell"/> seconds of every <see cref="WeatherEvery"/>.</summary>
    private (string? Effect, string? Prop) Weather(double now, CharacterSenses s, bool free)
    {
        if (s.Weather is not { } w)
        {
            _weatherSaid = null;
            return (null, null);
        }

        var key = WeatherKey(w);
        if (_weatherSeen != w.Id)
        {
            _weatherSeen = w.Id;
            if (key != _weatherSaid)
            {
                _weatherSaid = key;
                _weatherSince = now;
                if (free)
                {
                    Say(now, WeatherText(key, w), "info");
                }
            }
        }

        if (!free || (now - _weatherSince) % WeatherEvery >= WeatherSpell)
        {
            return (null, null);
        }

        return key switch
        {
            "rain" or "storm" => ("rain", "umbrella"),
            "snow" => ("snow", null),
            "hot" => ("sweat", "desk-fan"),
            "cold" => (null, "coffee"),
            _ when w.Wind >= 10 => ("wind", null),
            _ => (null, null),
        };
    }

    /// <summary>How the weather is told apart for saying it: its kind (rain, snow, storm, fog), else hot (30 °C or more), cold (0 °C or less), clear or clouds.</summary>
    private static string WeatherKey(CharacterWeather w) => w.Kind switch
    {
        "rain" or "snow" or "storm" or "fog" => w.Kind,
        _ when w.Temperature >= 30 => "hot",
        _ when w.Temperature <= 0 => "cold",
        _ => w.Kind,
    };

    private static string WeatherText(string key, CharacterWeather w)
    {
        var t = (Math.Round(w.Temperature, MidpointRounding.AwayFromZero) + 0.0).ToString("0", CultureInfo.InvariantCulture);
        return key switch
        {
            "storm" => "Thunderstorm outside!",
            "rain" => $"Raining outside: {t} °C",
            "snow" => $"Snowing outside: {t} °C",
            "fog" => "Foggy outside",
            "hot" => $"Hot outside: {t} °C",
            "cold" => $"Brr, {t} °C outside",
            "clear" => w.Day ? $"Sunny outside: {t} °C" : $"Clear night: {t} °C",
            _ => $"Cloudy outside: {t} °C",
        };
    }

    /// <summary>The reading it reacts to, at a moment: a change of status said, a fast rise warned of.</summary>
    private void Watch(double now, ReadingJudged judged, string status, IReadOnlyList<string>? plays)
    {
        var value = judged.Source.Value ?? double.NaN;
        if (_source != judged.Source.Id)
        {
            // Another reading: its own history (a change of reading because another turned bad is said below).
            _samples.Clear();
            _source = judged.Source.Id;
        }

        if (double.IsFinite(value))
        {
            _samples.Enqueue((now, value));
            while (_samples.Count > 0 && _samples.Peek().At < now - TrendWindow)
            {
                _samples.Dequeue();
            }
        }

        var label = judged.Source.Label;
        var shown = Format(judged.Source);
        if (_status is { } was && was != status)
        {
            if (status == "critical")
            {
                Say(now, CriticalText(judged.Kind, label, shown), "critical", force: true);
            }
            else if (IsBad(judged.Kind, status))
            {
                Say(now, WarningText(judged.Kind, label, shown), "high");
            }
            else if (IsBad(judged.Kind, was))
            {
                // Back from critical it is always said (the end of an alarm), whatever the budget.
                Say(now, $"Phew, {label} back to {shown}", "relief", force: was == "critical");
                _moment = (Pick(plays, "cheer", "dance", "jump", "play"), "notes", now + 3, "trophy");
                _confetti = now;
            }
            else if (judged.Kind == "fps" && status == "high")
            {
                Say(now, $"{shown}! Smooth", "relief");
            }
        }

        _status = status;

        // A fast rise while still normal: a jump with "!" before it turns bad.
        if (status != "normal" || now - _lastTrend < TrendGap || _samples.Count < 2 || !OverlayReactions.Kinds.TryGetValue(judged.Kind, out var rules) || rules.BadDown)
        {
            return;
        }

        var critical = judged.Reading.Critical ?? rules.Critical;
        if (critical is not { } top || rules.Share is not null)
        {
            return;
        }

        var span = top - (rules.Low ?? 0);
        var lowest = _samples.Min(s => s.Value);
        if (span > 0 && value - lowest >= TrendShare * span && now - _samples.Peek().At >= 1)
        {
            _lastTrend = now;
            _moment = (Pick(plays, "worried", "jump", "run", "play"), "exclaim", now + 2, null);
            Say(now, $"{label} rising: {shown}", "high");
        }
    }

    /// <summary>An event, once: a moment and a bubble.</summary>
    private void React(double now, CharacterEvent e, IReadOnlyList<string>? plays)
    {
        switch (e.Kind)
        {
            case "incident":
                _moment = (Pick(plays, "panic", "jump", "run", "play"), "exclaim", now + 3, "toolbox");
                Say(now, e.Text is { Length: > 0 } title ? $"Incident saved: {Short(title)}" : "Incident saved", "critical", force: true);
                break;
            case "recording-on":
                _moment = (Pick(plays, "play", "jump", "dance"), null, now + 2, null);
                Say(now, "Recording started", "info", force: true);
                break;
            case "recording-off":
                Say(now, "Recording stopped", "info", force: true);
                break;
            case "game":
                _moment = (Pick(plays, "cheer", "dance", "jump", "play"), "notes", now + 3, "popcorn");
                _confetti = now;
                Say(now, e.Text is { Length: > 0 } game ? $"{Short(game)} started: have fun!" : "Game on: have fun!", "relief", force: true);
                break;
        }
    }

    /// <summary>A name or title cut to fit a bubble (28 characters, with an ellipsis).</summary>
    private static string Short(string text) => text.Length <= 28 ? text : text[..27].TrimEnd() + "…";

    private void Say(double now, string text, string tone, bool force = false)
    {
        if (!force && now - _lastBubble < BubbleGap)
        {
            return;
        }

        _bubble = (text, tone, now, tone == "critical" ? 5 : 4);
        _lastBubble = now;
    }

    /// <summary>Whether a status is the bad way for a kind: critical, or the warning side (high going up; low going down).</summary>
    public static bool IsBad(string kind, string status) =>
        status == "critical" || status == (OverlayReactions.Kinds.TryGetValue(kind, out var k) && k.BadDown ? "low" : "high");

    private static string CriticalText(string kind, string label, string value) => kind switch
    {
        "temperature" => $"{label} {value}: too hot!",
        "load" => $"{label} at {value}!",
        "fps" => $"{label} down to {value}!",
        _ => $"{label} {value}!",
    };

    private static string WarningText(string kind, string label, string value) => kind switch
    {
        "temperature" => $"{label} {value}, getting hot",
        "load" or "bandwidth" => $"{label} busy: {value}",
        "fps" => $"{label} dropped to {value}",
        _ => $"{label} {value}",
    };

    /// <summary>The first of the animations it uses (all of them when it has no list); null when none.</summary>
    private static string? Pick(IReadOnlyList<string>? plays, params string[] wanted) =>
        wanted.FirstOrDefault(a => plays is null || plays.Contains(a, StringComparer.Ordinal));

    /// <summary>A value as a bubble says it (short: whole degrees and percents, bytes in binary units).</summary>
    public static string Format(OverlaySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Value is not { } v || !double.IsFinite(v))
        {
            return source.Text ?? "?";
        }

        // (+ 0.0: never "-0")
        string Whole(double x) => (Math.Round(x, MidpointRounding.AwayFromZero) + 0.0).ToString("0", CultureInfo.InvariantCulture);
        string One(double x) => ((Math.Round(x * 10, MidpointRounding.AwayFromZero) / 10) + 0.0).ToString("0.0", CultureInfo.InvariantCulture);
        string Scaled(double x, string[] units)
        {
            var i = 0;
            while (Math.Abs(x) >= 1024 && i < units.Length - 1)
            {
                x /= 1024;
                i++;
            }

            return (i == 0 ? Whole(x) : One(x)) + " " + units[i];
        }

        return source.Unit switch
        {
            "B" => Scaled(v, ["B", "KB", "MB", "GB", "TB"]),
            "B/s" => Scaled(v, ["B/s", "KB/s", "MB/s", "GB/s"]),
            "ms" => One(v) + " ms",
            "" => Whole(v),
            var unit => Whole(v) + " " + unit,
        };
    }
}
