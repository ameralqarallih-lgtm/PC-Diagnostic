import type { OverlayBackdrop, OverlayCharacter, OverlayConfig, OverlayDevice, OverlayDisplay, OverlayImageFit, OverlayMotion, OverlayPanel, OverlayPanelPlace, OverlayPattern, OverlayReading, OverlayScreen, OverlayShow, OverlaySource, OverlayStyle, OverlayValueAlign, OverlayValueVertical, OverlayVisual } from '@/lib/api/types';
import { ICON_PATHS, autoIcon, type IconId } from './icons';

/** Limits the recorder checks (Core OverlayStyles). */
export const MAX_PANELS = 12;
export const MAX_READINGS = 16;
export const MAX_TEXT = 60;
export const INTERVALS = [250, 500, 1000, 2000] as const;
export const DEFAULT_HOTKEY = 'Ctrl+Alt+Shift+O';

/** Fonts every Windows 10/11 PC has (the recorder draws with them). */
export const FONTS = ['Segoe UI', 'Segoe UI Semibold', 'Bahnschrift', 'Consolas', 'Cascadia Mono', 'Arial', 'Verdana', 'Tahoma', 'Calibri', 'Courier New', 'Impact'] as const;

export const DEFAULT_STYLE: OverlayStyle = {
  font: 'Segoe UI',
  size: 14,
  bold: true,
  labelColor: '#9CA3AF',
  valueColor: '#FFFFFF',
  unitColor: '#9CA3AF',
  titleColor: '#38BDF8',
  warningColor: '#F59E0B',
  criticalColor: '#EF4444',
  background: '#0B1220',
  backgroundOpacity: 70,
  border: null,
  cornerRadius: 8,
  padding: 10,
  opacity: 100,
  outline: false,
  showLabels: true,
  showUnits: true,
  accent: '#38BDF8',
  background2: null,
  gradientAngle: 135,
  borderWidth: 1,
  shadow: 0,
  glow: 0,
  sheen: false,
  accentEdge: 'none',
  titleBar: false,
  caps: false,
  valueScale: 100,
  spacing: 100,
  icons: false,
  iconColor: null,
  barStyle: 'rounded',
  barHeight: 100,
  graphSeconds: 60,
  tileColor: null,
  tileOpacity: 7,
  okColor: '#34D399',
  image: null,
  imageFit: 'cover',
  imagePosition: 'center',
  imageOpacity: 100,
  imageBlur: 0,
  imageGray: false,
  imageTint: null,
  imageTintOpacity: 35,
  pattern: null,
  patternColor: null,
  patternOpacity: 14,
  patternScale: 100,
  valueAlign: 'center',
  motion: 'none',
  motionSpeed: 100,
  backdrop: 'none',
};

/** The moving backgrounds, as the recorder draws them. */
export const MOTIONS: { id: OverlayMotion; name: string; description: string }[] = [
  { id: 'none', name: 'Still', description: 'No motion.' },
  { id: 'aurora', name: 'Aurora', description: 'Soft lights in the accent and gradient colors drifting.' },
  { id: 'grid', name: 'Grid', description: 'Accent lines scrolling down.' },
  { id: 'stars', name: 'Stars', description: 'A drifting, twinkling star field.' },
  { id: 'pulse', name: 'Pulse', description: 'A breathing glow from the middle.' },
  { id: 'scan', name: 'Scan', description: 'A band of light sweeping down.' },
  { id: 'shimmer', name: 'Shimmer', description: 'A sheen sweeping across.' },
];

/** What can be drawn behind each tile, as the recorder draws it (fitted to the tile and its dial). */
export const BACKDROPS: { id: OverlayBackdrop; name: string; description: string }[] = [
  { id: 'none', name: 'None', description: "Only the tile's color." },
  { id: 'plate', name: 'Plate', description: 'A metal plate with a screw in each corner.' },
  { id: 'hud', name: 'HUD', description: 'Corner brackets in the accent and a line under the label.' },
  { id: 'bezel', name: 'Bezel', description: 'A ring around each dial, a double frame around the rest.' },
  { id: 'socket', name: 'Socket', description: 'A recess, dark at the top and lit at the bottom.' },
  { id: 'chevron', name: 'Chevron', description: 'Cut corners outlined in the accent, with a tab.' },
  { id: 'glass', name: 'Glass', description: 'Frosted, with a fine edge and a highlight along the top.' },
  { id: 'neon', name: 'Neon', description: 'A glowing outline in the accent (in the warning color past a limit).' },
  { id: 'blueprint', name: 'Blueprint', description: 'A fine grid in the accent, in a frame.' },
];

/** Where a value can sit in its space. */
/** Where a value sits up and down in its tile. */
export const VALUE_VERTICALS: { id: OverlayValueVertical; name: string }[] = [
  { id: 'top', name: 'Top' },
  { id: 'middle', name: 'Middle' },
  { id: 'bottom', name: 'Bottom' },
];

export const VALUE_ALIGNS: { id: OverlayValueAlign; name: string }[] = [
  { id: 'left', name: 'Left' },
  { id: 'center', name: 'Centre' },
  { id: 'right', name: 'Right' },
];

/** How a background picture can fill its panel. */
export const IMAGE_FITS: { id: OverlayImageFit; name: string }[] = [
  { id: 'cover', name: 'Fill' },
  { id: 'contain', name: 'Fit' },
  { id: 'stretch', name: 'Stretch' },
  { id: 'tile', name: 'Tile' },
  { id: 'center', name: 'Centre' },
];

/** The background patterns, as the recorder draws them. */
export const PATTERNS: { id: OverlayPattern; name: string }[] = [
  { id: 'dots', name: 'Dots' },
  { id: 'grid', name: 'Grid' },
  { id: 'diagonal', name: 'Diagonal' },
  { id: 'carbon', name: 'Carbon' },
  { id: 'hexagons', name: 'Hexagons' },
  { id: 'scanlines', name: 'Scanlines' },
];

