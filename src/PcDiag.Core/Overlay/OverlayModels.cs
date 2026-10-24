using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcDiag.Core.Configuration;

namespace PcDiag.Core.Overlay;

/// <summary>
/// Readings shown on the screens (an on-screen display): panels of readings placed on each monitor, drawn by the
/// recorder over everything else and never taking the mouse. Stored as overlay.json in the data folder (written by the
/// Web host, read by the recorder).
/// </summary>
/// <param name="Version">File format version.</param>
/// <param name="Enabled">Show the panels.</param>
/// <param name="Hotkey">Shows and hides the panels (empty: no hotkey).</param>
/// <param name="IntervalMs">How often the values refresh: 250, 500, 1000 or 2000 ms.</param>
/// <param name="HideFromCapture">Keep the panels out of screenshots, screen recordings and streams.</param>
/// <param name="Screens">Panels per monitor (also for monitors that are not connected now: they come back with it).</param>
/// <param name="ArrangeHotkey">Starts and ends arranging the panels on the screens themselves (empty: no hotkey).</param>
/// <param name="Devices">Settings of USB screens (screens that are not Windows monitors), by their key.</param>
/// <param name="Motion">Moving backgrounds move (off: every panel shows its still picture).</param>
/// <param name="PauseMotionInGames">No longer used (16 Oct 2026): a full-screen game keeps nothing still. Kept so older layouts read.</param>
/// <param name="Characters">Show the panels' animated characters (each panel says which it has).</param>
/// <param name="SmoothValues">
/// A new value glides there (numbers count, bars, rings and dials sweep) over about half a second instead of jumping;
/// off: each refresh shows it at once. Kept to jumps when motion is held still (games, battery saver…).
/// </param>
/// <param name="Weather">The place whose weather the characters show (null: none: nothing is fetched).</param>
public sealed record OverlayConfig(
    int Version = 1,
    bool Enabled = false,
    string Hotkey = OverlayConfig.DefaultHotkey,
    int IntervalMs = 1000,
    bool HideFromCapture = false,
    IReadOnlyList<OverlayScreen>? Screens = null,
    string ArrangeHotkey = OverlayConfig.DefaultArrangeHotkey,
    IReadOnlyList<OverlayDevice>? Devices = null,
    bool Motion = true,
    bool PauseMotionInGames = true,
    bool Characters = true,
    bool SmoothValues = true,
    OverlayWeather? Weather = null)
{
    /// <summary>A USB screen's settings (its defaults when it has none).</summary>
    public OverlayDevice DeviceOf(string key) => Devices?.FirstOrDefault(d => d.Key == key) ?? new OverlayDevice(key);

    /// <summary>The default show/hide hotkey.</summary>
    public const string DefaultHotkey = "Ctrl+Alt+Shift+O";

    /// <summary>The default hotkey for arranging the panels on the screens.</summary>
    public const string DefaultArrangeHotkey = "Ctrl+Alt+Shift+A";

    /// <summary>A layout with nothing on it.</summary>
    public static OverlayConfig Empty { get; } = new();

    /// <summary>The screens, never null.</summary>
    public IReadOnlyList<OverlayScreen> AllScreens => Screens ?? [];

    /// <summary>The panels of one monitor (its landscape ones; see <see cref="PanelsFor(string, bool)"/>).</summary>
    public IReadOnlyList<OverlayPanel> PanelsOf(string key) => ScreenOf(key)?.Panels ?? [];

    /// <summary>
    /// The panels a monitor shows in an orientation: its portrait panels while it is taller than wide, when it has
    /// them; otherwise its panels.
    /// </summary>
    public IReadOnlyList<OverlayPanel> PanelsFor(string key, bool portrait) => ScreenOf(key) is { } screen ? screen.PanelsFor(portrait) : [];

    /// <summary>The panels a monitor shows now (by its current shape).</summary>
    public IReadOnlyList<OverlayPanel> PanelsFor(OverlayDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return PanelsFor(display.Key, display.Height > display.Width);
    }

    /// <summary>Every panel of the layout, in both orientations (for what the whole layout uses: its pictures, its sources).</summary>
    public IEnumerable<OverlayPanel> EveryPanel() => AllScreens.SelectMany(s => s.EveryPanel());

    private OverlayScreen? ScreenOf(string key) => AllScreens.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>The stand-in panel a monitor's or USB screen's own characters play on now (see <see cref="OverlayScreen.Stage"/>).</summary>
    public OverlayPanel? StageFor(OverlayDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return ScreenOf(display.Key)?.Stage(display.Height > display.Width);
    }
}

/// <summary>The panels of one monitor.</summary>
/// <param name="Key">The monitor's stable key (from its device path: the same monitor on any port keeps it, turned too).</param>
/// <param name="Name">Its name when the layout was made (shown while it is not connected).</param>
/// <param name="Panels">Its panels (while it is wider than tall, and always when it has no portrait panels).</param>
/// <param name="Portrait">Its panels while it is taller than wide (turned to portrait); null: the same as <paramref name="Panels"/>.</param>
/// <param name="Characters">
/// Its own characters, apart from any panel's: along the screen's bottom ("screen") or its top ("top"), reacting to the
/// readings of every panel it shows.
/// </param>
public sealed record OverlayScreen(string Key, string? Name, IReadOnlyList<OverlayPanel> Panels, IReadOnlyList<OverlayPanel>? Portrait = null, IReadOnlyList<OverlayCharacter>? Characters = null)
{
    /// <summary>Where a screen's own characters play: along its bottom or its top.</summary>
    public static readonly IReadOnlySet<string> CharacterPlaces = new HashSet<string>(StringComparer.Ordinal) { "screen", "top", "spot" };

    /// <summary>The id of the stand-in panel a screen's own characters play on (the whole screen).</summary>
    public const string StageId = "~screen";

    /// <summary>
    /// The stand-in panel a screen's own characters play on: the whole screen, with every reading its panels show now (for
    /// reacting); null when it has none.
    /// </summary>
    public OverlayPanel? Stage(bool portrait) => Characters is { Count: > 0 } characters
        ? new OverlayPanel(StageId, null, 0, 0, "rows", [.. PanelsFor(portrait).Where(p => !p.Hidden).SelectMany(p => p.Readings).DistinctBy(r => r.Source, StringComparer.Ordinal)], new OverlayStyle(), Characters: characters)
        : null;

    /// <summary>The panels shown in an orientation.</summary>
    public IReadOnlyList<OverlayPanel> PanelsFor(bool portrait) => portrait && Portrait is { } tall ? tall : Panels;

    /// <summary>Its panels in both orientations.</summary>
    public IEnumerable<OverlayPanel> EveryPanel() => Portrait is null ? Panels : Panels.Concat(Portrait);
}

/// <summary>A box of readings on a monitor.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Title">Optional heading.</param>
/// <param name="X">Where it sits across the monitor: 0 at the left edge, 1 at the right edge (a share of the room the panel leaves).</param>
/// <param name="Y">Where it sits down the monitor: 0 at the top edge, 1 at the bottom edge.</param>
/// <param name="Layout">"rows" (one reading per line), "line" (all on one line) or "tiles" (a grid of tiles, each with a large value).</param>
/// <param name="Readings">What it shows, in order.</param>
/// <param name="Style">How it looks.</param>
/// <param name="Hidden">Kept in the layout but not shown.</param>
/// <param name="Columns">Columns of the tiles grid (1–6).</param>
/// <param name="Width">The panel's width at 100 % scaling (null: as wide as its readings need, never shrinking while shown).</param>
/// <param name="Height">The panel's height at 100 % scaling (null: as tall as its readings need).</param>
/// <param name="RowColumns">Columns of readings in the rows layout (1–4), filled left to right.</param>
/// <param name="Restore">Its place and size before it was maximized, put in a grid cell or fitted to the screen (to go back to).</param>
/// <param name="Movable">It can be dragged on the screen itself (a handle shows when the mouse rests on it).</param>
/// <param name="Resizable">It can be resized on the screen itself (a corner handle).</param>
/// <param name="Closable">It can be closed on the screen itself (hidden until shown again with the hotkey or the tray).</param>
/// <param name="Characters">Animated characters playing on, in or around it (at most 8; see <see cref="OverlayCharacter"/>).</param>
public sealed record OverlayPanel(
    string Id,
    string? Title,
    double X,
    double Y,
    string Layout,
    IReadOnlyList<OverlayReading> Readings,
    OverlayStyle Style,
    bool Hidden = false,
    int Columns = 2,
    double? Width = null,
    double? Height = null,
    int RowColumns = 1,
    OverlayPanelPlace? Restore = null,
    bool Movable = false,
    bool Resizable = false,
    bool Closable = false,
    IReadOnlyList<OverlayCharacter>? Characters = null,
    string? Design = null)
{
    /// <summary>Has handles on the screen itself.</summary>
    public bool Handles => Movable || Resizable || Closable;
}

/// <summary>A panel's place and size to go back to.</summary>
/// <param name="X">Its <see cref="OverlayPanel.X"/>.</param>
/// <param name="Y">Its <see cref="OverlayPanel.Y"/>.</param>
/// <param name="Width">Its fixed width (null: automatic).</param>
/// <param name="Height">Its fixed height (null: automatic).</param>
/// <param name="Size">Its text size.</param>
public sealed record OverlayPanelPlace(double X, double Y, double? Width, double? Height, double Size);

