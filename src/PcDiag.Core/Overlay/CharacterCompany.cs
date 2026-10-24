namespace PcDiag.Core.Overlay;

/// <summary>
/// What a character senses at a moment beyond its reading (as the editor's characters/company.ts): where it is, the
/// mouse, clicks on it, the nearest other character on its line, the weather and the time of day.
/// </summary>
/// <param name="Self">Who it is among the characters of its screen (its panel's id and its own: which of two speaks first).</param>
/// <param name="X">Where its feet were last drawn, across (in the same pixels as the cursor and the others).</param>
/// <param name="Y">Where its feet were last drawn, down.</param>
/// <param name="Size">How tall it is drawn, in those pixels.</param>
/// <param name="CursorX">Where the mouse pointer is, across (null: not known, hidden, held by a game, or a USB screen).</param>
/// <param name="CursorY">Where the mouse pointer is, down.</param>
/// <param name="Touches">How many times it has been clicked (each new one is reacted to).</param>
/// <param name="Friend">The nearest other character on its line (null: none).</param>
/// <param name="Weather">The weather where the user is (null: not known or not wanted).</param>
/// <param name="Hour">The local time of day, 0-24 (null: not known).</param>
/// <param name="Day">The local day (a number, one more each day): a new day is greeted again.</param>
/// <param name="Line">It plays along a line (on top of or inside its panel, its screen's bottom or top, a spot): it can walk to things.</param>
public sealed record CharacterSenses(
    string Self,
    double X,
    double Y,
    double Size,
    double? CursorX = null,
    double? CursorY = null,
    long Touches = 0,
    CharacterFriend? Friend = null,
    CharacterWeather? Weather = null,
    double? Hour = null,
    int Day = 0,
    bool Line = true);

/// <summary>Another character near it on its line, as it was last drawn.</summary>
/// <param name="Id">Who it is (<see cref="CharacterSenses.Self"/>'s form).</param>
/// <param name="Name">Its character's name ("Mochi").</param>
/// <param name="X">Where its feet are, across.</param>
/// <param name="Size">How tall it is drawn.</param>
/// <param name="Free">It has nothing else to do (nothing bad, no moment, not meeting another, not watching the mouse).</param>
/// <param name="MeetingWith">Whom it is meeting now (null: no one).</param>
public sealed record CharacterFriend(string Id, string Name, double X, double Size, bool Free, string? MeetingWith);

/// <summary>The weather where the user is, for the characters.</summary>
/// <param name="Id">Which report (its time in seconds): a new one is looked at again.</param>
/// <param name="Kind">clear, clouds, fog, rain, snow or storm.</param>
/// <param name="Temperature">Air temperature, °C.</param>
/// <param name="Wind">Wind speed, m/s.</param>
/// <param name="Day">Daytime there (false: night).</param>
public sealed record CharacterWeather(long Id, string Kind, double Temperature, double Wind, bool Day);

/// <summary>A character as it was last drawn, for the others of its screen.</summary>
/// <param name="Line">It plays along a line (only those meet).</param>
public sealed record CharacterSpot(string Id, string Name, double X, double Y, double Size, bool Free, string? MeetingWith, bool Line);

/// <summary>
/// The characters of one screen as they were last drawn (as the editor's Company): each one's nearest other on its
/// line, so two can meet.
/// </summary>
public sealed class CharacterCompany
{
    private readonly Dictionary<string, CharacterSpot> _spots = new(StringComparer.Ordinal);

    /// <summary>Whether a place is along a line (on top of or inside its panel, its screen's bottom or top, a spot).</summary>
    public static bool IsLine(string place) => place is "top" or "inside" or "screen" or "spot";

    /// <summary>Where a character was drawn now.</summary>
    public void Set(CharacterSpot spot)
    {
        ArgumentNullException.ThrowIfNull(spot);
        _spots[spot.Id] = spot;
    }

    /// <summary>The characters no longer shown let go.</summary>
    public void Keep(IReadOnlySet<string> shown) =>
        _spots.Keys.Where(id => !shown.Contains(id)).ToList().ForEach(id => _spots.Remove(id));

    /// <summary>The nearest other character on <paramref name="self"/>'s line (its feet within a third of the taller one's height up or down), or null.</summary>
    public CharacterFriend? FriendOf(string self) => _spots.GetValueOrDefault(self) is { } me ? FriendOf(me, _spots.Values) : null;

    /// <summary>The nearest of <paramref name="others"/> on <paramref name="self"/>'s line, or null.</summary>
    public static CharacterFriend? FriendOf(CharacterSpot self, IEnumerable<CharacterSpot> others)
    {
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(others);
        if (!self.Line)
        {
            return null;
        }

        CharacterSpot? best = null;
        foreach (var o in others)
        {
            if (o.Id == self.Id || !o.Line || Math.Abs(o.Y - self.Y) > 0.35 * Math.Max(self.Size, o.Size))
            {
                continue;
            }

            if (best is null || Math.Abs(o.X - self.X) < Math.Abs(best.X - self.X))
            {
                best = o;
            }
        }

        return best is null ? null : new CharacterFriend(best.Id, best.Name, best.X, best.Size, best.Free, best.MeetingWith);
    }
}