/** A pattern's tile for its unit (12 px at 100 % size and scaling), as the recorder sizes it. */
export function patternTile(pattern: OverlayPattern, u: number): { width: number; height: number } {
  switch (pattern) {
    case 'diagonal':
      return { width: 0.7 * u, height: 0.7 * u };
    case 'carbon':
      return { width: 0.5 * u, height: 0.5 * u };
    case 'hexagons':
      return { width: u * 0.866, height: u * 1.5 };
    case 'scanlines':
      return { width: 1, height: Math.max(2, 0.25 * u) };
    default:
      return { width: u, height: u };
  }
}

/** A pattern's tile as an SVG (the same shapes the recorder draws), for a CSS background. */
export function patternSvg(pattern: OverlayPattern, u: number, color: string, opacity: number): string {
  const { width: w, height: h } = patternTile(pattern, u);
  const fill = `fill="${color}" fill-opacity="${opacity}"`;
  const stroke = `fill="none" stroke="${color}" stroke-opacity="${opacity}" stroke-width="${Math.max(1, 0.07 * u)}"`;
  const f = (n: number) => +n.toFixed(3);
  let body: string;
  switch (pattern) {
    case 'dots':
      body = `<circle cx="${f(u / 2)}" cy="${f(u / 2)}" r="${f(0.12 * u)}" ${fill}/>`;
      break;
    case 'grid': {
      const line = Math.max(1, 0.06 * u);
      body = `<rect x="0" y="0" width="${f(u)}" height="${f(line)}" ${fill}/><rect x="0" y="0" width="${f(line)}" height="${f(u)}" ${fill}/>`;
      break;
    }
    case 'diagonal':
      body = `<path d="M0 ${f(w)}L${f(w)} 0M${f(-w / 4)} ${f(w / 4)}L${f(w / 4)} ${f(-w / 4)}M${f((3 * w) / 4)} ${f((5 * w) / 4)}L${f((5 * w) / 4)} ${f((3 * w) / 4)}" ${stroke}/>`;
      break;
    case 'carbon':
      body = `<rect x="0" y="0" width="${f(w / 2)}" height="${f(w / 2)}" ${fill}/><rect x="${f(w / 2)}" y="${f(w / 2)}" width="${f(w / 2)}" height="${f(w / 2)}" ${fill}/>`;
      break;
    case 'hexagons': {
      const r = h / 3;
      body = `<path d="M${f(w / 2)} 0L${f(w)} ${f(r / 2)}L${f(w)} ${f(1.5 * r)}L${f(w / 2)} ${f(2 * r)}L0 ${f(1.5 * r)}L0 ${f(r / 2)}ZM${f(w / 2)} ${f(2 * r)}L${f(w / 2)} ${f(3 * r)}" ${stroke}/>`;
      break;
    }
    default:
      body = `<rect x="0" y="0" width="${f(w)}" height="${f(h / 2)}" ${fill}/>`;
  }
  return `url("data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" width="${f(w)}" height="${f(h)}">${body}</svg>`)}")`;
}

/** Where an uploaded background picture is served. */
export const imageUrl = (id: string) => `/api/v1/overlay/images/${encodeURIComponent(id)}`;

/** The ready-made looks in the order the gallery shows them (their styles come from the recorder's own list). */
export const PRESET_INFO: { id: string; name: string; description: string }[] = [
  { id: 'aurora', name: 'Aurora', description: 'Indigo to teal glass, icons, spaced capitals' },
  { id: 'glass', name: 'Glass', description: 'See-through slate with a sheen' },
  { id: 'carbon', name: 'Carbon', description: 'Graphite, an orange edge, segment bars' },
  { id: 'neon', name: 'Neon', description: 'Glowing cyan and pink' },
  { id: 'frost', name: 'Frost', description: 'Light and clean' },
  { id: 'terminal', name: 'Terminal', description: 'Green on black, monospaced' },
  { id: 'classic', name: 'Classic OSD', description: 'Outlined text, no background' },
  { id: 'minimal', name: 'Minimal', description: 'Plain outlined text' },
  { id: 'card', name: 'Card', description: 'The first look' },
  { id: 'obsidian', name: 'Obsidian', description: 'Smoked glass with a violet glow and dots' },
  { id: 'gold', name: 'Graphite Gold', description: 'Graphite with gold, a gold edge' },
  { id: 'nordic', name: 'Nordic', description: 'The calm Nord palette' },
  { id: 'ocean', name: 'Ocean', description: 'Abyss blue to lagoon, sea-glass tiles' },
  { id: 'rose', name: 'Midnight Rose', description: 'Plum night and rose light' },
  { id: 'forest', name: 'Forest', description: 'Pine to moss on a honeycomb' },
  { id: 'paper', name: 'Paper', description: 'Light and warm, ink-dark values' },
  { id: 'holo', name: 'Holo', description: 'See-through holographic cyan' },
  { id: 'synthwave', name: 'Synthwave', description: 'Neon pink grid, cyan values' },
  { id: 'aurora-flow', name: 'Aurora Flow', description: 'Moving: soft cyan and violet lights drifting' },
  { id: 'cyber-grid', name: 'Cyber Grid', description: 'Moving: a neon grid scrolling under glowing values' },
  { id: 'starfield', name: 'Starfield', description: 'Moving: stars drifting across deep navy' },
  { id: 'pulse-core', name: 'Pulse Core', description: 'Moving: a red core breathing behind the readings' },
  { id: 'scan-hud', name: 'Scan HUD', description: 'Moving: a cyan scan line over a see-through HUD' },
  { id: 'liquid-glass', name: 'Liquid Glass', description: 'Moving: a sheen sliding over frosted glass' },
];

/** The look new panels start with. */
export const NEW_PANEL_PRESET = 'aurora';

/** The presets offered: the recorder's, or the plain card when none came. */
export function presetsOf(presets: Record<string, OverlayStyle> | undefined): { id: string; name: string; description: string; style: OverlayStyle }[] {
  const known = PRESET_INFO.filter((p) => presets?.[p.id]).map((p) => ({ ...p, style: { ...DEFAULT_STYLE, ...presets![p.id]! } }));
  // Without the recorder's looks: the first look, Card (the default style).
  return known.length ? known : [{ ...PRESET_INFO.find((p) => p.id === 'card')!, style: DEFAULT_STYLE }];
}