/// <summary>One reading in a panel.</summary>
/// <param name="Source">The value (see <see cref="OverlaySource.Id"/>).</param>
/// <param name="Label">Its label (the source's own when empty).</param>
/// <param name="Decimals">Decimals (the unit's usual when null).</param>
/// <param name="Warning">Shown in the warning color at or beyond this.</param>
/// <param name="Critical">Shown in the critical color at or beyond this.</param>
/// <param name="Below">Limits are crossed going down (free memory, fan speed) rather than up.</param>
/// <param name="Bar">A small bar under the value (older layouts; <paramref name="Visual"/> says it now).</param>
/// <param name="Max">The full scale of its bar, ring or graph (100 for percentages when null).</param>
/// <param name="Color">The reading's own color for its value, bar, ring and graph (the panel's when null).</param>
/// <param name="Visual">
/// What goes with the value (the same designs as the dashboard's widgets): none; dials — ring, arc, speed (a
/// speedometer), status (a light in the OK, warning or critical color); charts of its recent history — graph (area),
/// line, columns; under it — bar, thermo (a thermometer); text after it — stats (lowest, average, highest), trend (an
/// arrow and the change). Null follows <paramref name="Bar"/>.
/// </param>
/// <param name="Icon">Its icon (one of <see cref="OverlayIcons.Paths"/>), "none", or null to choose by what it measures.</param>
/// <param name="Show">How the value reads: "value" (null), "percent" of its total, "both", or "of" (used of total).</param>
/// <param name="ColSpan">Columns its tile takes in the tiles grid (1–6).</param>
/// <param name="RowSpan">Rows its tile takes in the tiles grid (1–4).</param>
/// <param name="Look">Its own look over the panel's: fonts, sizes, colors and its visual's (null follows the panel).</param>
public sealed record OverlayReading(
    string Source,
    string? Label = null,
    int? Decimals = null,
    double? Warning = null,
    double? Critical = null,
    bool Below = false,
    bool Bar = false,
    double? Max = null,
    string? Color = null,
    string? Visual = null,
    string? Icon = null,
    string? Show = null,
    int ColSpan = 1,
    int RowSpan = 1,
    OverlayReadingLook? Look = null);

/// <summary>A reading's own look, over its panel's (each null follows the panel's Look).</summary>
/// <param name="Font">Its font (one of <see cref="OverlayStyles.Fonts"/>).</param>
/// <param name="ValueSize">Its value's size against the panel's, 50–300 %.</param>
/// <param name="Bold">Its value in bold.</param>
/// <param name="ValueColor">Its value's color (its visual keeps <see cref="OverlayReading.Color"/>); limits still color it.</param>
/// <param name="LabelSize">Its label's size against the panel's, 50–250 %.</param>
/// <param name="LabelColor">Its label's color.</param>
/// <param name="UnitColor">Its unit's color.</param>
/// <param name="ShowLabel">Its label shown.</param>
/// <param name="ShowUnit">Its unit shown.</param>
/// <param name="Align">Its value on the left, center or right of its space.</param>
/// <param name="Vertical">Its value at the top, middle or bottom of its tile (see <see cref="OverlayStyle.ValueVertical"/>).</param>
/// <param name="VisualSize">Its dial's or chart's size, 50–250 %.</param>
/// <param name="Thickness">Its ring's, arc's, bar's, thermometer's or graph line's thickness, 25–400 %.</param>
/// <param name="TrackColor">The unfilled part of its ring, arc, bar or thermometer, and its chart's baseline.</param>
/// <param name="TrackOpacity">That track's opacity, 0–100 % (22 % when null).</param>
/// <param name="BarStyle">Its bar: solid, rounded or segments.</param>
/// <param name="FillOpacity">Its graph's area under the line, 0–100 % (43 % when null).</param>
public sealed record OverlayReadingLook(
    string? Font = null,
    int? ValueSize = null,
    bool? Bold = null,
    string? ValueColor = null,
    int? LabelSize = null,
    string? LabelColor = null,
    string? UnitColor = null,
    bool? ShowLabel = null,
    bool? ShowUnit = null,
    string? Align = null,
    int? VisualSize = null,
    int? Thickness = null,
    string? TrackColor = null,
    int? TrackOpacity = null,
    string? BarStyle = null,
    int? FillOpacity = null,
    string? Vertical = null);

/// <summary>How a panel looks. Sizes are at 100 % display scaling and grow with the monitor's scaling.</summary>
/// <param name="Font">Font family (one of <see cref="OverlayStyles.Fonts"/>).</param>
/// <param name="Size">Text size in pixels at 100 % scaling.</param>
/// <param name="Bold">Bold values.</param>
/// <param name="LabelColor">Label color (#RRGGBB).</param>
/// <param name="ValueColor">Value color.</param>
/// <param name="UnitColor">Unit color.</param>
/// <param name="TitleColor">Heading color.</param>
/// <param name="WarningColor">A value beyond its warning limit.</param>
/// <param name="CriticalColor">A value beyond its critical limit.</param>
/// <param name="Background">Background color.</param>
/// <param name="BackgroundOpacity">Background opacity, 0–100 %.</param>
/// <param name="Border">Border color (none when null).</param>
/// <param name="CornerRadius">Corner radius in pixels.</param>
/// <param name="Padding">Inner spacing in pixels.</param>
/// <param name="Opacity">The whole panel's opacity, 10–100 %.</param>
/// <param name="Outline">A dark outline around the text (readable over any picture).</param>
/// <param name="ShowLabels">Show labels.</param>
/// <param name="ShowUnits">Show units.</param>
/// <param name="Accent">The accent: bars, rings, graphs, icons, the accent edge and the title bar.</param>
/// <param name="Background2">A second background color: the background is a gradient from one to the other (none when null).</param>
/// <param name="GradientAngle">The gradient's direction in degrees, as in CSS (0 to the top, 90 to the right, 180 to the bottom).</param>
/// <param name="BorderWidth">Border width in pixels (0.5–6).</param>
/// <param name="Shadow">A soft shadow under the panel, 0–100 %.</param>
/// <param name="Glow">Values, icons and the border glow in their own color, 0–100 %.</param>
/// <param name="Sheen">A light sheen over the top of the panel (glass).</param>
/// <param name="AccentEdge">A stripe in the accent along one edge: none, left, top or bottom.</param>
/// <param name="TitleBar">The title on a band in the accent.</param>
/// <param name="Caps">Labels and the title in small spaced capitals.</param>
/// <param name="ValueScale">Values' size against the text size, 80–300 %.</param>
/// <param name="Spacing">Space between readings, 50–250 %.</param>
/// <param name="Icons">An icon before each reading.</param>
/// <param name="IconColor">Icons' color (the accent when null; warning and critical colors when a limit is crossed).</param>
/// <param name="BarStyle">Bars: solid, rounded or segments.</param>
/// <param name="BarHeight">Bars' and rings' thickness, 50–400 %.</param>
/// <param name="GraphSeconds">How much history a graph shows, 15–300 seconds.</param>
/// <param name="TileColor">Tiles' color (null: a light tint on a dark panel, a dark one on a light panel).</param>
/// <param name="TileOpacity">Tiles' opacity, 0–100 %.</param>
/// <param name="OkColor">A status light's color when the value is within its limits.</param>
/// <param name="Image">
/// A picture behind the readings: an id of <see cref="OverlayStyles.ImagePattern"/> (stored in the data folder's
/// overlay-images), a still (png, jpg) or an animation (anim: JPEG frames played in a loop); none when null.
/// </param>
/// <param name="ImageFit">How the picture fills the panel: cover, contain, stretch, tile or center.</param>
/// <param name="ImagePosition">Where it sits when it does not fill exactly: center, top, bottom, left or right.</param>
/// <param name="ImageOpacity">The picture's opacity, 0–100 % (over the background color).</param>
/// <param name="ImageBlur">The picture blurred, 0–30 pixels at 100 % scaling.</param>
/// <param name="ImageGray">The picture in grey.</param>
/// <param name="ImageTint">A color laid over the picture (to darken, lighten or color it), none when null.</param>
/// <param name="ImageTintOpacity">The tint's opacity, 0–100 %.</param>
/// <param name="Pattern">A pattern over the background: dots, grid, diagonal, carbon, hexagons or scanlines (none when null).</param>
/// <param name="PatternColor">The pattern's color (null: light on a dark panel, dark on a light one).</param>
/// <param name="PatternOpacity">The pattern's opacity, 0–100 %.</param>
/// <param name="PatternScale">The pattern's size, 50–400 %.</param>
/// <param name="ValueAlign">
/// Where a value sits in its space: left, center or right. Each reading's value has a fixed space (room for the widest it
/// can read), so a value changing never moves another.
/// </param>
/// <param name="ValueVertical">
/// Where a value sits up and down in its tile: top, middle or bottom (null: in the middle of a tile with the value alone,
/// at the top of one with a bar or thermometer under it). A dial's value stays in the dial, a chart's above the chart.
/// </param>
/// <param name="Motion">A moving background over its color and under the readings: none, aurora, grid, stars, pulse, scan or shimmer.</param>
/// <param name="MotionSpeed">How fast it moves, 25–300 %.</param>
/// <param name="Backdrop">
/// What is drawn behind each tile, fitted to it and to its dial wherever the tile is: none, plate (a metal plate with
/// screws), hud (corner brackets and a line under the label), bezel (a ring around a dial, a double frame around the
/// rest), socket (a recess), chevron (cut corners with a tab), glass (frosted with a highlight), neon (a glowing
/// outline) or blueprint (a fine grid in a frame).
/// </param>
/// <param name="Art">
/// The art its picture was drawn from, fitted to where its readings are (gauge faces behind its dials, grids behind its
/// graphs, frames and controls around its tiles): cockpit, racing, aviation, holo, synthwave, brass, terminal or lux;
/// a generated, moving scene with frosted-glass cards over its tiles: silk, mesh, aurora, nebula, caustics, bokeh, topo
/// or prism; or glass: frosted-glass cards over a picture of the library (<paramref name="ArtSource"/>). None when null.
/// The picture itself is <paramref name="Image"/> (an animation for the moving ones); the editor draws it again when the
/// panel changes size.
/// </param>
/// <param name="ArtSource">The library picture the glass art is drawn over (a still or an animation).</param>
public sealed record OverlayStyle(
    string Font = "Segoe UI",
    double Size = 14,
    bool Bold = true,
    string LabelColor = "#9CA3AF",
    string ValueColor = "#FFFFFF",
    string UnitColor = "#9CA3AF",
    string TitleColor = "#38BDF8",
    string WarningColor = "#F59E0B",
    string CriticalColor = "#EF4444",
    string Background = "#0B1220",
    int BackgroundOpacity = 70,
    string? Border = null,
    int CornerRadius = 8,
    int Padding = 10,
    int Opacity = 100,
    bool Outline = false,
    bool ShowLabels = true,
    bool ShowUnits = true,
    string Accent = "#38BDF8",
    string? Background2 = null,
    int GradientAngle = 135,
    double BorderWidth = 1,
    int Shadow = 0,
    int Glow = 0,
    bool Sheen = false,
    string AccentEdge = "none",
    bool TitleBar = false,
    bool Caps = false,
    int ValueScale = 100,
    int Spacing = 100,
    bool Icons = false,
    string? IconColor = null,
    string BarStyle = "rounded",
    int BarHeight = 100,
    int GraphSeconds = 60,
    string? TileColor = null,
    int TileOpacity = 7,
    string OkColor = "#34D399",
    string? Image = null,
    string ImageFit = "cover",
    string ImagePosition = "center",
    int ImageOpacity = 100,
    int ImageBlur = 0,
    bool ImageGray = false,
    string? ImageTint = null,
    int ImageTintOpacity = 35,
    string? Pattern = null,
    string? PatternColor = null,
    int PatternOpacity = 14,
    int PatternScale = 100,
    string ValueAlign = "center",
    string Motion = "none",
    int MotionSpeed = 100,
    string Backdrop = "none",
    string? Art = null,
    string? ArtSource = null,
    string? ValueVertical = null);

/// <summary>
/// A USB screen's settings: a screen that is not a Windows monitor (an LCD panel in a case or on a cooler), driven by
/// the recorder through a screen driver. Its panels are kept under its key like a monitor's.
/// </summary>
/// <param name="Key">Its key: usb:vid:pid:serial.</param>
/// <param name="Use">
/// Show the panels on it. The program that holds it otherwise (its vendor's, e.g. L-Connect) is paused while it is used
/// and started again after.
/// </param>
/// <param name="Rotation">How its picture is turned, in degrees clockwise (-1: as its model is usually mounted).</param>
/// <param name="Zoom">Its panels' size, 50–300 % (its pixels are often small).</param>
/// <param name="Brightness">Its backlight, 0–100 %.</param>
/// <param name="Background">The color behind its panels (it shows no desktop).</param>
/// <param name="MotionFps">
/// How many pictures a second its moving backgrounds and gliding values send, 1–30, and at most what its model takes
/// (each is drawn, encoded and sent over USB: the most costly thing the recorder does; fewer use less CPU).
/// </param>
public sealed record OverlayDevice(string Key, bool Use = false, int Rotation = -1, int Zoom = 100, int Brightness = 100, string Background = "#000000", int MotionFps = 30);

/// <summary>A monitor as the recorder sees it now, in desktop pixels.</summary>
/// <param name="Key">Its stable key (what <see cref="OverlayScreen.Key"/> refers to).</param>
/// <param name="Name">Its name, e.g. "PG32UCDP".</param>
/// <param name="Number">Its number, left to right then top to bottom (what Identify shows).</param>
/// <param name="X">Left edge on the desktop.</param>
/// <param name="Y">Top edge on the desktop.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Scale">Windows' display scaling (1.5 = 150 %).</param>
/// <param name="Primary">The main display.</param>
/// <param name="RefreshHz">Refresh rate.</param>
/// <param name="Kind">"monitor" (a Windows monitor) or "device" (a USB screen: not on the desktop; X and Y are 0).</param>
/// <param name="Status">A USB screen's state: "available", "busy" (another program holds it), "on" or "problem".</param>
/// <param name="Detail">More about it: the program holding it, the problem, its firmware.</param>
/// <param name="Model">A USB screen's model, e.g. "Lian Li Universal Screen 8.8″".</param>
/// <param name="Rotation">How far Windows turns the picture (0, 90, 180 or 270°), as set in the display settings.</param>
public sealed record OverlayDisplay(string Key, string Name, int Number, int X, int Y, int Width, int Height, double Scale, bool Primary, double? RefreshHz, string Kind = "monitor", string? Status = null, string? Detail = null, string? Model = null, int Rotation = 0)
{
    /// <summary>"portrait" while it is taller than wide (turned, or made so), otherwise "landscape".</summary>
    public string Orientation => Height > Width ? "portrait" : "landscape";
}

/// <summary>A monitor's area on the desktop as Windows reports it (physical pixels).</summary>
/// <param name="DeviceName">Its GDI device name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Primary">The main display.</param>
/// <param name="Scale">Display scaling (1.5 = 150 %).</param>
public sealed record MonitorArea(string DeviceName, int X, int Y, int Width, int Height, bool Primary, double Scale);

/// <summary>A value a panel can show, with its current value.</summary>
/// <param name="Id">Stable id, e.g. "cpu.usage", "gpu.{adapter key}.temp", "sensor.{logical key}".</param>
/// <param name="Label">Short label, e.g. "CPU".</param>
/// <param name="Group">Where it belongs, e.g. "Processor" or the GPU's name.</param>
/// <param name="Unit">Unit: %, °C, W, GHz, MHz, B (bytes, scaled), B/s (scaled), RPM, V… or empty.</param>
/// <param name="Value">The value now (null when unavailable or a text value).</param>
/// <param name="Text">A text value (the time, the recorder's state).</param>
/// <param name="Max">Its total when it has one (VRAM and memory in use: the installed amount), for percentages and "of".</param>
/// <param name="Room">
/// A text value's space: the widest texts it can read (the time as 00:00, the recorder's states), or a width in zeros for
/// a free text (an app's name), which is shortened with "…" to fit. Null: as wide as the text.
/// </param>
/// <param name="Loading">
/// Not read yet (the hardware sensors start a few seconds after the recorder): its name as last seen, and a loading
/// mark for its value.
/// </param>
public sealed record OverlaySource(string Id, string Label, string Group, string Unit, double? Value, string? Text = null, double? Max = null, IReadOnlyList<string>? Room = null, bool Loading = false);

/// <summary>What the on-screen display is doing, its monitors and the values it can show.</summary>
/// <param name="Running">The recorder can draw on this desktop.</param>
/// <param name="Visible">The panels are shown (enabled and not hidden with the hotkey).</param>
/// <param name="Problem">Why it cannot show (no interactive desktop, a hotkey taken by another program).</param>
/// <param name="Displays">The monitors now.</param>
/// <param name="Sources">The values and their current readings.</param>
/// <param name="Arranging">The panels are being arranged on the screens themselves (moved, resized, edited there).</param>
/// <param name="Revision">The saved layout's revision (the editor reloads it when it changes, as when the panels are arranged on the screens).</param>
public sealed record OverlayState(bool Running, bool Visible, string? Problem, IReadOnlyList<OverlayDisplay> Displays, IReadOnlyList<OverlaySource> Sources, bool Arranging = false, string? Revision = null);

/// <summary>The on-screen display, as the recorder controls it.</summary>
public interface IOverlayControl
{
    /// <summary>Its state, monitors and values (sampling starts so the values are current).</summary>
    Task<OverlayState> StateAsync(CancellationToken cancellationToken);

    /// <summary>Reads overlay.json again (after the Web host saved it).</summary>
    void Reload();

    /// <summary>Shows each monitor's number on it for a few seconds.</summary>
    void Identify();

    /// <summary>Starts or ends arranging the panels on the screens themselves.</summary>
    void Arrange(bool arranging);

    /// <summary>The panels are being arranged on the screens now.</summary>
    bool Arranging { get; }

    /// <summary>How many panels were closed on the screens themselves (hidden until shown again).</summary>
    int ClosedPanels { get; }

    /// <summary>Shows the panels closed on the screens again.</summary>
    void ShowClosed();

    /// <summary>The USB screens' driver packages found (built in and in the drivers folder).</summary>
    IReadOnlyList<ScreenDriverInfo> ScreenDrivers();

    /// <summary>Approves a driver package by its id and content hash, or withdraws it; the problem when it cannot be.</summary>
    string? ApproveScreenDriver(string id, string sha256, bool approve);

    /// <summary>Imports a driver package uploaded to the drivers' uploads folder: its id, or the problem.</summary>
    (string? Id, string? Problem) ImportDriver(string upload) => (null, "This recorder cannot import driver packages.");

    /// <summary>Removes a driver package (not a built-in one); the problem when it cannot be.</summary>
    string? RemoveDriver(string id) => "This recorder cannot remove driver packages.";
}

/// <summary>A USB screen driver package as the editor lists it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Publisher">Who made it.</param>
/// <param name="BuiltIn">It ships with the app (used without approval).</param>
/// <param name="Sha256">What an approval names (null for a built-in one).</param>
/// <param name="Approved">Approved (or built in).</param>
/// <param name="Loaded">In use by the screen host.</param>
/// <param name="Problem">Why it is not in use.</param>
/// <param name="Models">The screens it drives.</param>
/// <param name="Capabilities">What it is for: "screen", "sensors".</param>
/// <param name="Sensors">The sensors it reads (their names).</param>
public sealed record ScreenDriverInfo(string Id, string Name, string Version, string Publisher, bool BuiltIn, string? Sha256, bool Approved, bool Loaded, string? Problem, IReadOnlyList<string> Models, IReadOnlyList<string>? Capabilities = null, IReadOnlyList<string>? Sensors = null);

/// <summary>Limits, fonts, presets and checks for overlay layouts.</summary>
public static partial class OverlayStyles
{
    /// <summary>Most monitors in a layout.</summary>
    public const int MaxScreens = 16;

    /// <summary>Most panels per monitor.</summary>
    public const int MaxPanels = 12;

    /// <summary>Most readings per panel.</summary>
    public const int MaxReadings = 16;