/** The preset a style is, if it is one unchanged. */
export function presetOf(style: OverlayStyle, presets: { id: string; style: OverlayStyle }[]): string | null {
  const same = (a: OverlayStyle, b: OverlayStyle) => (Object.keys(DEFAULT_STYLE) as (keyof OverlayStyle)[]).every((k) => (a[k] ?? null) === (b[k] ?? null));
  return presets.find((p) => same({ ...DEFAULT_STYLE, ...style }, p.style))?.id ?? null;
}

export const LAYOUTS: { id: OverlayPanel['layout']; name: string }[] = [
  { id: 'rows', name: 'One per line' },
  { id: 'tiles', name: 'Tiles' },
  { id: 'line', name: 'All on one line' },
];

/** What can go with a value, as the dashboard widgets' designs, grouped as the picker shows them. */
export const VISUALS: { id: OverlayVisual; name: string; group: 'Value' | 'Dial' | 'History' | 'Bar' | 'Text'; description: string }[] = [
  { id: 'none', name: 'Value only', group: 'Value', description: 'Just the value and its unit.' },
  { id: 'ring', name: 'Ring', group: 'Dial', description: 'A ring filled to the value, against its full scale.' },
  { id: 'arc', name: 'Arc dial', group: 'Dial', description: 'An open dial (250°) filled to the value, like the dashboard’s dials.' },
  { id: 'speed', name: 'Speedometer', group: 'Dial', description: 'A scale with ticks and a needle pointing at the value.' },
  { id: 'status', name: 'Status light', group: 'Dial', description: 'A light: OK, warning or critical against your limits (set them below).' },
  { id: 'graph', name: 'Area graph', group: 'History', description: 'The last seconds as a filled graph (the length is set under Look).' },
  { id: 'line', name: 'Line graph', group: 'History', description: 'The last seconds as a line only.' },
  { id: 'columns', name: 'Columns', group: 'History', description: 'The last seconds as columns, each the highest of its slice.' },
  { id: 'bar', name: 'Bar', group: 'Bar', description: 'A meter bar under the value, against its full scale.' },
  { id: 'thermo', name: 'Thermometer', group: 'Bar', description: 'A thermometer under the value: good for temperatures.' },
  { id: 'stats', name: 'Low · avg · high', group: 'Text', description: 'The lowest, average and highest over the last seconds, after the value.' },
  { id: 'trend', name: 'Trend arrow', group: 'Text', description: 'How much it rose or fell over the last seconds, after the value.' },
];

export const DIALS: readonly OverlayVisual[] = ['ring', 'arc', 'speed', 'status'];
export const CHARTS: readonly OverlayVisual[] = ['graph', 'line', 'columns'];
export const UNDERS: readonly OverlayVisual[] = ['bar', 'thermo'];
export const TEXTS: readonly OverlayVisual[] = ['stats', 'trend'];

/** Visuals drawn from the reading's recent values (kept while it is shown). */
export const needsHistory = (visual: OverlayVisual) => CHARTS.includes(visual) || TEXTS.includes(visual);

/** Visuals that need a full scale (a status light needs only limits). */
export const needsScale = (visual: OverlayVisual) => (DIALS.includes(visual) && visual !== 'status') || UNDERS.includes(visual);

/** What goes with a reading's value (older layouts said only whether it had a bar). */
export const visualOf = (reading: OverlayReading): OverlayVisual => reading.visual ?? (reading.bar ? 'bar' : 'none');

/** The full scale of a reading's bar, ring or graph (its own, the source's total, 100 for percentages), or null. */
export const maxOf = (reading: OverlayReading, source: OverlaySource | undefined): number | null => reading.max ?? source?.max ?? (source?.unit === '%' ? 100 : null);

/**
 * The scale a ring, bar or dial uses when the reading has no full scale of its own (as the recorder decides): the next
 * round number (1, 2, 2.5, 5 × 10ⁿ) above 1.25 × the value now.
 */
export function autoMax(source: OverlaySource | undefined): number | null {
  if (!source || source.text != null || source.value == null || !Number.isFinite(source.value) || source.value <= 0) return null;
  // Sizes in bytes are rounded in their own units (2 GB → 2.5 GB, not 5 000 000 000 bytes, "4.7 GB").
  const unit = source.unit === 'B' || source.unit === 'B/s' ? 1024 ** Math.max(0, Math.floor(Math.log(source.value) / Math.log(1024))) : 1;
  const target = (source.value / unit) * 1.25;
  const magnitude = 10 ** Math.floor(Math.log10(target));
  for (const step of [1, 2, 2.5, 5, 10]) if (step * magnitude >= target) return step * magnitude * unit;
  return 10 * magnitude * unit;
}

export const SHOWS: { id: OverlayShow; name: string }[] = [
  { id: 'value', name: 'Value' },
  { id: 'percent', name: 'Percent' },
  { id: 'both', name: 'Both' },
  { id: 'of', name: 'Of total' },
];

/** The value with its total a percentage is of (RAM in use for RAM's, VRAM in use for VRAM's), as the recorder's ValueOf. */
export const valueSourceOf = (id: string): string | null => (id === 'memory.usage' ? 'memory.used' : id.endsWith('.vram_pct') ? id.slice(0, -'_pct'.length) : null);

/** How a reading reads now (a percentage reads as one). */
export const readsAs = (reading: OverlayReading, source: OverlaySource | undefined): OverlayShow => (source?.unit === '%' ? 'percent' : (reading.show ?? 'value'));

/**
 * A reading set to read as its value, a percentage, both or of its total, as the recorder's ReadAs: a percentage of a
 * value with its total (RAM, VRAM) reads as anything else from that value's source, its limits and full scale turned into
 * the value's units.
 */
export function readAs(reading: OverlayReading, source: OverlaySource | undefined, valueSource: OverlaySource | undefined, show: OverlayShow): OverlayReading {
  const total = valueSource?.max;
  if (source?.unit === '%' && show !== 'percent' && valueSource && total != null && total > 0 && valueSourceOf(source.id) === valueSource.id) {
    const of = (percent: number | null | undefined) => (percent == null ? null : Math.round((percent / 100) * total));
    return { ...reading, source: valueSource.id, show: show === 'value' ? null : show, warning: of(reading.warning), critical: of(reading.critical), max: of(reading.max) };
  }
  // A percentage already reads as one.
  return { ...reading, show: show === 'value' || (show === 'percent' && source?.unit === '%') ? null : show };
}

/** Whether a reading can read as a percentage or of its total (it has a total and is not a percentage already). */
export const hasTotal = (reading: OverlayReading, source: OverlaySource | undefined) =>
  source?.text == null && source?.unit !== '%' && (reading.max ?? source?.max ?? 0) > 0;

// As formatSource rounds (the recorder rounds halves away from zero to match).
const fixed = (v: number, places: number) => v.toFixed(Math.min(3, Math.max(0, places)));

/** A reading as shown: its value, a percentage of its total, both, or of its total (as the recorder's Shown). */
export function shown(source: OverlaySource | undefined, reading: OverlayReading): { value: string; unit: string } {
  const show = reading.show ?? 'value';
  const max = reading.max ?? source?.max ?? null;
  const v = source?.value;
  if (!source || show === 'value' || source.text != null || v == null || !Number.isFinite(v) || !(max != null && max > 0) || source.unit === '%') return formatSource(source, reading.decimals);
  const percent = fixed((100 * v) / max, reading.decimals ?? 0);
  if (show === 'percent') return { value: percent, unit: '%' };
  if (show === 'both') {
    const f = formatSource(source, reading.decimals);
    return { value: f.value, unit: `${f.unit ? `${f.unit} · ` : ''}${percent}%` };
  }
  const total = formatSource({ ...source, value: max }, null);
  const divisor = ({ TB: 2 ** 40, GB: 2 ** 30, 'GB/s': 2 ** 30, MB: 2 ** 20, 'MB/s': 2 ** 20, KB: 1024, 'KB/s': 1024 } as Record<string, number>)[total.unit] ?? 1;
  const places = reading.decimals ?? (total.value.includes('.') ? total.value.length - total.value.indexOf('.') - 1 : 0);
  return { value: `${fixed(v / divisor, places)}/${total.value}`, unit: total.unit };
}

/** What a unit's values usually reach, when the reading has no full scale (as the recorder's). */
function usual(unit: string, max: number | null | undefined): number {
  switch (unit) {
    case '%':
      return 100;
    case 'fps':
    case 'ms':
    case 'W':
      return 999;
    case '°C':
      return 100;
    case 'MHz':
    case 'RPM':
      return 9999;
    case 'GHz':
      return 9.99;
    case 'V':
      return 9.999;
    case 'A':
      return 99.99;
    case 'B':
      return max != null && max > 0 ? max : 999 * 2 ** 30;
    case 'B/s':
      return 99 * 2 ** 30;
    default:
      return 99;
  }
}

const MEASURED = new Set(['B', 'B/s', 'ms', 'GHz', 'V', 'A', '%', '°C', 'W', 'MHz', 'RPM', 'fps']);

/** A text with its digits as 0 (digits are as wide as a 0). */
export const zeros = (text: string) => text.replace(/[0-9]/g, '0');

/**
 * The widest texts a reading's number can take over its range, digits as 0, and the units it can take: the room the
 * value keeps, so a panel keeps its size whatever the values (as the recorder's Widest). A text: its room; `raw` for the
 * number itself, not as the reading shows it (its lowest, average and highest).
 */
export function widest(source: OverlaySource | undefined, reading: OverlayReading, raw = false): { values: string[]; units: string[] } {
  if (!source) return { values: [], units: [] };
  if (source.text != null) return { values: source.room ?? [], units: [] };
  const top = (raw ? null : maxOf(reading, source)) ?? usual(source.unit, source.max);
  // Just under each place a number gains a digit or its unit changes (where it rounds up), and the top. Plain numbers
  // are counts: whole ones, and halves only when it has a fraction now.
  const plain = !MEASURED.has(source.unit);
  const fraction = source.value != null && Math.abs(source.value - Math.round(source.value)) >= 1e-9;
  const probes = [top];
  for (const scale of [1, 2 ** 10, 2 ** 20, 2 ** 30, 2 ** 40]) {
    for (let b = 10; b <= 1e6; b *= 10) {
      const edge = b * scale;
      if (edge > top) continue;
      probes.push(plain ? edge - 1 : edge * (1 - 1e-9));
      if (plain && fraction) probes.push(edge - 0.5);
    }
  }
  const values: string[] = [];
  const units: string[] = [];
  for (const probe of probes) {
    const probed = { ...source, value: probe };
    const f = raw ? formatSource({ ...probed, max: null }, reading.decimals) : shown(probed, reading);
    const [value, unit] = [zeros(f.value), zeros(f.unit)];
    if (!values.includes(value)) values.push(value);
    if (unit && !units.includes(unit)) units.push(unit);
  }
  return { values, units };
}

/** The contrast labels and units keep against a panel's background (WCAG's for small text), as the recorder's. */
export const READABLE_CONTRAST = 4.5;

const rgbOf = (hex: string) => [1, 3, 5].map((i) => Number.parseInt(hex.slice(i, i + 2), 16)) as [number, number, number];
const relative = ([r, g, b]: readonly number[]) => {
  const channel = (v: number) => {
    const x = v / 255;
    return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r!) + 0.7152 * channel(g!) + 0.0722 * channel(b!);
};
const contrastOf = (a: readonly number[], b: readonly number[]) => {
  const [x, y] = [relative(a), relative(b)];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
};
const isHex = (text: string | null | undefined): text is string => !!text && /^#[0-9A-Fa-f]{6}$/.test(text);

/** A color moved toward another (or white or black) until it has the contrast against a background, as the recorder's. */
export function readableOn(color: string, background: string, toward: string, ratio = READABLE_CONTRAST): string {
  const [c, b] = [rgbOf(color), rgbOf(background)];
  if (contrastOf(c, b) >= ratio) return color;
  const target = isHex(toward) && contrastOf(rgbOf(toward), b) >= ratio ? rgbOf(toward) : relative(b) < 0.5 ? [255, 255, 255] : [0, 0, 0];
  const mix = (t: number) => c.map((v, i) => Math.round(v + (target[i]! - v) * t));
  let [low, high] = [0, 1];
  for (let i = 0; i < 20; i++) {
    const mid = (low + high) / 2;
    if (contrastOf(mix(mid), b) >= ratio) high = mid;
    else low = mid;
  }
  return `#${mix(high).map((v) => v.toString(16).padStart(2, '0').toUpperCase()).join('')}`;
}