    /// <summary>Longest title, label or name.</summary>
    public const int MaxText = 60;

    /// <summary>Longest source id or monitor key.</summary>
    public const int MaxKey = 300;

    /// <summary>Refresh intervals offered.</summary>
    public static readonly IReadOnlySet<int> Intervals = new HashSet<int> { 250, 500, 1000, 2000 };

    /// <summary>Fonts that every Windows 10/11 PC has.</summary>
    public static readonly IReadOnlyList<string> Fonts = ["Segoe UI", "Segoe UI Semibold", "Bahnschrift", "Consolas", "Cascadia Mono", "Arial", "Verdana", "Tahoma", "Calibri", "Courier New", "Impact"];

    /// <summary>Ready-made looks (the same as the editor's; each is an ordinary style that can be changed further).</summary>
    public static readonly IReadOnlyDictionary<string, OverlayStyle> Presets = new Dictionary<string, OverlayStyle>(StringComparer.Ordinal)
    {
        ["aurora"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#C7D2FE", ValueColor: "#FFFFFF", UnitColor: "#A5B4FC", TitleColor: "#E0E7FF", WarningColor: "#FBBF24", CriticalColor: "#FB7185",
            Background: "#1E1B4B", BackgroundOpacity: 80, CornerRadius: 14, Padding: 12, Accent: "#22D3EE", Background2: "#0E7490", GradientAngle: 135, Shadow: 60, Sheen: true, Caps: true,
            ValueScale: 120, Icons: true),
        ["glass"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#E2E8F0", ValueColor: "#FFFFFF", UnitColor: "#CBD5E1", TitleColor: "#FFFFFF", WarningColor: "#FCD34D", CriticalColor: "#FCA5A5",
            Background: "#0F172A", BackgroundOpacity: 45, Border: "#64748B", CornerRadius: 16, Padding: 12, Accent: "#60A5FA", Background2: "#334155", GradientAngle: 160, Shadow: 50,
            Sheen: true, Caps: true, ValueScale: 115, Icons: true),
        ["carbon"] = new(Font: "Bahnschrift", Size: 14, LabelColor: "#9CA3AF", ValueColor: "#F9FAFB", UnitColor: "#9CA3AF", TitleColor: "#F97316", Background: "#111827", BackgroundOpacity: 90,
            Border: "#374151", CornerRadius: 6, Padding: 10, Accent: "#F97316", Background2: "#1F2937", GradientAngle: 180, Shadow: 40, AccentEdge: "left", Caps: true, ValueScale: 110,
            Icons: true, BarStyle: "segments"),
        ["neon"] = new(Font: "Consolas", Size: 14, LabelColor: "#F472B6", ValueColor: "#22D3EE", UnitColor: "#A5F3FC", TitleColor: "#F472B6", Background: "#05010D", BackgroundOpacity: 72,
            Border: "#22D3EE", CornerRadius: 10, Accent: "#22D3EE", Background2: "#1A0B2E", GradientAngle: 160, BorderWidth: 1.5, Glow: 70, ValueScale: 115, Icons: true,
            IconColor: "#F472B6", BarStyle: "segments"),
        ["frost"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#475569", ValueColor: "#0F172A", UnitColor: "#64748B", TitleColor: "#0369A1", WarningColor: "#B45309", CriticalColor: "#B91C1C",
            Background: "#FFFFFF", BackgroundOpacity: 86, Border: "#CBD5E1", CornerRadius: 14, Padding: 12, Accent: "#0284C7", Background2: "#E2E8F0", GradientAngle: 180, Shadow: 35,
            Sheen: true, Caps: true, ValueScale: 115, Icons: true),
        ["terminal"] = new(Font: "Cascadia Mono", Size: 13, LabelColor: "#4ADE80", ValueColor: "#BBF7D0", UnitColor: "#86EFAC", TitleColor: "#22C55E", Background: "#020617", BackgroundOpacity: 84,
            Border: "#166534", CornerRadius: 4, Accent: "#22C55E", Glow: 35, Caps: true, BarStyle: "segments"),
        ["classic"] = new(Font: "Bahnschrift", Size: 15, LabelColor: "#F97316", ValueColor: "#FFFFFF", UnitColor: "#FDBA74", TitleColor: "#F97316", BackgroundOpacity: 0, Padding: 4, Outline: true,
            Accent: "#F97316", BarStyle: "solid"),
        ["minimal"] = new(Font: "Segoe UI", Size: 13, Bold: false, LabelColor: "#E5E7EB", ValueColor: "#FFFFFF", UnitColor: "#D1D5DB", BackgroundOpacity: 0, Padding: 4, Outline: true, Accent: "#E5E7EB"),
        ["card"] = new(),
        ["obsidian"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#CBD5E1", ValueColor: "#F8FAFC", UnitColor: "#94A3B8", TitleColor: "#DDD6FE", Background: "#0B0F19", BackgroundOpacity: 66,
            Border: "#334155", CornerRadius: 16, Padding: 12, Accent: "#A78BFA", Background2: "#1E293B", GradientAngle: 160, Shadow: 55, Glow: 15, Sheen: true, Caps: true, ValueScale: 120,
            Icons: true, IconColor: "#C4B5FD", TileOpacity: 6, Pattern: "dots", PatternOpacity: 9, PatternScale: 90),
        ["gold"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#A8A29E", ValueColor: "#FAFAF9", UnitColor: "#D4AF37", TitleColor: "#D4AF37", Background: "#141414", BackgroundOpacity: 86,
            Border: "#8B6F2E", CornerRadius: 10, Padding: 13, Accent: "#D4AF37", Background2: "#262626", GradientAngle: 160, Shadow: 50, Sheen: true, AccentEdge: "top", Caps: true,
            ValueScale: 115, Icons: true, IconColor: "#D4AF37"),
        ["nordic"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#D8DEE9", ValueColor: "#ECEFF4", UnitColor: "#81A1C1", TitleColor: "#88C0D0", WarningColor: "#EBCB8B", CriticalColor: "#BF616A",
            Background: "#2E3440", BackgroundOpacity: 88, CornerRadius: 12, Padding: 14, Accent: "#88C0D0", Background2: "#3B4252", GradientAngle: 180, Shadow: 35, Spacing: 115, Icons: true,
            IconColor: "#88C0D0", OkColor: "#A3BE8C"),
        ["ocean"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#CAF0F8", ValueColor: "#FFFFFF", UnitColor: "#ADE8F4", TitleColor: "#CAF0F8", Background: "#03045E", BackgroundOpacity: 80,
            CornerRadius: 16, Padding: 12, Accent: "#90E0EF", Background2: "#0077B6", GradientAngle: 165, Shadow: 50, Sheen: true, Caps: true, ValueScale: 120, Icons: true, IconColor: "#90E0EF",
            TileColor: "#CAF0F8", TileOpacity: 9, Pattern: "dots", PatternColor: "#90E0EF", PatternOpacity: 8),
        ["rose"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#FBCFE8", ValueColor: "#FFF1F2", UnitColor: "#FDA4AF", TitleColor: "#FBCFE8", Background: "#1A1023", BackgroundOpacity: 82,
            CornerRadius: 20, Padding: 12, Accent: "#FB7185", Background2: "#3A1C32", GradientAngle: 145, Shadow: 55, Sheen: true, Caps: true, ValueScale: 120, Icons: true, IconColor: "#FDA4AF",
            TileColor: "#FB7185", TileOpacity: 7),
        ["forest"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#BBF7D0", ValueColor: "#F0FDF4", UnitColor: "#86EFAC", TitleColor: "#BBF7D0", WarningColor: "#FDE047", CriticalColor: "#FCA5A5",
            Background: "#0B1F14", BackgroundOpacity: 84, CornerRadius: 14, Padding: 12, Accent: "#86EFAC", Background2: "#14532D", GradientAngle: 160, Shadow: 45, Caps: true, Icons: true,
            IconColor: "#86EFAC", Pattern: "hexagons", PatternColor: "#86EFAC", PatternOpacity: 7),
        ["paper"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#6B6358", ValueColor: "#2B2620", UnitColor: "#8A8174", TitleColor: "#2B2620", WarningColor: "#B45309", CriticalColor: "#B91C1C",
            Background: "#FAF7F0", BackgroundOpacity: 95, Border: "#E7E0D2", CornerRadius: 14, Padding: 14, Accent: "#C2410C", Background2: "#F1ECE1", GradientAngle: 180, Shadow: 30,
            Icons: true, IconColor: "#C2410C", OkColor: "#15803D", Pattern: "diagonal", PatternColor: "#D6CCB8", PatternOpacity: 12),
        ["holo"] = new(Font: "Bahnschrift", Size: 14, LabelColor: "#80F2FF", ValueColor: "#E0FCFF", UnitColor: "#00E5FF", TitleColor: "#80F2FF", Background: "#001018", BackgroundOpacity: 38,
            Border: "#00E5FF", CornerRadius: 4, Padding: 12, Accent: "#00E5FF", Glow: 45, Caps: true, ValueScale: 120, Icons: true, IconColor: "#00E5FF", TileColor: "#00E5FF", TileOpacity: 5,
            Pattern: "scanlines", PatternColor: "#00E5FF", PatternOpacity: 8),
        ["synthwave"] = new(Font: "Consolas", Size: 14, LabelColor: "#F0ABFC", ValueColor: "#67E8F9", UnitColor: "#F0ABFC", TitleColor: "#67E8F9", Background: "#12002B", BackgroundOpacity: 78,
            Border: "#FF2BD6", CornerRadius: 6, Padding: 12, Accent: "#FF2BD6", Background2: "#2A0845", GradientAngle: 135, BorderWidth: 1.5, Shadow: 35, Glow: 70, Caps: true, Icons: true,
            IconColor: "#FF2BD6", Pattern: "grid", PatternColor: "#FF2BD6", PatternOpacity: 11, PatternScale: 160),
        ["aurora-flow"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#C7D2FE", ValueColor: "#FFFFFF", UnitColor: "#A5B4FC", TitleColor: "#E0E7FF", WarningColor: "#FBBF24", CriticalColor: "#FB7185",
            Background: "#14123A", BackgroundOpacity: 82, CornerRadius: 16, Padding: 12, Accent: "#22D3EE", Background2: "#7C3AED", GradientAngle: 135, Shadow: 55, Caps: true, ValueScale: 120,
            Icons: true, TileOpacity: 7, Motion: "aurora"),
        ["cyber-grid"] = new(Font: "Bahnschrift", Size: 14, LabelColor: "#F0ABFC", ValueColor: "#67E8F9", UnitColor: "#F0ABFC", TitleColor: "#67E8F9", Background: "#0B0020", BackgroundOpacity: 80,
            Border: "#FF2BD6", CornerRadius: 6, Padding: 12, Accent: "#FF2BD6", Background2: "#2A0845", GradientAngle: 180, Glow: 50, Caps: true, Icons: true, IconColor: "#FF2BD6", Motion: "grid"),
        ["starfield"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#C7D2FE", ValueColor: "#FFFFFF", UnitColor: "#A5B4FC", TitleColor: "#E0E7FF", Background: "#050816", BackgroundOpacity: 86,
            CornerRadius: 14, Padding: 12, Accent: "#A5B4FC", Background2: "#111C3A", GradientAngle: 170, Shadow: 50, Caps: true, Icons: true, IconColor: "#A5B4FC", Motion: "stars", MotionSpeed: 60),
        ["pulse-core"] = new(Font: "Bahnschrift", Size: 14, LabelColor: "#FECDD3", ValueColor: "#FFF1F2", UnitColor: "#FDA4AF", TitleColor: "#FECDD3", Background: "#1A0507", BackgroundOpacity: 84,
            CornerRadius: 12, Padding: 12, Accent: "#F43F5E", Background2: "#3B0A12", GradientAngle: 160, Shadow: 50, Caps: true, Icons: true, IconColor: "#FB7185", Motion: "pulse"),
        ["scan-hud"] = new(Font: "Bahnschrift", Size: 14, LabelColor: "#80F2FF", ValueColor: "#E0FCFF", UnitColor: "#00E5FF", TitleColor: "#80F2FF", Background: "#001018", BackgroundOpacity: 55,
            Border: "#00E5FF", CornerRadius: 4, Padding: 12, Accent: "#00E5FF", Glow: 40, Caps: true, Icons: true, IconColor: "#00E5FF", TileColor: "#00E5FF", TileOpacity: 5, Motion: "scan"),
        ["liquid-glass"] = new(Font: "Segoe UI", Size: 14, LabelColor: "#E2E8F0", ValueColor: "#FFFFFF", UnitColor: "#CBD5E1", TitleColor: "#FFFFFF", Background: "#0F172A", BackgroundOpacity: 50,
            Border: "#94A3B8", CornerRadius: 18, Padding: 12, Accent: "#93C5FD", Background2: "#334155", GradientAngle: 150, Shadow: 45, Sheen: true, Caps: true, Icons: true, Motion: "shimmer",
            MotionSpeed: 80),
    };

    /// <summary>Layouts of a panel.</summary>
    public static readonly IReadOnlySet<string> Layouts = new HashSet<string>(StringComparer.Ordinal) { "rows", "line", "tiles" };

    /// <summary>What can go with a value.</summary>
    public static readonly IReadOnlySet<string> Visuals = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "bar", "ring", "graph", "arc", "speed", "thermo", "line", "columns", "stats", "status", "trend",
    };

    /// <summary>Dials, drawn beside the value (or filling a tile).</summary>
    public static readonly IReadOnlySet<string> Dials = new HashSet<string>(StringComparer.Ordinal) { "ring", "arc", "speed", "status" };

    /// <summary>Charts of the recent history.</summary>
    public static readonly IReadOnlySet<string> Charts = new HashSet<string>(StringComparer.Ordinal) { "graph", "line", "columns" };

    /// <summary>Drawn under the value.</summary>
    public static readonly IReadOnlySet<string> Unders = new HashSet<string>(StringComparer.Ordinal) { "bar", "thermo" };

    /// <summary>Text after the value.</summary>
    public static readonly IReadOnlySet<string> Texts = new HashSet<string>(StringComparer.Ordinal) { "stats", "trend" };

    /// <summary>Visuals that need the reading's recent values (the recorder keeps them while they are shown).</summary>
    public static bool NeedsHistory(string visual) => Charts.Contains(visual) || Texts.Contains(visual);

    /// <summary>Bar styles.</summary>
    public static readonly IReadOnlySet<string> BarStyles = new HashSet<string>(StringComparer.Ordinal) { "solid", "rounded", "segments" };

    /// <summary>How a background picture fills its panel.</summary>
    public static readonly IReadOnlySet<string> ImageFits = new HashSet<string>(StringComparer.Ordinal) { "cover", "contain", "stretch", "tile", "center" };

    /// <summary>Where a background picture sits.</summary>
    public static readonly IReadOnlySet<string> ImagePositions = new HashSet<string>(StringComparer.Ordinal) { "center", "top", "bottom", "left", "right" };

    /// <summary>The patterns a background can have.</summary>
    public static readonly IReadOnlySet<string> Patterns = new HashSet<string>(StringComparer.Ordinal) { "dots", "grid", "diagonal", "carbon", "hexagons", "scanlines" };

    /// <summary>A background picture's id: 16 hex digits of its content's hash and its type.</summary>
    public const string ImagePattern = "^[0-9a-f]{16}\\.(png|jpg|anim)$";

    /// <summary>Whether a text is a background picture's id.</summary>
    public static bool IsImageId(string? text) => text is not null && ImageId().IsMatch(text);

    /// <summary>How a value can read.</summary>
    public static readonly IReadOnlySet<string> Shows = new HashSet<string>(StringComparer.Ordinal) { "value", "percent", "both", "of" };

    /// <summary>Where the accent stripe goes.</summary>
    public static readonly IReadOnlySet<string> AccentEdges = new HashSet<string>(StringComparer.Ordinal) { "none", "left", "top", "bottom" };

    /// <summary>Where a value sits in its space.</summary>
    public static readonly IReadOnlySet<string> ValueAligns = new HashSet<string>(StringComparer.Ordinal) { "left", "center", "right" };

    /// <summary>Where a value sits up and down in its tile (see <see cref="OverlayStyle.ValueVertical"/>).</summary>
    public static readonly IReadOnlySet<string> ValueVerticals = new HashSet<string>(StringComparer.Ordinal) { "top", "middle", "bottom" };

    /// <summary>The moving backgrounds.</summary>
    public static readonly IReadOnlySet<string> Motions = new HashSet<string>(StringComparer.Ordinal) { "none", "aurora", "grid", "stars", "pulse", "scan", "shimmer" };

    /// <summary>The art a panel's picture can be drawn from.</summary>
    public static readonly IReadOnlySet<string> Arts = new HashSet<string>(StringComparer.Ordinal)
    {
        "cockpit", "racing", "aviation", "holo", "synthwave", "brass", "terminal", "lux",
        "silk", "mesh", "aurora", "nebula", "caustics", "bokeh", "topo", "prism", "ocean", "lava", "ink", "marble", "foil", "fireflies", "warp", "galaxy", "clouds", "snow", "ribbons", "circuit", "glass",
    };

    /// <summary>What can be drawn behind each tile.</summary>
    public static readonly IReadOnlySet<string> Backdrops = new HashSet<string>(StringComparer.Ordinal) { "none", "plate", "hud", "bezel", "socket", "chevron", "glass", "neon", "blueprint" };

    /// <summary>What goes with a reading's value (older layouts said only whether it had a bar).</summary>
    public static string VisualOf(OverlayReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Visual ?? (reading.Bar ? "bar" : "none");
    }

    /// <summary>A reading's icon: its own, none, or the one for what it measures.</summary>
    public static string? IconOf(OverlayReading reading, OverlaySource? source)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Icon switch
        {
            "none" => null,
            { } own when OverlayIcons.Paths.ContainsKey(own) => own,
            _ => OverlayIcons.Auto(reading.Source, source?.Unit),
        };
    }

    /// <summary>The full scale of a reading's bar, ring or graph (its own, the source's total, 100 for percentages), or null.</summary>
    public static double? MaxOf(OverlayReading reading, OverlaySource? source)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Max ?? source?.Max ?? (source?.Unit == "%" ? 100 : null);
    }

    /// <summary>
    /// The scale a ring, bar or dial uses when the reading has no full scale of its own: the next round number (1, 2,
    /// 2.5, 5 × 10ⁿ) above 1.25 × the value now, so it is drawn rather than left out; "Full at" sets a fixed one.
    /// </summary>
    public static double? AutoMax(OverlaySource? source)
    {
        if (source is not { Text: null, Value: { } value } || !double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        // Sizes in bytes are rounded in their own units (2 GB → 2.5 GB, not 5 000 000 000 bytes, "4.7 GB").
        var unit = source.Unit is "B" or "B/s" ? Math.Pow(1024, Math.Max(0, Math.Floor(Math.Log(value, 1024)))) : 1;
        var target = value / unit * 1.25;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var step in (double[])[1, 2, 2.5, 5, 10])
        {
            if (step * magnitude >= target)
            {
                return step * magnitude * unit;
            }
        }

        return 10 * magnitude * unit;
    }

    /// <summary>A loading value's mark, one to three dots by the second (the same width throughout).</summary>
    public static string LoadingMark(DateTime now) => (now.Second % 3) switch { 0 => "·  ", 1 => "·· ", _ => "···" };

    /// <summary>
    /// A name for a value never seen (shown while it loads, rather than its id): "sensor.lhm/gpu-nvidia/0/temperature/2"
    /// reads "GPU temperature", "gpu.{key}.vram" "VRAM", "net.{key}.down" "Download".
    /// </summary>
    public static string LabelOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        static string Kind(string part) => part.Split('-')[0].ToLowerInvariant() switch
        {
            "gpu" => "GPU",
            "cpu" => "CPU",
            "memory" or "ram" => "Memory",
            "nvme" or "hdd" or "ssd" or "storage" => "Drive",
            "lpc" or "motherboard" or "mainboard" => "Board",
            "nic" or "network" => "Network",
            _ => part.Length > 0 ? char.ToUpperInvariant(part[0]) + part[1..] : part,
        };

        if (id.StartsWith("sensor.", StringComparison.Ordinal))
        {
            var parts = id["sensor.".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var type = parts.LastOrDefault(p => !p.All(char.IsDigit)) ?? "sensor";
            var hardware = parts.Length > 1 ? Kind(parts.Skip(1).FirstOrDefault(p => !p.All(char.IsDigit)) ?? parts[0]) : string.Empty;
            return (hardware.Length > 0 && hardware != type ? hardware + " " : string.Empty) + type.Replace('_', ' ').ToLowerInvariant();
        }

        var last = id.Split('.')[^1];
        var head = Kind(id.Split('.')[0]);
        return last switch
        {
            "usage" or "load" => head,
            "temp" => head + " temp",
            "vram" => "VRAM",
            "vram_pct" => "VRAM",
            "down" => "Download",
            "up" => "Upload",
            "read" => "Read",
            "write" => "Write",
            "fps" => "FPS",
            "frametime" => "Frame time",
            _ => last.Length > 0 ? char.ToUpperInvariant(last[0]) + last[1..].Replace('_', ' ') : id,
        };
    }

    /// <summary>
    /// A reading as shown: its value as it chose to read — the value, a percentage of its total, both, or the value of
    /// its total ("22.1/24" GB) — falling back to the value when it has no total.
    /// </summary>
    public static (string Value, string Unit) Shown(OverlaySource source, OverlayReading reading)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reading);
        var show = reading.Show ?? "value";
        var max = reading.Max ?? source.Max;
        if (show == "value" || source.Text is not null || source.Value is not { } value || !double.IsFinite(value) || max is not > 0 || source.Unit == "%")
        {
            return Format(source, reading.Decimals);
        }

        var percent = Number(100 * value / max.Value, reading.Decimals ?? 0);
        switch (show)
        {
            case "percent":
                return (percent, "%");
            case "both":
            {
                var (v, u) = Format(source, reading.Decimals);
                return (v, (u.Length > 0 ? u + " · " : string.Empty) + percent + "%");
            }

            default:
            {
                // The value and its total in the total's unit: 22.1/24 GB.
                var (total, unit) = Format(source with { Value = max }, null);
                double scale = unit switch
                {
                    "TB" => 1L << 40,
                    "GB" or "GB/s" => 1L << 30,
                    "MB" or "MB/s" => 1L << 20,
                    "KB" or "KB/s" => 1024,
                    _ => 1,
                };
                var places = Math.Clamp(reading.Decimals ?? (total.Contains('.', StringComparison.Ordinal) ? total.Length - total.IndexOf('.', StringComparison.Ordinal) - 1 : 0), 0, 3);
                return ($"{Number(value / scale, places)}/{total}", unit);
            }
        }
    }

    /// <summary>
    /// The widest texts a reading's number can take over its range, digits as 0, and the units it can take: the room the
    /// value keeps, so a panel keeps its size whatever the values ("100" for a percentage, "00.0" and "000" for
    /// milliseconds, "000" frames a second, "000.0" MB/s or "00.00" GB/s for a rate). The range is its full scale, or
    /// what its unit usually reaches. Empty for a text (the time, an app's name); <paramref name="raw"/> for the number
    /// itself, not as the reading shows it (its lowest, average and highest). A text: its <see cref="OverlaySource.Room"/>.
    /// </summary>
    public static (IReadOnlyList<string> Values, IReadOnlyList<string> Units) Widest(OverlaySource source, OverlayReading reading, bool raw = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reading);
        if (source.Text is not null)
        {
            return (source.Room ?? [], []);
        }

        var top = (raw ? null : MaxOf(reading, source)) ?? Usual(source.Unit, source.Max);

        // Just under each place a number gains a digit or its unit changes (where it rounds up: 99.96 ms reads 100.0),
        // and the top. Plain numbers are counts: whole ones, and halves only when it has a fraction now.
        var plain = source.Unit is not ("B" or "B/s" or "ms" or "GHz" or "V" or "A" or "%" or "°C" or "W" or "MHz" or "RPM" or "fps");
        var probes = new List<double> { top };
        foreach (var scale in new[] { 1.0, 1024, 1L << 20, 1L << 30, 1L << 40 })
        {
            for (var b = 10.0; b <= 1e6; b *= 10)
            {
                var edge = b * scale;
                if (edge > top)
                {
                    continue;
                }

                probes.Add(plain ? edge - 1 : edge * (1 - 1e-9));
                if (plain && source.Value is { } now && Math.Abs(now - Math.Round(now)) >= 1e-9)
                {
                    probes.Add(edge - 0.5);
                }
            }
        }

        var values = new List<string>();
        var units = new List<string>();
        foreach (var probe in probes)
        {
            var probed = source with { Value = probe };
            var (value, unit) = raw ? Format(probed with { Max = null }, reading.Decimals) : Shown(probed, reading);
            value = Zeros(value);
            unit = Zeros(unit);
            if (!values.Contains(value))
            {
                values.Add(value);
            }

            if (unit.Length > 0 && !units.Contains(unit))
            {
                units.Add(unit);
            }
        }

        return (values, units);
    }

    /// <summary>What a unit's values usually reach, when the reading has no full scale.</summary>
    private static double Usual(string unit, double? max) => unit switch
    {
        "%" => 100,
        "fps" or "ms" or "W" => 999,
        "°C" => 100,
        "MHz" or "RPM" => 9999,
        "GHz" => 9.99,
        "V" => 9.999,
        "A" => 99.99,
        "B" => max is > 0 ? max.Value : 999.0 * (1L << 30),
        "B/s" => 99.0 * (1L << 30),
        _ => 99,
    };

    /// <summary>A text with its digits as 0 (digits are measured as wide as a 0).</summary>
    public static string Zeros(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, (span, t) =>
        {
            for (var i = 0; i < t.Length; i++)
            {
                span[i] = char.IsAsciiDigit(t[i]) ? '0' : t[i];
            }
        });
    }

    /// <summary>
    /// The value with its total that a percentage is of, by source id (RAM in use for RAM's percentage, VRAM in use for
    /// VRAM's), or null: a percentage that is only one (CPU load).
    /// </summary>
    public static string? ValueOf(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return sourceId == "memory.usage" ? "memory.used"
            : sourceId.EndsWith(".vram_pct", StringComparison.Ordinal) ? sourceId[..^"_pct".Length]
            : null;
    }

    /// <summary>How a reading reads now: its value, a percentage, both or of its total (a percentage reads as one).</summary>
    public static string ReadsAs(OverlayReading reading, OverlaySource? source)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return source?.Unit == "%" ? "percent" : reading.Show ?? "value";
    }

    /// <summary>
    /// A reading set to read as its value, a percentage, both or of its total. A percentage of a value with its total
    /// (RAM, VRAM) reads as anything else from that value's source, its limits and full scale turned into the value's
    /// units (80 % of 24 GB is 19.2 GB), and back.
    /// </summary>
    public static OverlayReading ReadAs(OverlayReading reading, OverlaySource? source, OverlaySource? valueSource, string show)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(show);
        if (source is { Unit: "%" } && show != "percent" && valueSource is { Max: > 0 } value && ValueOf(source.Id) == value.Id)
        {
            double? Of(double? percent) => percent is { } p ? Math.Round(p / 100 * value.Max!.Value) : null;
            return reading with { Source = value.Id, Show = show == "value" ? null : show, Warning = Of(reading.Warning), Critical = Of(reading.Critical), Max = Of(reading.Max) };
        }

        // A percentage already reads as one.
        return reading with { Show = show == "value" || (show == "percent" && source?.Unit == "%") ? null : show };
    }

    /// <summary>The contrast labels and units keep against a panel's background (WCAG's for small text).</summary>
    public const double ReadableContrast = 4.5;

    /// <summary>
    /// A text color made readable on the panel: moved toward the value color (or white or black) only as far as it takes
    /// to reach <see cref="ReadableContrast"/> against the background (and the gradient's second color). A panel with
    /// little background (under 40 %) shows what is behind it instead, so its colors are kept (its outline helps).
    /// </summary>
    public static string Readable(string color, OverlayStyle style)
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(style);
        if (style.BackgroundOpacity < 40 || !IsColor(color) || !IsColor(style.Background))
        {
            return color;
        }

        var readable = Readable(color, style.Background, style.ValueColor);
        return style.Background2 is { } second && IsColor(second) ? Readable(readable, second, style.ValueColor) : readable;
    }

    /// <summary>A color moved toward another (or white or black) until it has the contrast against a background.</summary>
    public static string Readable(string color, string background, string toward, double ratio = ReadableContrast)
    {
        ArgumentNullException.ThrowIfNull(color);
        var (c, b) = (Rgb(color), Rgb(background));
        if (Contrast(c, b) >= ratio)
        {
            return color;
        }

        var target = IsColor(toward) && Contrast(Rgb(toward), b) >= ratio ? Rgb(toward) : Luminance(b) < 0.5 ? (255, 255, 255) : (0, 0, 0);
        double low = 0, high = 1;
        for (var i = 0; i < 20; i++)
        {
            var mid = (low + high) / 2;
            if (Contrast(Mix(c, target, mid), b) >= ratio)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        var (r, g, bl) = Mix(c, target, high);
        return $"#{r:X2}{g:X2}{bl:X2}";
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static (int R, int G, int B) Mix((int R, int G, int B) a, (int R, int G, int B) b, double t) =>
        ((int)Math.Round(a.R + ((b.R - a.R) * t)), (int)Math.Round(a.G + ((b.G - a.G) * t)), (int)Math.Round(a.B + ((b.B - a.B) * t)));

    /// <summary>Relative luminance (WCAG), 0 to 1.</summary>
    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>A number with fixed decimals, halves away from zero (as the editor's preview rounds).</summary>
    private static string Number(double value, int places)
    {
        places = Math.Clamp(places, 0, 3);
        return Math.Round(value, places, MidpointRounding.AwayFromZero).ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The problem with a layout, or null when it is fine.</summary>
    public static string? Validate(OverlayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Intervals.Contains(config.IntervalMs))
        {
            return "intervalMs must be 250, 500, 1000 or 2000.";
        }

        if (!string.IsNullOrWhiteSpace(config.Hotkey) && HotkeyGesture.TryParse(config.Hotkey, out var hotkeyProblem) is null)
        {
            return "hotkey: " + hotkeyProblem;
        }

        if (!string.IsNullOrWhiteSpace(config.ArrangeHotkey))
        {
            if (HotkeyGesture.TryParse(config.ArrangeHotkey, out var arrangeProblem) is not { } arrange)
            {
                return "arrangeHotkey: " + arrangeProblem;
            }

            if (HotkeyGesture.TryParse(config.Hotkey, out _) is { } show && show.Modifiers == arrange.Modifiers && show.VirtualKey == arrange.VirtualKey)
            {
                return "arrangeHotkey must differ from hotkey.";
            }
        }

        var screens = config.AllScreens;
        if (screens.Count > MaxScreens)
        {
            return $"at most {MaxScreens} screens.";
        }

        if (screens.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != screens.Count)
        {
            return "each screen is listed once.";
        }

        if (config.Weather?.Check() is { } weatherProblem)
        {
            return weatherProblem;
        }

        foreach (var screen in screens)
        {
            if (Key(screen.Key) is { } keyProblem)
            {
                return "screen key " + keyProblem;
            }

            if (Text(screen.Name) is { } nameProblem)
            {
                return "screen name " + nameProblem;
            }

            if (screen.Panels is null || screen.Panels.Count > MaxPanels || screen.Portrait is { Count: > MaxPanels })
            {
                return $"at most {MaxPanels} panels per screen (in each orientation).";
            }

            // Unique across both orientations: a panel is found by its id whichever list holds it.
            var every = screen.EveryPanel().ToList();
            if (every.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != every.Count)
            {
                return "panel ids are unique on a screen.";
            }

            foreach (var panel in every)
            {
                if (Panel(panel) is { } panelProblem)
                {
                    return $"panel {panel.Id}: {panelProblem}";
                }
            }

            if (screen.Characters is { } characters)
            {
                if (OverlayCharacters.Check(new OverlayPanel(OverlayScreen.StageId, null, 0, 0, "rows", [], new OverlayStyle(), Characters: characters)) is { } characterProblem)
                {
                    return "screen characters: " + characterProblem;
                }

                if (characters.Any(c => !OverlayScreen.CharacterPlaces.Contains(c.Place)))
                {
                    return "screen characters: a screen's own character plays along its bottom (screen) or its top (top).";
                }
            }
        }

        var devices = config.Devices ?? [];
        if (devices.Count > MaxScreens || devices.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count() != devices.Count)
        {
            return $"at most {MaxScreens} USB screens, each listed once.";
        }

        foreach (var device in devices)
        {
            if (Key(device.Key) is { } keyProblem)
            {
                return "USB screen key " + keyProblem;
            }

            if (device.Rotation is not (-1 or 0 or 90 or 180 or 270) || device.Zoom is < 50 or > 300 || device.Brightness is < 0 or > 100 || !IsColor(device.Background) || device.MotionFps is < 1 or > 30)
            {
                return "a USB screen's rotation is -1, 0, 90, 180 or 270, its zoom 50-300, brightness 0-100, background #RRGGBB and motion 1-30 pictures a second.";
            }
        }

        return null;
    }

    /// <summary>Reads a layout file; a missing or broken file gives the empty layout (and the problem).</summary>
    public static OverlayConfig Read(string path, out string? problem)
    {
        TryRead(path, out var config, out problem);
        return config;
    }

    /// <summary>
    /// Reads a layout file: false when it could not be read just now (another program replacing it), so the caller can
    /// keep the layout it has; true otherwise, with the empty layout and the problem when the file is missing or wrong.
    /// </summary>
    public static bool TryRead(string path, out OverlayConfig config, out string? problem)
    {
        problem = null;
        config = OverlayConfig.Empty;
        string? text;
        try
        {
            text = ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }

        if (text is null)
        {
            return true;
        }

        try
        {
            var read = JsonSerializer.Deserialize<OverlayConfig>(text, Json) ?? OverlayConfig.Empty;
            problem = Validate(read);
            config = problem is null ? read : OverlayConfig.Empty;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            problem = ex.Message;
        }

        return true;
    }

    /// <summary>
    /// A file's text (null when there is none) read without stopping another program from replacing it meanwhile (the
    /// recorder and the Web host both read and write the layout), tried again briefly while it is being replaced.
    /// </summary>
    public static string? ReadShared(string path)
    {
        var bytes = ReadBytesShared(path);
        if (bytes is null)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static byte[]? ReadBytesShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(40);
            }
        }
    }

    /// <summary>
    /// Writes a layout file atomically: to a temporary file of its own (the recorder and the Web host may save at once),
    /// renamed over the layout, tried again briefly while another program has the layout open.
    /// </summary>
    public static void Write(string path, OverlayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(30);
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind: harmless, it is never read.
            }
        }
    }

    /// <summary>The JSON of a layout as stored.</summary>
    public static string Serialize(OverlayConfig config) => JsonSerializer.Serialize(config, Json);

    /// <summary>A short fingerprint of a layout file's content (it changes whenever the layout does).</summary>
    public static string Revision(string path)
    {
        try
        {
            return ReadBytesShared(path) is { } bytes ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// The layout with one panel changed (<paramref name="change"/> returns the new panel, or null to delete it);
    /// unchanged when there is no such panel.
    /// </summary>
    public static OverlayConfig WithPanel(this OverlayConfig config, string screen, string panelId, Func<OverlayPanel, OverlayPanel?> change)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(change);
        var screens = config.AllScreens.Select(s =>
        {
            if (!string.Equals(s.Key, screen, StringComparison.Ordinal))
            {
                return s;
            }

            // The panel in whichever orientation's list holds it.
            List<OverlayPanel> Changed(IEnumerable<OverlayPanel> list) => [.. list.Select(p => string.Equals(p.Id, panelId, StringComparison.Ordinal) ? change(p) : p).OfType<OverlayPanel>()];
            return s with { Panels = Changed(s.Panels), Portrait = s.Portrait is { } tall ? Changed(tall) : null };
        }).ToList();
        return config with { Screens = screens };
    }

    /// <summary>A value as shown: its number and its unit, scaled (bytes to GB, bytes per second to MB/s…).</summary>
    public static (string Value, string Unit) Format(OverlaySource source, int? decimals)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Text is { } text)
        {
            return (text, string.Empty);
        }

        if (source.Value is not { } value || !double.IsFinite(value))
        {
            return ("—", source.Unit is "B" or "B/s" ? string.Empty : source.Unit);
        }

        // Halves round away from zero, as the browser's preview does (7.25 → 7.3).
        string N(double v, int d)
        {
            var places = Math.Clamp(decimals ?? d, 0, 3);
            return Math.Round(v, places, MidpointRounding.AwayFromZero).ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        // The next unit once a number would read 1000 (1000 MB reads 0.98 GB), so a value never takes four digits.
        (string, string) Scaled(params (string Unit, double Scale, int Places)[] steps)
        {
            foreach (var (unit, scale, places) in steps)
            {
                if (unit == steps[^1].Unit || Math.Abs(Math.Round(value / scale, Math.Clamp(decimals ?? places, 0, 3), MidpointRounding.AwayFromZero)) < 1000)
                {
                    return (N(value / scale, places), unit);
                }
            }

            throw new UnreachableException();
        }

        switch (source.Unit)
        {
            case "B":
                return Scaled(("KB", 1024, 0), ("MB", 1L << 20, 0), ("GB", 1L << 30, 1), ("TB", 1L << 40, 2));
            case "B/s":
                return Scaled(("KB/s", 1024, 0), ("MB/s", 1L << 20, 1), ("GB/s", 1L << 30, 2));
            case "GHz":
                return (N(value, 2), "GHz");
            case "V":
                return (N(value, 3), "V");
            case "A":
                return (N(value, 2), "A");
            case "ms":
                // A tenth under 100 (as rounded: 99.96 reads 100, not 100.0).
                return (N(value, Math.Abs(Math.Round(value, 1, MidpointRounding.AwayFromZero)) < 100 ? 1 : 0), source.Unit);
            case "%" or "°C" or "W" or "MHz" or "RPM" or "fps":
                return (N(value, 0), source.Unit);
            default:
                return (N(value, Math.Abs(value - Math.Round(value)) < 1e-9 ? 0 : 1), source.Unit);
        }
    }

    /// <summary>How far a value is: 0 normal, 1 warning, 2 critical (a reading shown as a percentage of <paramref name="total"/> by that percentage).</summary>
    public static int Level(OverlayReading reading, double? value, double? total = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (value is not { } v)
        {
            return 0;
        }

        // A reading shown as a percentage of its total (VRAM in bytes) is judged by that percentage: its limits are one.
        if (reading.Show == "percent" && total is > 0)
        {
            v = v / total.Value * 100;
        }

        bool Beyond(double? limit) => limit is { } l && (reading.Below ? v <= l : v >= l);
        return Beyond(reading.Critical) ? 2 : Beyond(reading.Warning) ? 1 : 0;
    }

    private static string? Panel(OverlayPanel panel)
    {
        if (string.IsNullOrWhiteSpace(panel.Id) || panel.Id.Length > 64 || !PanelId().IsMatch(panel.Id))
        {
            return "id is 1-64 letters, digits, - or _.";
        }

        if (OverlayCharacters.Check(panel) is { } characterProblem)
        {
            return "characters: " + characterProblem;
        }

        if (Text(panel.Title) is { } titleProblem)
        {
            return "title " + titleProblem;
        }

        if (!double.IsFinite(panel.X) || !double.IsFinite(panel.Y) || panel.X is < 0 or > 1 || panel.Y is < 0 or > 1)
        {
            return "x and y are between 0 and 1.";
        }

        if (!Layouts.Contains(panel.Layout ?? string.Empty))
        {
            return "layout is rows, line or tiles.";
        }

        if (panel.Columns is < 1 or > 6 || panel.RowColumns is < 1 or > 4)
        {
            return "columns are 1-6 (tiles) and 1-4 (rows).";
        }

        if (panel.Width is { } width && (!double.IsFinite(width) || width is < 40 or > 4000) || panel.Height is { } height && (!double.IsFinite(height) || height is < 20 or > 3000))
        {
            return "width is 40-4000 and height 20-3000 (or automatic).";
        }

        if (panel.Design is { } design && (design.Length > 64 || design.Any(char.IsControl)))
        {
            return "the ready-made design it came from is named in at most 64 characters.";
        }

        if (panel.Restore is { } restore
            && (!double.IsFinite(restore.X) || !double.IsFinite(restore.Y) || restore.X is < 0 or > 1 || restore.Y is < 0 or > 1
                || restore.Width is { } rw && (!double.IsFinite(rw) || rw is < 40 or > 4000) || restore.Height is { } rh && (!double.IsFinite(rh) || rh is < 20 or > 3000)
                || !double.IsFinite(restore.Size) || restore.Size is < 6 or > 72))
        {
            return "the place to restore is within the screen, its size within the panel limits.";
        }

        if (panel.Readings is null || panel.Readings.Count > MaxReadings)
        {
            return $"at most {MaxReadings} readings.";
        }

        foreach (var reading in panel.Readings)
        {
            if (Key(reading.Source) is { } sourceProblem)
            {
                return "source " + sourceProblem;
            }

            if (Text(reading.Label) is { } labelProblem)
            {
                return "label " + labelProblem;
            }

            if (reading.Decimals is < 0 or > 3)
            {
                return "decimals are 0-3.";
            }

            if (new[] { reading.Warning, reading.Critical, reading.Max }.Any(v => v is { } x && !double.IsFinite(x)) || reading.Max is <= 0)
            {
                return "limits are numbers (a bar's full scale above 0).";
            }

            if (reading.Color is { } color && !IsColor(color))
            {
                return "color is #RRGGBB.";
            }

            if (reading.Visual is { } visual && !Visuals.Contains(visual))
            {
                return "visual is one of " + string.Join(", ", Visuals) + ".";
            }

            if (reading.Icon is { } icon && icon != "none" && !OverlayIcons.Paths.ContainsKey(icon))
            {
                return "icon is none or one of " + string.Join(", ", OverlayIcons.Paths.Keys) + ".";
            }

            if (reading.Show is { } show && !Shows.Contains(show))
            {
                return "show is value, percent, both or of.";
            }

            if (reading.ColSpan is < 1 or > 6 || reading.RowSpan is < 1 or > 4)
            {
                return "a tile spans 1-6 columns and 1-4 rows.";
            }

            if (reading.Look is { } look && Look(look) is { } lookError)
            {
                return "look: " + lookError;
            }
        }

        return Style(panel.Style);
    }

    private static string? Look(OverlayReadingLook look)
    {
        if (look.Font is { } font && !Fonts.Contains(font))
        {
            return "font is one of " + string.Join(", ", Fonts) + ".";
        }

        if (look.ValueSize is < 50 or > 300 || look.LabelSize is < 50 or > 250 || look.VisualSize is < 50 or > 250 || look.Thickness is < 25 or > 400)
        {
            return "value size is 50-300 %, label and visual sizes 50-250 %, thickness 25-400 %.";
        }

        if (look.TrackOpacity is < 0 or > 100 || look.FillOpacity is < 0 or > 100)
        {
            return "opacities are 0-100 %.";
        }

        if (new[] { look.ValueColor, look.LabelColor, look.UnitColor, look.TrackColor }.Any(c => c is not null && !IsColor(c)))
        {
            return "colors are #RRGGBB.";
        }

        if ((look.Align is { } align && !ValueAligns.Contains(align)) || (look.BarStyle is { } bar && !BarStyles.Contains(bar)) || (look.Vertical is { } vertical && !ValueVerticals.Contains(vertical)))
        {
            return "align is left, center or right; vertical top, middle or bottom; bar style solid, rounded or segments.";
        }

        return null;
    }

    private static string? Style(OverlayStyle? style)
    {
        if (style is null)
        {
            return "style is required.";
        }

        if (!Fonts.Contains(style.Font))
        {
            return "font is one of " + string.Join(", ", Fonts) + ".";
        }

        if (!double.IsFinite(style.Size) || style.Size is < 6 or > 72)
        {
            return "size is 6-72.";
        }

        var colors = new[] { style.LabelColor, style.ValueColor, style.UnitColor, style.TitleColor, style.WarningColor, style.CriticalColor, style.Background, style.Accent, style.OkColor };
        if (colors.Any(c => !IsColor(c)) || new[] { style.Border, style.Background2, style.IconColor }.Any(c => c is not null && !IsColor(c)))
        {
            return "colors are #RRGGBB.";
        }

        if (style.BackgroundOpacity is < 0 or > 100 || style.Opacity is < 10 or > 100 || style.CornerRadius is < 0 or > 32 || style.Padding is < 0 or > 40)
        {
            return "background opacity 0-100, opacity 10-100, corner radius 0-32, padding 0-40.";
        }

        if (style.GradientAngle is < 0 or > 360 || !double.IsFinite(style.BorderWidth) || style.BorderWidth is < 0.5 or > 6 || style.Shadow is < 0 or > 100 || style.Glow is < 0 or > 100)
        {
            return "gradient angle 0-360, border width 0.5-6, shadow and glow 0-100.";
        }

        if (style.ValueScale is < 80 or > 300 || style.Spacing is < 50 or > 250 || style.BarHeight is < 50 or > 400 || style.GraphSeconds is < 15 or > 300)
        {
            return "value size 80-300, spacing 50-250, bar thickness 50-400, graph 15-300 seconds.";
        }

        if (style.TileOpacity is < 0 or > 100 || (style.TileColor is { } tile && !IsColor(tile)))
        {
            return "tile opacity is 0-100 and the tile color #RRGGBB.";
        }

        if (!AccentEdges.Contains(style.AccentEdge ?? string.Empty) || !BarStyles.Contains(style.BarStyle ?? string.Empty))
        {
            return "accent edge is none, left, top or bottom; bar style is solid, rounded or segments.";
        }

        if ((style.Image is { } image && !IsImageId(image)) || !ImageFits.Contains(style.ImageFit ?? string.Empty) || !ImagePositions.Contains(style.ImagePosition ?? string.Empty))
        {
            return "image is an uploaded picture's id; image fit is cover, contain, stretch, tile or center; image position center, top, bottom, left or right.";
        }

        if (style.ImageOpacity is < 0 or > 100 || style.ImageBlur is < 0 or > 30 || style.ImageTintOpacity is < 0 or > 100 || (style.ImageTint is { } tint && !IsColor(tint)))
        {
            return "image opacity 0-100, blur 0-30, tint #RRGGBB and its opacity 0-100.";
        }

        if ((style.Pattern is { } pattern && !Patterns.Contains(pattern)) || style.PatternOpacity is < 0 or > 100 || style.PatternScale is < 50 or > 400 || (style.PatternColor is { } patternColor && !IsColor(patternColor)))
        {
            return "pattern is dots, grid, diagonal, carbon, hexagons or scanlines; its opacity 0-100, size 50-400 and color #RRGGBB.";
        }

        if (!ValueAligns.Contains(style.ValueAlign ?? string.Empty))
        {
            return "valueAlign is left, center or right.";
        }

        if (style.ValueVertical is { } vertical && !ValueVerticals.Contains(vertical))
        {
            return "valueVertical is top, middle or bottom.";
        }

        if (!Motions.Contains(style.Motion ?? string.Empty) || style.MotionSpeed is < 25 or > 300)
        {
            return "motion is none, aurora, grid, stars, pulse, scan or shimmer, at 25-300 %.";
        }

        if (!Backdrops.Contains(style.Backdrop ?? string.Empty))
        {
            return "backdrop is none, plate, hud, bezel, socket, chevron, glass, neon or blueprint.";
        }

        if (style.Art is { } art && !Arts.Contains(art))
        {
            return "art is one of the drawn arts (cockpit, racing, aviation, holo, synthwave, brass, terminal, lux), a generated scene (silk, mesh, aurora, nebula, caustics, bokeh, topo, prism, ocean, lava, ink, marble, foil, fireflies, warp, galaxy, clouds, snow, ribbons, circuit) or glass (or none).";
        }

        if (style.ArtSource is { } source && !IsImageId(source))
        {
            return "artSource is a picture of the library.";
        }

        return null;
    }

    private static string? Key(string? key) =>
        string.IsNullOrWhiteSpace(key) || key.Length > MaxKey || key.Any(char.IsControl) ? $"is 1-{MaxKey} printable characters." : null;

    private static string? Text(string? text) =>
        text is not null && (text.Length > MaxText || text.Any(char.IsControl)) ? $"is at most {MaxText} printable characters." : null;

    /// <summary>Whether a text is a #RRGGBB color.</summary>
    public static bool IsColor(string? text) => text is not null && ColorPattern().IsMatch(text);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PanelId();

    [GeneratedRegex(ImagePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ImageId();
}