/** A label's or unit's color made readable on the panel (kept on a panel with little background), as the recorder's Readable. */
export function readable(color: string, style: OverlayStyle): string {
  if (style.backgroundOpacity < 40 || !isHex(color) || !isHex(style.background)) return color;
  const first = readableOn(color, style.background, style.valueColor);
  return isHex(style.background2) ? readableOn(first, style.background2, style.valueColor) : first;
}

/** Where a tile goes: its first row and column and how many it spans. */
export interface Place {
  row: number;
  column: number;
  columns: number;
  rows: number;
}

/** Tiles in reading order, each in the first free cells where it fits (CSS grid's "row dense", as the recorder). */
export function pack(readings: readonly OverlayReading[], columns: number): Place[] {
  const taken: boolean[][] = [];
  const free = (row: number, column: number, w: number, h: number) => {
    for (let r = row; r < row + h; r++) for (let c = column; c < column + w; c++) if (taken[r]?.[c]) return false;
    return true;
  };
  return readings.map((reading) => {
    const w = Math.min(columns, Math.max(1, reading.colSpan ?? 1));
    const h = Math.min(4, Math.max(1, reading.rowSpan ?? 1));
    for (let row = 0; ; row++) {
      for (let column = 0; column + w <= columns; column++) {
        if (!free(row, column, w, h)) continue;
        for (let r = row; r < row + h; r++) {
          taken[r] ??= Array.from({ length: columns }, () => false);
          for (let c = column; c < column + w; c++) taken[r]![c] = true;
        }
        return { row, column, columns: w, rows: h };
      }
    }
  });
}

/** A tile spanning more cells shows its value larger (1, 1.6, 2.2…). */
export const heroScale = (place: Place) => 1 + 0.6 * (Math.min(place.columns, place.rows) - 1);

/** A reading's icon: its own, none, or the one for what it measures. */
export function iconOf(reading: OverlayReading, source: OverlaySource | undefined): IconId | null {
  if (reading.icon === 'none') return null;
  if (reading.icon && reading.icon in ICON_PATHS) return reading.icon as IconId;
  return autoIcon(reading.source, source?.unit);
}

/** Recent values of the readings shown as graphs: [time in ms, value] oldest first, by source. */
export type History = ReadonlyMap<string, readonly (readonly [number, number])[]>;

/** The most history kept (the longest graph). */
export const HISTORY_SECONDS = 300;

export const emptyConfig = (): OverlayConfig => ({ version: 1, enabled: false, hotkey: DEFAULT_HOTKEY, intervalMs: 1000, hideFromCapture: false, screens: [] });

export const screensOf = (config: OverlayConfig): OverlayScreen[] => config.screens ?? [];

/** A screen's panels in an orientation: its portrait ones while upright, when it has them (as the recorder's PanelsFor). */
export const panelsOf = (config: OverlayConfig, key: string, portrait = false): OverlayPanel[] => {
  const screen = screensOf(config).find((s) => s.key === key);
  return screen ? (portrait && screen.portrait ? screen.portrait : screen.panels) : [];
};

/** Whether a screen has panels of its own for when it is upright. */
export const hasPortrait = (config: OverlayConfig, key: string): boolean => !!screensOf(config).find((s) => s.key === key)?.portrait;

/** A screen's panels in both orientations (ids are unique across both). */
export const everyPanel = (screen: OverlayScreen): OverlayPanel[] => [...screen.panels, ...(screen.portrait ?? [])];

/** A display's shape now. */
export const isPortrait = (display: { width: number; height: number }) => display.height > display.width;

/** A USB screen's settings (its defaults when it has none), as the recorder's DeviceOf. */
export const deviceOf = (config: OverlayConfig, key: string): OverlayDevice =>
  config.devices?.find((d) => d.key === key) ?? { key, use: false, rotation: -1, zoom: 100, brightness: 100, background: '#000000' };

/** The layout with a USB screen's settings changed. */
export function withDevice(config: OverlayConfig, key: string, patch: Partial<Omit<OverlayDevice, 'key'>>): OverlayConfig {
  const next = { ...deviceOf(config, key), ...patch, key };
  return { ...config, devices: [...(config.devices ?? []).filter((d) => d.key !== key), next] };
}

/**
 * The layout with one monitor's panels replaced (its name remembered for when it is not connected): its portrait
 * panels when `portrait` and it has them, otherwise its panels.
 */
export function withPanels(config: OverlayConfig, key: string, name: string | null, panels: OverlayPanel[], portrait = false): OverlayConfig {
  const others = screensOf(config).filter((s) => s.key !== key);
  const current = screensOf(config).find((s) => s.key === key);
  const next: OverlayScreen =
    portrait && current?.portrait
      ? { ...current, name, portrait: panels }
      : { key, name, panels, ...(current?.portrait ? { portrait: current.portrait } : {}), ...(current?.characters?.length ? { characters: current.characters } : {}) };
  return { ...config, screens: next.panels.length || next.portrait?.length || next.characters?.length ? [...others, next] : others };
}

/** A screen's own characters (apart from its panels'). */
export const screenCharactersOf = (config: OverlayConfig, key: string): OverlayCharacter[] => screensOf(config).find((s) => s.key === key)?.characters ?? [];

/** The layout with a screen's own characters set. */
export function withScreenCharacters(config: OverlayConfig, key: string, name: string | null, characters: OverlayCharacter[]): OverlayConfig {
  const others = screensOf(config).filter((s) => s.key !== key);
  const current = screensOf(config).find((s) => s.key === key) ?? { key, name, panels: [] };
  const next: OverlayScreen = { ...current, name, characters: characters.length ? characters : null };
  if (!characters.length) delete next.characters;
  return { ...config, screens: next.panels.length || next.portrait || next.characters?.length ? [...others, next] : others };
}

/** The stand-in panel a screen's own characters play on: the whole screen, reacting to every reading its panels show. */
export function screenStage(panels: readonly OverlayPanel[], characters: OverlayCharacter[]): OverlayPanel {
  const seen = new Set<string>();
  const readings = panels.filter((p) => !p.hidden).flatMap((p) => p.readings).filter((r) => !seen.has(r.source) && seen.add(r.source));
  return { id: '~screen', title: null, x: 0, y: 0, layout: 'rows', readings, style: DEFAULT_STYLE, characters };
}

/** The layout with a monitor's portrait panels set (or removed: it uses its panels when upright too). */
export function withPortrait(config: OverlayConfig, key: string, name: string | null, portrait: OverlayPanel[] | null): OverlayConfig {
  const others = screensOf(config).filter((s) => s.key !== key);
  const current = screensOf(config).find((s) => s.key === key) ?? { key, name, panels: [] };
  const next: OverlayScreen = { ...current, name, portrait };
  if (!portrait) delete next.portrait;
  return { ...config, screens: next.panels.length || next.portrait || next.characters?.length ? [...others, next] : others };
}

/** A screen's size for its panels: its pixels at 100 % scaling. */
export interface ScreenBox {
  width: number;
  height: number;
}

/** The grids a panel can be put in, or every panel arranged into. */
export const GRIDS: { columns: number; rows: number; name: string }[] = [
  { columns: 2, rows: 1, name: 'Two side by side' },
  { columns: 1, rows: 2, name: 'Two, one above the other' },
  { columns: 2, rows: 2, name: 'Four' },
  { columns: 3, rows: 2, name: 'Six' },
  { columns: 3, rows: 3, name: 'Nine' },
];

const placeOf = (panel: OverlayPanel): OverlayPanelPlace => ({ x: panel.x, y: panel.y, width: panel.width ?? null, height: panel.height ?? null, size: panel.style.size });

/** Where it goes back to: the place before the first of maximize, grid and fit (kept until restored). */
const remembered = (panel: OverlayPanel): OverlayPanelPlace => panel.restore ?? placeOf(panel);

const shareIn = (at: number, room: number) => (room <= 0 ? 0 : Math.min(1, Math.max(0, at / room)));
const within = (v: number, min: number, max: number) => Math.min(max, Math.max(min, Math.round(v)));

/**
 * Its text size for a box: smaller when its readings at their own size (`content`, at 100 %, when known) would not
 * fit in it, never larger.
 */
function textFor(panel: OverlayPanel, box: ScreenBox, content?: ScreenBox): number {
  if (!content || content.width <= 0 || content.height <= 0) return panel.style.size;
  const pad = 2 * panel.style.padding;
  const k = Math.min(1, (box.width - pad) / Math.max(1, content.width - pad), (box.height - pad) / Math.max(1, content.height - pad));
  return k >= 1 ? panel.style.size : Math.max(6, Math.floor(panel.style.size * k * 2) / 2);
}

/** The panel filling the screen (its text smaller if its readings would not fit). */
export function maximized(panel: OverlayPanel, screen: ScreenBox, content?: ScreenBox): OverlayPanel {
  const width = within(screen.width, 40, 4000);
  const height = within(screen.height, 20, 3000);
  return { ...panel, x: 0, y: 0, width, height, style: { ...panel.style, size: textFor(panel, { width, height }, content) }, restore: remembered(panel) };
}

/** The panel in one cell of a grid on the screen, at the cell's size (its text smaller if its readings would not fit). */
export function inCell(panel: OverlayPanel, screen: ScreenBox, grid: { columns: number; rows: number }, column: number, row: number, content?: ScreenBox): OverlayPanel {
  const cellWidth = screen.width / grid.columns;
  const cellHeight = screen.height / grid.rows;
  const width = within(cellWidth, 40, 4000);
  const height = within(cellHeight, 20, 3000);
  return {
    ...panel,
    width,
    height,
    x: shareIn(column * cellWidth, screen.width - width),
    y: shareIn(row * cellHeight, screen.height - height),
    style: { ...panel.style, size: textFor(panel, { width, height }, content) },
    restore: remembered(panel),
  };
}

/**
 * The panel as large as fits the screen, keeping its proportions: its text (and fixed size) grow or shrink by how much
 * room there is; `drawn` is its size now and `fixed` the part of it that does not follow the text (padding, margins),
 * both at 100 % scaling. Its place, a share of the room it leaves, stays.
 */
export function fittedToScreen(panel: OverlayPanel, screen: ScreenBox, drawn: ScreenBox, fixed: number): OverlayPanel {
  const k = Math.min((screen.width - fixed) / Math.max(1, drawn.width - fixed), (screen.height - fixed) / Math.max(1, drawn.height - fixed));
  if (!Number.isFinite(k) || k <= 0) return panel;
  // The text in half points; a fixed box by the exact ratio (so it reaches the screen's edge).
  const size = Math.min(72, Math.max(6, Math.floor(panel.style.size * k * 2) / 2));
  const boxRatio = Math.min(k, screen.width / Math.max(1, panel.width ?? 1), screen.height / Math.max(1, panel.height ?? 1));
  return {
    ...panel,
    width: panel.width ? within(panel.width * boxRatio, 40, Math.min(4000, screen.width)) : panel.width,
    height: panel.height ? within(panel.height * boxRatio, 20, Math.min(3000, screen.height)) : panel.height,
    style: { ...panel.style, size },
    restore: remembered(panel),
  };
}

/** The panel shrunk (its text and fixed size together) when its fixed size is larger than the screen. */
export function fitWithin(panel: OverlayPanel, screen: ScreenBox): OverlayPanel {
  const k = Math.min(1, panel.width ? screen.width / panel.width : 1, panel.height ? screen.height / panel.height : 1);
  if (k >= 1) return panel;
  return {
    ...panel,
    width: panel.width ? within(panel.width * k, 40, 4000) : panel.width,
    height: panel.height ? within(panel.height * k, 20, 3000) : panel.height,
    style: { ...panel.style, size: Math.max(6, Math.floor(panel.style.size * k * 2) / 2) },
  };
}

/** The panel back where and how big it was before maximize, grid or fit. */
export function restored(panel: OverlayPanel): OverlayPanel {
  const place = panel.restore;
  if (!place) return panel;
  return { ...panel, x: place.x, y: place.y, width: place.width, height: place.height, style: { ...panel.style, size: place.size }, restore: null };
}

/** A grid that holds this many panels: as square as the screen allows (more columns on a wide screen). */
export function gridFor(count: number, screen: ScreenBox): { columns: number; rows: number } {
  if (count <= 1) return { columns: 1, rows: 1 };
  const wide = screen.width >= screen.height;
  let best = { columns: count, rows: 1 };
  let score = Number.POSITIVE_INFINITY;
  for (let columns = 1; columns <= count; columns++) {
    const rows = Math.ceil(count / columns);
    // Cells as close to the screen's own shape as possible, with few empty ones.
    const cellAspect = screen.width / columns / (screen.height / rows);
    const s = Math.abs(Math.log(cellAspect / (wide ? 1.6 : 0.8))) + (columns * rows - count) * 0.35;
    if (s < score) {
      score = s;
      best = { columns, rows };
    }
  }

  return best;
}

/** Every shown panel of a screen in the cells of a grid, in order (hidden ones, and any beyond the cells, stay as they are). */
export function arrangedInGrid(panels: OverlayPanel[], screen: ScreenBox, grid: { columns: number; rows: number }): OverlayPanel[] {
  let cell = 0;
  return panels.map((panel) => {
    if (panel.hidden || cell >= grid.columns * grid.rows) return panel;
    const at = cell++;
    return inCell(panel, screen, grid, at % grid.columns, Math.floor(at / grid.columns));
  });
}

export function newId(existing: readonly string[]): string {
  for (let i = 1; ; i++) {
    const id = `panel-${i}`;
    if (!existing.includes(id)) return id;
  }
}

/** A value as the recorder shows it: scaled bytes and rates, the unit's usual decimals. */
export function formatSource(source: OverlaySource | undefined, decimals?: number | null): { value: string; unit: string } {
  if (!source) return { value: '—', unit: '' };
  if (source.text != null) return { value: source.text, unit: '' };
  const v = source.value;
  if (v == null || !Number.isFinite(v)) return { value: '—', unit: source.unit === 'B' || source.unit === 'B/s' ? '' : source.unit };
  const n = (x: number, d: number) => x.toFixed(Math.min(3, Math.max(0, decimals ?? d)));
  // The next unit once a number would read 1000 (1000 MB reads 0.98 GB), so a value never takes four digits.
  const scaled = (...steps: [unit: string, scale: number, places: number][]) => {
    const [unit, scale, places] = steps.find(([, sc, d], i) => i === steps.length - 1 || Math.abs(Number(n(v / sc, d))) < 1000)!;
    return { value: n(v / scale, places), unit };
  };
  switch (source.unit) {
    case 'B':
      return scaled(['KB', 2 ** 10, 0], ['MB', 2 ** 20, 0], ['GB', 2 ** 30, 1], ['TB', 2 ** 40, 2]);
    case 'B/s':
      return scaled(['KB/s', 2 ** 10, 0], ['MB/s', 2 ** 20, 1], ['GB/s', 2 ** 30, 2]);
    case 'GHz':
      return { value: n(v, 2), unit: 'GHz' };
    case 'V':
      return { value: n(v, 3), unit: 'V' };
    case 'A':
      return { value: n(v, 2), unit: 'A' };
    case 'ms':
      // A tenth under 100 (as rounded: 99.96 reads 100, not 100.0).
      return { value: n(v, Math.abs(Number(v.toFixed(1))) < 100 ? 1 : 0), unit: source.unit };
    case '%':
    case '°C':
    case 'W':
    case 'MHz':
    case 'RPM':
    case 'fps':
      return { value: n(v, 0), unit: source.unit };
    default:
      return { value: n(v, Math.abs(v - Math.round(v)) < 1e-9 ? 0 : 1), unit: source.unit };
  }
}

/** 0 normal, 1 warning, 2 critical. */
export function level(reading: OverlayReading, value: number | null | undefined, total?: number | null): 0 | 1 | 2 {
  if (value == null) return 0;
  // A reading shown as a percentage of its total (VRAM in bytes) is judged by that percentage: its limits are one.
  const v = reading.show === 'percent' && total != null && total > 0 ? (value / total) * 100 : value;
  const beyond = (limit: number | null | undefined) => limit != null && (reading.below ? v <= limit : v >= limit);
  return beyond(reading.critical) ? 2 : beyond(reading.warning) ? 1 : 0;
}

/** Where a panel sits, in the monitor's pixels: its share of the room it leaves. */
/**
 * Where a drawn panel goes on a screen: its box (its size less the shadow's `margin` around it) at its share of the
 * room the box leaves, so a panel at an edge, filling the screen or in a grid cell lines up exactly, as the recorder
 * places it (the shadow going past the edge).
 */
export function placement(panel: Pick<OverlayPanel, 'x' | 'y'>, monitor: { width: number; height: number }, size: { width: number; height: number }, margin = 0) {
  const width = Math.max(0, size.width - 2 * margin);
  const height = Math.max(0, size.height - 2 * margin);
  return { left: panel.x * Math.max(0, monitor.width - width) - margin, top: panel.y * Math.max(0, monitor.height - height) - margin };
}

/** The share of the room for a pixel position (what a drag sets). */
export function shareOf(position: number, room: number): number {
  return room <= 0 ? 0 : Math.min(1, Math.max(0, position / room));
}

/** The nine snap points, as [x, y] shares. */
export const ANCHORS: { id: string; label: string; x: number; y: number }[] = [
  { id: 'tl', label: 'Top left', x: 0, y: 0 },
  { id: 't', label: 'Top', x: 0.5, y: 0 },
  { id: 'tr', label: 'Top right', x: 1, y: 0 },
  { id: 'l', label: 'Left', x: 0, y: 0.5 },
  { id: 'c', label: 'Middle', x: 0.5, y: 0.5 },
  { id: 'r', label: 'Right', x: 1, y: 0.5 },
  { id: 'bl', label: 'Bottom left', x: 0, y: 1 },
  { id: 'b', label: 'Bottom', x: 0.5, y: 1 },
  { id: 'br', label: 'Bottom right', x: 1, y: 1 },
];

/** Ready-made panels, filled with this PC's values (its first graphics card, drive, network adapter). */
export const TEMPLATES: { id: string; name: string; description: string }[] = [
  { id: 'essentials', name: 'Essentials', description: 'CPU and GPU load and temperatures, memory, with bars' },
  { id: 'hud', name: 'Gaming HUD', description: 'Tiles: a large frame-rate graph, rings for GPU and CPU, hot spot, VRAM' },
  { id: 'graphs', name: 'Live graphs', description: 'CPU, GPU, memory and network over the last minute' },
  { id: 'temperatures', name: 'Temperatures', description: 'Processor, graphics card and drives' },
  { id: 'gaming', name: 'Gaming', description: 'The game, FPS, frame time, 1% low, GPU, busiest core, ping' },
  { id: 'network', name: 'Network', description: 'Download and upload with graphs' },
  { id: 'clock', name: 'Clock', description: 'The time, large' },
  { id: 'empty', name: 'Empty', description: 'Add the readings yourself' },
];

export function templateReadings(template: string, sources: readonly OverlaySource[]): OverlayReading[] {
  const has = (id: string) => sources.some((s) => s.id === id);
  const first = (prefix: string, suffix: string) => sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix) && s.value != null)?.id ?? sources.find((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix))?.id;
  const gpu = (suffix: string) => first('gpu.', `.${suffix}`);
  const pick = (ids: (string | undefined)[]) => ids.filter((id): id is string => !!id && has(id));
  const hot = { warning: 85, critical: 95 };
  const bar = (source: string): OverlayReading => (source.endsWith('temp') ? { source, ...hot } : { source, visual: 'bar' });
  switch (template) {
    case 'essentials':
      return pick(['cpu.usage', 'cpu.temp', gpu('usage'), gpu('temp'), 'memory.usage']).map(bar);
    case 'hud':
      return pick(['game.fps', gpu('usage'), 'cpu.usage', gpu('hotspot'), gpu('vram_pct'), 'game.low1', 'memory.usage']).map((source): OverlayReading =>
        source === 'game.fps'
          ? { source, visual: 'graph', colSpan: 2, rowSpan: 2 }
          : source === 'game.low1'
            ? { source }
            : source.endsWith('hotspot')
              ? { source, warning: 95, critical: 105, visual: 'ring', max: 110 }
              : source.endsWith('vram_pct')
                ? { source, visual: 'bar' }
                : { source, visual: 'ring' },
      );
    case 'graphs':
      return pick(['cpu.usage', gpu('usage'), 'memory.usage', first('net.', '.down'), first('net.', '.up')]).map((source) => ({ source, visual: 'graph' }));
    case 'temperatures':
      return pick(['cpu.temp', gpu('temp'), gpu('hotspot'), ...sources.filter((s) => s.id.startsWith('disk.') && s.id.endsWith('.temp') && s.value != null).slice(0, 2).map((s) => s.id)]).map((source) => ({
        source,
        ...hot,
        visual: 'bar',
        max: 110,
      }));
    case 'gaming':
      return pick(['game.app', 'game.fps', 'game.frametime', 'game.low1', gpu('usage'), gpu('hotspot'), gpu('vram'), 'cpu.max_core', 'net.ping.router']).map((source): OverlayReading =>
        source === 'game.fps'
          ? { source, visual: 'graph' }
          : source.endsWith('hotspot')
            ? { source, warning: 95, critical: 105 }
            : source.endsWith('.vram')
              ? { source, show: 'of', visual: 'bar' }
              : source === 'cpu.max_core'
                ? { source, warning: 95, visual: 'bar' }
                : { source },
      );
    case 'network':
      return pick([first('net.', '.down'), first('net.', '.up')]).map((source) => ({ source, visual: 'graph' }));
    case 'clock':
      return pick(['time.clock']).map((source) => ({ source }));
    default:
      return [];
  }
}

/** A panel for a template, placed at the first free corner. */
export function newPanel(template: string, sources: readonly OverlaySource[], existing: readonly OverlayPanel[], style: OverlayStyle = DEFAULT_STYLE): OverlayPanel {
  const taken = new Set(existing.map((p) => `${p.x},${p.y}`));
  const corner = [ANCHORS[2]!, ANCHORS[0]!, ANCHORS[8]!, ANCHORS[6]!, ANCHORS[5]!, ANCHORS[3]!].find((a) => !taken.has(`${a.x},${a.y}`)) ?? ANCHORS[4]!;
  const name = TEMPLATES.find((t) => t.id === template)?.name;
  const readings = templateReadings(template, sources);
  return {
    id: newId(existing.map((p) => p.id)),
    title: template === 'clock' || template === 'empty' ? null : (name ?? null),
    x: corner.x,
    y: corner.y,
    layout: template === 'network' || template === 'clock' ? 'line' : template === 'hud' ? 'tiles' : 'rows',
    readings,
    style: template === 'clock' ? { ...style, showLabels: false, icons: false, valueScale: 200 } : { ...style },
    ...(template === 'hud' ? { columns: Math.min(3, Math.max(1, readings.length)) } : {}),
  };
}

/** The monitors drawn to scale in a box: each monitor's rectangle in the box's pixels. */
export function arrange(displays: readonly OverlayDisplay[], width: number, height: number, margin = 12) {
  if (displays.length === 0) return { scale: 1, rects: [] as { display: OverlayDisplay; left: number; top: number; width: number; height: number }[] };
  const minX = Math.min(...displays.map((d) => d.x));
  const minY = Math.min(...displays.map((d) => d.y));
  const maxX = Math.max(...displays.map((d) => d.x + d.width));
  const maxY = Math.max(...displays.map((d) => d.y + d.height));
  const scale = Math.min((width - 2 * margin) / Math.max(1, maxX - minX), (height - 2 * margin) / Math.max(1, maxY - minY));
  const offsetX = (width - (maxX - minX) * scale) / 2;
  const offsetY = (height - (maxY - minY) * scale) / 2;
  return {
    scale,
    rects: displays.map((d) => ({ display: d, left: offsetX + (d.x - minX) * scale, top: offsetY + (d.y - minY) * scale, width: d.width * scale, height: d.height * scale })),
  };
}

/** Hex color with an opacity (0-100) as CSS. */
export function rgba(hex: string, opacity = 100): string {
  const n = Number.parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${Math.max(0, Math.min(100, opacity)) / 100})`;
}
