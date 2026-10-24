import type { OverlayDrawnArt, OverlayPanel, OverlayReading, OverlayVisual } from '@/lib/api/types';
import { heroScale, pack, visualOf } from './model';
import { metrics } from './PanelPreview';

/**
 * Pictures drawn for a panel from where its readings are: a gauge face behind each dial (its ticks, numbers and red
 * zone along the dial's own sweep), a scope grid behind each graph, a channel and a scale under each bar, a window
 * behind each value, a socket around each light, frames, traces and small controls around the tiles, over a textured
 * background. The geometry is the editor's and the recorder's tile layout (at 100 %), so the picture lines up with the
 * readings; the editor draws it live in its previews, and saves it as the panel's picture for the recorder (again
 * whenever the panel's size or readings change).
 */

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** A tile and what is in it, in the panel's pixels at 100 %. */
export interface TileGeometry extends Rect {
  reading: OverlayReading;
  visual: OverlayVisual;
  /** The label line, when it has one. */
  header: Rect | null;
  /** The room under the label. */
  body: Rect;
  /** A dial's circle: its centre, diameter, track and the angles it runs over (clockwise from the right). */
  dial: { cx: number; cy: number; d: number; track: number; start: number; sweep: number } | null;
  chart: Rect | null;
  bar: Rect | null;
  value: Rect;
  light: { cx: number; cy: number; r: number } | null;
}

const DIAL_ANGLES: Partial<Record<OverlayVisual, { start: number; sweep: number }>> = {
  ring: { start: -90, sweep: 360 },
  arc: { start: 145, sweep: 250 },
  speed: { start: 160, sweep: 220 },
};

/**
 * Where each tile of a fixed-size tiles panel and what is in it sit, as the editor and the recorder lay them out at
 * 100 %; null for a panel that is not tiles or has no fixed size.
 */
export function panelGeometry(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>): { width: number; height: number; tiles: TileGeometry[]; pad: number; gap: number } | null {
  if (panel.layout !== 'tiles' || panel.width == null || panel.height == null || panel.readings.length === 0) return null;
  const s = panel.style;
  const m = metrics(s, 1);
  const columns = Math.max(1, Math.min(6, panel.columns ?? 2));
  const places = pack(panel.readings, columns);
  const cols = Math.min(columns, Math.max(...places.map((p) => p.column + p.columns)));
  const rows = Math.max(...places.map((p) => p.row + p.rows));
  const pad = m.pad;
  const top = pad + (panel.title ? m.titleLine + m.rowGap : 0);
  const gap = m.tileGap;
  const cw = (panel.width - 2 * pad - (cols - 1) * gap) / cols;
  const ch = (panel.height - top - pad - (rows - 1) * gap) / rows;
  const tiles = panel.readings.map((reading, i): TileGeometry => {
    const place = places[i]!;
    const x = pad + place.column * (cw + gap);
    const y = top + place.row * (ch + gap);
    const w = place.columns * cw + (place.columns - 1) * gap;
    const h = place.rows * ch + (place.rows - 1) * gap;
    const visual = visualOf(reading);
    const labelled = s.showLabels && reading.label !== null && reading.label !== '';
    const iconed = s.icons && reading.icon !== 'none';
    const headerH = labelled || iconed ? Math.max(m.labelLine, iconed ? m.tileIcon : 0) : 0;
    const ix = x + m.tilePad;
    const iw = w - 2 * m.tilePad;
    const by = y + m.tilePad + (headerH ? headerH + m.px * 0.35 : 0);
    const bh = Math.max(0, y + h - m.tilePad - by);
    const scale = heroScale(place);
    const valueH = m.valueLine * scale;
    const angles = DIAL_ANGLES[visual];
    let dial: TileGeometry['dial'] = null;
    if (angles) {
      const k = visual === 'ring' ? 1 : 0.92;
      const d = Math.max(4, Math.min(iw, bh / k));
      const thickness = Math.max(2, Math.min(d * 0.085, m.px * 0.9));
      const track = (thickness * (visual === 'speed' ? 0.6 : 1) * s.barHeight) / 100;
      dial = { cx: ix + iw / 2, cy: by + (bh - d * k) / 2 + d / 2, d, track, ...angles };
    }
    const charted = visual === 'graph' || visual === 'line' || visual === 'columns';
    const chartTop = by + valueH + m.px * 0.3;
    const chart = charted ? (place.rows > 1 ? { x: ix, y: chartTop, w: iw, h: Math.max(0, by + bh - chartTop) } : { x: ix, y: by + bh - m.tileGraphHeight, w: iw, h: m.tileGraphHeight }) : null;
    const barred = visual === 'bar' || visual === 'thermo';
    const barH = visual === 'thermo' ? m.barH * 1.6 : m.barH;
    const bar = barred ? { x: ix, y: by + bh - barH, w: iw, h: barH } : null;
    const centred = visual === 'none' || visual === 'status' || visual === 'stats' || visual === 'trend';
    const value = { x: ix, y: centred ? by + (bh - valueH) / 2 : by, w: iw, h: valueH };
    const lightR = (m.valueLine * 0.4 * scale) / 2;
    const light = visual === 'status' ? { cx: ix + iw - m.valueLine * 0.12 - lightR, cy: value.y + value.h / 2, r: lightR } : null;
    return { reading, visual, x, y, w, h, header: headerH ? { x: ix, y: y + m.tilePad, w: iw, h: headerH } : null, body: { x: ix, y: by, w: iw, h: bh }, dial, chart, bar, value, light };
  });
  return { width: panel.width, height: panel.height, tiles, pad, gap };
}

/** How each art looks: its colors, its background's texture, its frames, its gauge faces and its controls. */
interface Theme {
  name: string;
  bg: [string, string];
  texture: 'hex' | 'carbon' | 'brushed' | 'scan' | 'grain' | 'pinstripe' | 'stars' | 'none';
  textureColor: string;
  textureOpacity: number;
  frame: 'notch' | 'bevel' | 'instrument' | 'holo' | 'neon' | 'ascii' | 'gold' | 'brass';
  accent: string;
  accent2: string;
  ink: string;
  panel: string;
  panelOpacity: number;
  face: string;
  rim: string;
  glow: boolean;
  controls: 'leds' | 'switches' | 'screws' | 'rivets' | 'none';
  traces: boolean;
  horizon: boolean;
  numbers: boolean;
  font: string;
  redZone: boolean;
  guilloche: boolean;
}

export const ART_THEMES: Record<OverlayDrawnArt, Theme> = {
  cockpit: {
    name: 'Starship cockpit',
    bg: ['#020814', '#071A2E'],
    texture: 'hex',
    textureColor: '#22D3EE',
    textureOpacity: 7,
    frame: 'notch',
    accent: '#22D3EE',
    accent2: '#F59E0B',
    ink: '#7DD3FC',
    panel: '#03101C',
    panelOpacity: 88,
    face: '#020A14',
    rim: '#0E7490',
    glow: true,
    controls: 'leds',
    traces: true,
    horizon: false,
    numbers: true,
    font: 'Bahnschrift',
    redZone: false,
    guilloche: false,
  },
  racing: {
    name: 'Carbon racing dash',
    bg: ['#090909', '#1C1C1C'],
    texture: 'carbon',
    textureColor: '#FFFFFF',
    textureOpacity: 7,
    frame: 'bevel',
    accent: '#EF4444',
    accent2: '#F97316',
    ink: '#E5E5E5',
    panel: '#121212',
    panelOpacity: 92,
    face: '#070707',
    rim: '#737373',
    glow: false,
    controls: 'switches',
    traces: false,
    horizon: false,
    numbers: true,
    font: 'Bahnschrift',
    redZone: true,
    guilloche: false,
  },
  aviation: {
    name: 'Flight deck',
    bg: ['#191B1F', '#0D0E10'],
    texture: 'grain',
    textureColor: '#FFFFFF',
    textureOpacity: 6,
    frame: 'instrument',
    accent: '#FBBF24',
    accent2: '#F8FAFC',
    ink: '#F5F5F5',
    panel: '#0A0B0D',
    panelOpacity: 96,
    face: '#040405',
    rim: '#52525B',
    glow: false,
    controls: 'screws',
    traces: false,
    horizon: false,
    numbers: true,
    font: 'Segoe UI Semibold',
    redZone: true,
    guilloche: false,
  },
  holo: {
    name: 'Hologram',
    bg: ['#01161C', '#05303A'],
    texture: 'hex',
    textureColor: '#2DD4BF',
    textureOpacity: 10,
    frame: 'holo',
    accent: '#2DD4BF',
    accent2: '#E879F9',
    ink: '#99F6E4',
    panel: '#021F26',
    panelOpacity: 62,
    face: '#021A20',
    rim: '#2DD4BF',
    glow: true,
    controls: 'leds',
    traces: true,
    horizon: false,
    numbers: false,
    font: 'Bahnschrift',
    redZone: false,
    guilloche: false,
  },
  synthwave: {
    name: 'Synthwave sunset',
    bg: ['#12002B', '#3B0A45'],
    texture: 'stars',
    textureColor: '#FFFFFF',
    textureOpacity: 55,
    frame: 'neon',
    accent: '#FF2E97',
    accent2: '#22D3EE',
    ink: '#FBCFE8',
    panel: '#14022C',
    panelOpacity: 78,
    face: '#0D0221',
    rim: '#FF2E97',
    glow: true,
    controls: 'none',
    traces: false,
    horizon: true,
    numbers: false,
    font: 'Bahnschrift',
    redZone: false,
    guilloche: false,
  },
  brass: {
    name: 'Brass chronometer',
    bg: ['#241810', '#0B0705'],
    texture: 'grain',
    textureColor: '#F5D58A',
    textureOpacity: 7,
    frame: 'brass',
    accent: '#D4A24C',
    accent2: '#F5D58A',
    ink: '#F3E3BF',
    panel: '#140D08',
    panelOpacity: 92,
    face: '#0F0A06',
    rim: '#B8862F',
    glow: false,
    controls: 'rivets',
    traces: false,
    horizon: false,
    numbers: true,
    font: 'Segoe UI Semibold',
    redZone: true,
    guilloche: true,
  },
  terminal: {
    name: 'CRT terminal',
    bg: ['#010A03', '#021407'],
    texture: 'scan',
    textureColor: '#22C55E',
    textureOpacity: 12,
    frame: 'ascii',
    accent: '#22C55E',
    accent2: '#86EFAC',
    ink: '#4ADE80',
    panel: '#021006',
    panelOpacity: 82,
    face: '#010A03',
    rim: '#16A34A',
    glow: true,
    controls: 'leds',
    traces: true,
    horizon: false,
    numbers: true,
    font: 'Consolas',
    redZone: false,
    guilloche: false,
  },
  lux: {
    name: 'Midnight gold',
    bg: ['#08080B', '#141419'],
    texture: 'pinstripe',
    textureColor: '#C9A227',
    textureOpacity: 6,
    frame: 'gold',
    accent: '#C9A227',
    accent2: '#F1D27A',
    ink: '#E7D9A8',
    panel: '#0A0A0E',
    panelOpacity: 92,
    face: '#08080B',
    rim: '#C9A227',
    glow: false,
    controls: 'none',
    traces: false,
    horizon: false,
    numbers: true,
    font: 'Segoe UI',
    redZone: false,
    guilloche: true,
  },
};

const f = (n: number) => (Math.round(n * 100) / 100).toString();
const hex = (color: string, alpha: number) => `${color}" stroke-opacity="${alpha}`;
const point = (cx: number, cy: number, r: number, deg: number) => [cx + r * Math.cos((deg * Math.PI) / 180), cy + r * Math.sin((deg * Math.PI) / 180)] as const;

function arc(cx: number, cy: number, r: number, start: number, sweep: number) {
  if (sweep >= 359.9) return `M${f(cx - r)} ${f(cy)} a${f(r)} ${f(r)} 0 1 0 ${f(2 * r)} 0 a${f(r)} ${f(r)} 0 1 0 ${f(-2 * r)} 0`;
  const [x1, y1] = point(cx, cy, r, start);
  const [x2, y2] = point(cx, cy, r, start + sweep);
  return `M${f(x1)} ${f(y1)} A${f(r)} ${f(r)} 0 ${sweep > 180 ? 1 : 0} 1 ${f(x2)} ${f(y2)}`;
}

/** Corners cut by c: the cockpit's and the hologram's frames. */
function notched(r: Rect, c: number) {
  const { x, y, w, h } = r;
  return `M${f(x + c)} ${f(y)} H${f(x + w - c)} L${f(x + w)} ${f(y + c)} V${f(y + h - c)} L${f(x + w - c)} ${f(y + h)} H${f(x + c)} L${f(x)} ${f(y + h - c)} V${f(y + c)} Z`;
}

/** The scale a gauge shows (its labels), from the reading's own full scale. */
function scaleOf(reading: OverlayReading): number | null {
  if (reading.max != null && reading.max > 0) return reading.max;
  return /usage|_pct|percent/.test(reading.source) ? 100 : null;
}

function label(n: number) {
  return n >= 1000 ? `${Math.round(n / 100) / 10}k` : Number.isInteger(n) ? `${n}` : n.toFixed(1);
}

/** The art for a panel, as an SVG document at its size; null when it cannot be laid out (not tiles, no fixed size). */
export function artSvg(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>, kind: OverlayDrawnArt): string | null {
  const g = panelGeometry(panel);
  const t = ART_THEMES[kind];
  if (!g || !t) return null;
  const { width: W, height: H } = g;
  const s = panel.style;
  const unit = Math.max(1, Math.min(W, H) / 400);
  const out: string[] = [];
  const glow = t.glow ? ' filter="url(#glow)"' : '';

  // ---- Definitions: gradients, glow, textures ----
  out.push('<defs>');
  out.push(`<linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.bg[0]}"/><stop offset="1" stop-color="${t.bg[1]}"/></linearGradient>`);
  out.push(`<radialGradient id="face" cx="0.5" cy="0.42" r="0.6"><stop offset="0" stop-color="${t.face}" stop-opacity="0.55"/><stop offset="0.75" stop-color="${t.face}" stop-opacity="0.92"/><stop offset="1" stop-color="#000" stop-opacity="1"/></radialGradient>`);
  out.push(`<linearGradient id="metal" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${t.accent2}"/><stop offset="0.45" stop-color="${t.rim}"/><stop offset="0.55" stop-color="${t.accent2}"/><stop offset="1" stop-color="${t.rim}"/></linearGradient>`);
  out.push(`<linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.panel}" stop-opacity="${t.panelOpacity / 100}"/><stop offset="1" stop-color="#000" stop-opacity="${Math.min(1, t.panelOpacity / 100 + 0.05)}"/></linearGradient>`);
  out.push('<filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="2.2" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>');
  const tc = t.textureColor;
  const to = t.textureOpacity / 100;
  switch (t.texture) {
    case 'hex': {
      const a = 14 * unit;
      const hh = a * Math.sqrt(3);
      out.push(`<pattern id="tex" width="${f(a * 3)}" height="${f(hh)}" patternUnits="userSpaceOnUse"><path d="M${f(a * 0.5)} 0 L${f(a * 1.5)} 0 L${f(a * 2)} ${f(hh / 2)} L${f(a * 1.5)} ${f(hh)} L${f(a * 0.5)} ${f(hh)} L0 ${f(hh / 2)} Z M${f(a * 2)} ${f(hh / 2)} H${f(a * 3)}" fill="none" stroke="${tc}" stroke-opacity="${to}" stroke-width="1"/></pattern>`);
      break;
    }
    case 'carbon': {
      const c = 6 * unit;
      out.push(`<pattern id="tex" width="${f(c * 2)}" height="${f(c * 2)}" patternUnits="userSpaceOnUse"><rect width="${f(c)}" height="${f(c)}" fill="${tc}" fill-opacity="${to}"/><rect x="${f(c)}" y="${f(c)}" width="${f(c)}" height="${f(c)}" fill="${tc}" fill-opacity="${to}"/><path d="M0 ${f(c)} L${f(c)} 0 M${f(c)} ${f(c * 2)} L${f(c * 2)} ${f(c)}" stroke="#000" stroke-opacity="0.35" stroke-width="${f(c * 0.3)}"/></pattern>`);
      break;
    }
    case 'scan':
      out.push(`<pattern id="tex" width="4" height="${f(3 * unit)}" patternUnits="userSpaceOnUse"><rect width="4" height="${f(unit)}" fill="${tc}" fill-opacity="${to}"/></pattern>`);
      break;
    case 'pinstripe': {
      const p = 16 * unit;
      out.push(`<pattern id="tex" width="${f(p)}" height="${f(p)}" patternUnits="userSpaceOnUse" patternTransform="rotate(35)"><rect width="1" height="${f(p)}" fill="${tc}" fill-opacity="${to * 2}"/></pattern>`);
      break;
    }
    case 'brushed':
      out.push(`<pattern id="tex" width="200" height="6" patternUnits="userSpaceOnUse"><rect y="1" width="200" height="1" fill="${tc}" fill-opacity="${to}"/><rect y="4" width="200" height="1" fill="${tc}" fill-opacity="${to * 0.6}"/></pattern>`);
      break;
    case 'grain':
      out.push(`<filter id="grain"><feTurbulence type="fractalNoise" baseFrequency="0.85" numOctaves="2" seed="7"/><feColorMatrix values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  0 0 0 ${f(to * 4)} -${f(to * 1.4)}"/></filter>`);
      break;
    default:
      break;
  }
  out.push('</defs>');

  // ---- Background ----
  out.push(`<rect width="${f(W)}" height="${f(H)}" fill="url(#bg)"/>`);
  if (t.horizon) out.push(...horizon(W, H, t, unit));
  if (t.texture === 'stars') out.push(...stars(W, H * (t.horizon ? 0.62 : 1), t, unit));
  else if (t.texture === 'grain') out.push(`<rect width="${f(W)}" height="${f(H)}" filter="url(#grain)" opacity="1"/>`);
  else if (t.texture !== 'none') out.push(`<rect width="${f(W)}" height="${f(H)}" fill="url(#tex)"/>`);
  // A vignette, and the panel's own bezel along its edge.
  out.push(`<rect width="${f(W)}" height="${f(H)}" fill="none" stroke="#000" stroke-opacity="0.55" stroke-width="${f(g.pad * 1.2)}"/>`);
  if (t.frame === 'notch' || t.frame === 'holo') out.push(`<path d="${notched({ x: g.pad * 0.35, y: g.pad * 0.35, w: W - g.pad * 0.7, h: H - g.pad * 0.7 }, Math.min(28 * unit, W / 10, H / 10))}" fill="none" stroke="${hex(t.accent, 0.35)}" stroke-width="${f(1.2 * unit)}"${glow}/>`);
  if (t.controls === 'screws' || t.controls === 'rivets') {
    const inset = g.pad * 0.5;
    for (const [x, y] of [
      [inset, inset],
      [W - inset, inset],
      [W - inset, H - inset],
      [inset, H - inset],
    ] as const)
      out.push(...screw(x, y, Math.min(g.pad * 0.3, 5 * unit), t));
  }

  // ---- Traces between tiles ----
  if (t.traces && g.gap >= 5) out.push(...traces(g, t, unit, glow));

  // ---- Tiles ----
  g.tiles.forEach((tile, i) => out.push(...tileArt(tile, t, s.tileOpacity, unit, glow, `t${i}`)));

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${f(W)}" height="${f(H)}" viewBox="0 0 ${f(W)} ${f(H)}">${out.join('')}</svg>`;
}

function horizon(W: number, H: number, t: Theme, unit: number): string[] {
  const out: string[] = [];
  const top = H * 0.62;
  // The sun, banded, setting behind the horizon.
  const r = Math.min(W, H) * 0.32;
  out.push(`<defs><linearGradient id="sun" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFE29A"/><stop offset="0.55" stop-color="#FF6A88"/><stop offset="1" stop-color="#FF2E97"/></linearGradient><clipPath id="sky"><rect width="${f(W)}" height="${f(top)}"/></clipPath></defs>`);
  out.push(`<g clip-path="url(#sky)" opacity="0.85"><circle cx="${f(W / 2)}" cy="${f(top)}" r="${f(r)}" fill="url(#sun)"/>`);
  for (let i = 1; i < 6; i++) out.push(`<rect x="${f(W / 2 - r)}" y="${f(top - r * 0.12 * i * 0.9)}" width="${f(2 * r)}" height="${f(r * 0.025 * i)}" fill="#12002B"/>`);
  out.push('</g>');
  // The floor: a grid running to the horizon.
  out.push(`<rect y="${f(top)}" width="${f(W)}" height="${f(H - top)}" fill="#0B0019"/>`);
  out.push(`<line x1="0" y1="${f(top)}" x2="${f(W)}" y2="${f(top)}" stroke="${t.accent}" stroke-width="${f(2 * unit)}" filter="url(#glow)"/>`);
  for (let i = 1; i <= 12; i++) {
    const y = top + (H - top) * Math.pow(i / 12, 1.8);
    out.push(`<line x1="0" y1="${f(y)}" x2="${f(W)}" y2="${f(y)}" stroke="${t.accent}" stroke-opacity="0.55" stroke-width="${f(unit)}"/>`);
  }
  for (let i = -16; i <= 16; i++) {
    const x = W / 2 + (i * W) / 16;
    out.push(`<line x1="${f(W / 2 + (i * W) / 60)}" y1="${f(top)}" x2="${f(x * 1.0)}" y2="${f(H)}" stroke="${t.accent2}" stroke-opacity="0.4" stroke-width="${f(unit)}"/>`);
  }
  return out;
}

function stars(W: number, H: number, t: Theme, unit: number): string[] {
  const out: string[] = [];
  let seed = 11;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  const count = Math.round((W * H) / (2600 * unit * unit));
  for (let i = 0; i < count; i++) out.push(`<circle cx="${f(rnd() * W)}" cy="${f(rnd() * H)}" r="${f((0.4 + rnd() * 1.1) * unit)}" fill="${t.textureColor}" fill-opacity="${f((t.textureOpacity / 100) * (0.3 + rnd() * 0.7))}"/>`);
  return out;
}

function screw(x: number, y: number, r: number, t: Theme): string[] {
  if (t.controls === 'rivets') return [`<circle cx="${f(x)}" cy="${f(y)}" r="${f(r)}" fill="url(#metal)" stroke="#000" stroke-opacity="0.5" stroke-width="0.8"/>`];
  return [
    `<circle cx="${f(x)}" cy="${f(y)}" r="${f(r)}" fill="#3F3F46" stroke="#000" stroke-opacity="0.7" stroke-width="0.8"/>`,
    `<line x1="${f(x - r * 0.6)}" y1="${f(y + r * 0.6)}" x2="${f(x + r * 0.6)}" y2="${f(y - r * 0.6)}" stroke="#09090B" stroke-width="${f(Math.max(0.8, r * 0.35))}"/>`,
  ];
}

/** Lines along the gaps between tiles, with stubs into each tile and nodes where they meet. */
function traces(g: NonNullable<ReturnType<typeof panelGeometry>>, t: Theme, unit: number, glow: string): string[] {
  const out: string[] = [];
  const done = new Set<string>();
  for (const a of g.tiles) {
    for (const b of g.tiles) {
      if (a === b) continue;
      // A neighbour to the right: a line across the gap at their shared height, a node in the middle.
      if (Math.abs(b.x - (a.x + a.w + g.gap)) < 1 && b.y < a.y + a.h && b.y + b.h > a.y) {
        const key = `${a.x},${a.y}>${b.x},${b.y}`;
        if (done.has(key)) continue;
        done.add(key);
        const y = (Math.max(a.y, b.y) + Math.min(a.y + a.h, b.y + b.h)) / 2;
        const x = a.x + a.w + g.gap / 2;
        out.push(`<line x1="${f(a.x + a.w - 1)}" y1="${f(y)}" x2="${f(b.x + 1)}" y2="${f(y)}" stroke="${hex(t.accent, 0.6)}" stroke-width="${f(1.2 * unit)}"${glow}/>`);
        out.push(`<circle cx="${f(x)}" cy="${f(y)}" r="${f(Math.min(g.gap * 0.3, 2.6 * unit))}" fill="${t.accent2}" fill-opacity="0.9"${glow}/>`);
      }
      // A neighbour below: a short line down the gap.
      if (Math.abs(b.y - (a.y + a.h + g.gap)) < 1 && b.x < a.x + a.w && b.x + b.w > a.x) {
        const x = Math.max(a.x, b.x) + Math.min(a.w, b.w) * 0.18;
        out.push(`<line x1="${f(x)}" y1="${f(a.y + a.h - 1)}" x2="${f(x)}" y2="${f(b.y + 1)}" stroke="${hex(t.accent, 0.45)}" stroke-width="${f(unit)}"/>`);
      }
    }
  }
  return out;
}

function tileArt(tile: TileGeometry, t: Theme, tileOpacity: number, unit: number, glow: string, clip: string): string[] {
  const out: string[] = [`<clipPath id="${clip}"><rect x="${f(tile.x)}" y="${f(tile.y)}" width="${f(tile.w)}" height="${f(tile.h)}"/></clipPath>`];
  const r = { x: tile.x, y: tile.y, w: tile.w, h: tile.h };
  const corner = Math.min(14 * unit, tile.w / 6, tile.h / 6);
  // ---- The tile's frame ----
  switch (t.frame) {
    case 'notch':
      out.push(`<path d="${notched(r, corner)}" fill="url(#tile)" stroke="${hex(t.accent, 0.75)}" stroke-width="${f(1.4 * unit)}"${glow}/>`);
      out.push(`<path d="${notched({ x: r.x + 4 * unit, y: r.y + 4 * unit, w: r.w - 8 * unit, h: r.h - 8 * unit }, corner * 0.7)}" fill="none" stroke="${hex(t.accent, 0.18)}" stroke-width="${f(unit)}"/>`);
      out.push(`<rect x="${f(r.x + corner)}" y="${f(r.y - 1.5 * unit)}" width="${f(Math.min(r.w * 0.3, 60 * unit))}" height="${f(3 * unit)}" fill="${t.accent}"${glow}/>`);
      break;
    case 'holo': {
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" fill="url(#tile)"/>`);
      out.push(`<rect x="${f(r.x + 0.5)}" y="${f(r.y + 0.5)}" width="${f(r.w - 1)}" height="${f(r.h - 1)}" fill="none" stroke="${hex(t.accent, 0.35)}" stroke-width="${f(unit)}" stroke-dasharray="${f(6 * unit)} ${f(4 * unit)}"/>`);
      const arm = Math.min(18 * unit, r.w / 4, r.h / 4);
      for (const [x, y, dx, dy] of [
        [r.x, r.y, 1, 1],
        [r.x + r.w, r.y, -1, 1],
        [r.x + r.w, r.y + r.h, -1, -1],
        [r.x, r.y + r.h, 1, -1],
      ] as const)
        out.push(`<path d="M${f(x)} ${f(y + dy * arm)} V${f(y)} H${f(x + dx * arm)}" fill="none" stroke="${t.accent2}" stroke-width="${f(2 * unit)}"${glow}/>`);
      break;
    }
    case 'bevel':
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" rx="${f(8 * unit)}" fill="url(#tile)" stroke="#000" stroke-opacity="0.8" stroke-width="${f(1.5 * unit)}"/>`);
      out.push(`<rect x="${f(r.x + 1.5 * unit)}" y="${f(r.y + 1.5 * unit)}" width="${f(r.w - 3 * unit)}" height="${f(r.h - 3 * unit)}" rx="${f(6.5 * unit)}" fill="none" stroke="#FFF" stroke-opacity="0.09" stroke-width="${f(unit)}"/>`);
      out.push(`<line x1="${f(r.x + 10 * unit)}" y1="${f(r.y + r.h - 2 * unit)}" x2="${f(r.x + r.w - 10 * unit)}" y2="${f(r.y + r.h - 2 * unit)}" stroke="${t.accent}" stroke-opacity="0.65" stroke-width="${f(2 * unit)}"/>`);
      break;
    case 'instrument':
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" rx="${f(5 * unit)}" fill="url(#tile)" stroke="#000" stroke-width="${f(2 * unit)}"/>`);
      out.push(`<rect x="${f(r.x + 2 * unit)}" y="${f(r.y + 2 * unit)}" width="${f(r.w - 4 * unit)}" height="${f(r.h - 4 * unit)}" rx="${f(4 * unit)}" fill="none" stroke="#FFF" stroke-opacity="0.07" stroke-width="${f(unit)}"/>`);
      break;
    case 'neon':
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" rx="${f(10 * unit)}" fill="url(#tile)"/>`);
      for (const [w, a] of [
        [6, 0.12],
        [3.5, 0.25],
        [1.5, 0.95],
      ] as const)
        out.push(`<rect x="${f(r.x + 2 * unit)}" y="${f(r.y + 2 * unit)}" width="${f(r.w - 4 * unit)}" height="${f(r.h - 4 * unit)}" rx="${f(9 * unit)}" fill="none" stroke="${hex(t.accent, a)}" stroke-width="${f(w * unit)}"/>`);
      break;
    case 'ascii': {
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" fill="url(#tile)" stroke="${hex(t.accent, 0.6)}" stroke-width="${f(unit)}"/>`);
      out.push(`<rect x="${f(r.x + 3 * unit)}" y="${f(r.y + 3 * unit)}" width="${f(r.w - 6 * unit)}" height="${f(r.h - 6 * unit)}" fill="none" stroke="${hex(t.accent, 0.25)}" stroke-width="${f(unit)}"/>`);
      const c = 5 * unit;
      for (const [x, y] of [
        [r.x, r.y],
        [r.x + r.w, r.y],
        [r.x + r.w, r.y + r.h],
        [r.x, r.y + r.h],
      ] as const)
        out.push(`<path d="M${f(x - c)} ${f(y)} H${f(x + c)} M${f(x)} ${f(y - c)} V${f(y + c)}" stroke="${t.accent2}" stroke-width="${f(1.4 * unit)}"${glow}/>`);
      break;
    }
    case 'gold':
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" rx="${f(3 * unit)}" fill="url(#tile)" stroke="url(#metal)" stroke-width="${f(1.3 * unit)}"/>`);
      out.push(`<rect x="${f(r.x + 4 * unit)}" y="${f(r.y + 4 * unit)}" width="${f(r.w - 8 * unit)}" height="${f(r.h - 8 * unit)}" rx="${f(2 * unit)}" fill="none" stroke="${hex(t.accent, 0.3)}" stroke-width="${f(0.8 * unit)}"/>`);
      out.push(`<path d="M${f(r.x + r.w / 2 - 5 * unit)} ${f(r.y)} l${f(5 * unit)} ${f(-4 * unit)} l${f(5 * unit)} ${f(4 * unit)} l${f(-5 * unit)} ${f(4 * unit)} Z" fill="url(#metal)"/>`);
      break;
    case 'brass':
      out.push(`<rect x="${f(r.x)}" y="${f(r.y)}" width="${f(r.w)}" height="${f(r.h)}" rx="${f(10 * unit)}" fill="url(#tile)" stroke="url(#metal)" stroke-width="${f(3 * unit)}"/>`);
      for (let x = r.x + 18 * unit; x < r.x + r.w - 12 * unit; x += Math.max(36 * unit, (r.w - 36 * unit) / Math.max(1, Math.floor(r.w / (60 * unit)))))
        for (const y of [r.y + 5 * unit, r.y + r.h - 5 * unit]) out.push(`<circle cx="${f(x)}" cy="${f(y)}" r="${f(1.6 * unit)}" fill="url(#metal)"/>`);
      break;
  }

  // ---- The label's plate ----
  if (tile.header && (t.frame === 'notch' || t.frame === 'holo' || t.frame === 'ascii')) {
    const h = tile.header;
    out.push(`<line x1="${f(h.x)}" y1="${f(h.y + h.h + 2 * unit)}" x2="${f(h.x + h.w * 0.55)}" y2="${f(h.y + h.h + 2 * unit)}" stroke="${hex(t.accent, 0.45)}" stroke-width="${f(unit)}"/>`);
  }

  // ---- What is behind the reading ----
  const scaleMax = scaleOf(tile.reading);
  // An open dial's face is a whole circle: what falls past the tile is cut off.
  if (tile.dial) out.push(`<g clip-path="url(#${clip})">`, ...gauge(tile, t, scaleMax, unit, glow, tileOpacity), '</g>');
  if (tile.chart) out.push(...scope(tile.chart, t, unit));
  if (tile.bar) out.push(...channel(tile.bar, t, scaleMax, tile.reading, unit));
  if (!tile.dial && !tile.chart && !tile.bar) {
    const v = tile.value;
    const pad = 4 * unit;
    out.push(`<rect x="${f(v.x - pad)}" y="${f(v.y - pad * 0.6)}" width="${f(v.w + 2 * pad)}" height="${f(v.h + pad * 1.2)}" rx="${f(4 * unit)}" fill="#000" fill-opacity="0.32" stroke="${hex(t.ink, 0.14)}" stroke-width="${f(unit)}"/>`);
    if (t.frame === 'ascii') out.push(`<path d="M${f(v.x - pad + 5 * unit)} ${f(v.y - pad * 0.6)} h${f(-5 * unit)} v${f(v.h + pad * 1.2)} h${f(5 * unit)} M${f(v.x + v.w + pad - 5 * unit)} ${f(v.y - pad * 0.6)} h${f(5 * unit)} v${f(v.h + pad * 1.2)} h${f(-5 * unit)}" fill="none" stroke="${t.accent2}" stroke-width="${f(1.4 * unit)}"${glow}/>`);
  }
  if (tile.light) {
    const l = tile.light;
    out.push(`<circle cx="${f(l.cx)}" cy="${f(l.cy)}" r="${f(l.r * 1.9)}" fill="#000" fill-opacity="0.5" stroke="${t.rim}" stroke-width="${f(1.5 * unit)}"/>`);
  }

  // ---- Controls ----
  if (t.controls === 'leds' && tile.w > 80 * unit) {
    const colors = [t.accent, t.accent2, '#22C55E'];
    colors.forEach((c, i) =>
      out.push(`<circle cx="${f(tile.x + tile.w - 7 * unit - i * 7 * unit)}" cy="${f(tile.y + 6 * unit)}" r="${f(1.8 * unit)}" fill="${c}" fill-opacity="${i === 2 ? 1 : 0.75}"${glow}/>`),
    );
  }
  if (t.controls === 'switches' && tile.w > 80 * unit && tile.h > 50 * unit) {
    const x = tile.x + tile.w - 20 * unit;
    const y = tile.y + tile.h - 9 * unit;
    out.push(`<rect x="${f(x)}" y="${f(y)}" width="${f(12 * unit)}" height="${f(5 * unit)}" rx="${f(2.5 * unit)}" fill="#000" stroke="#525252" stroke-width="${f(0.8 * unit)}"/>`);
    out.push(`<circle cx="${f(x + 9 * unit)}" cy="${f(y + 2.5 * unit)}" r="${f(2 * unit)}" fill="${t.accent}"/>`);
  }
  if (t.controls === 'screws' && tile.w > 60 * unit && tile.h > 50 * unit) {
    const i = 6 * unit;
    for (const [x, y] of [
      [tile.x + i, tile.y + i],
      [tile.x + tile.w - i, tile.y + i],
      [tile.x + tile.w - i, tile.y + tile.h - i],
      [tile.x + i, tile.y + tile.h - i],
    ] as const)
      out.push(...screw(x, y, 2.4 * unit, t));
  }
  return out;
}

/**
 * A gauge face behind a dial: the face, a rim just outside the dial, and inside its track the scale along the dial's
 * own sweep (ticks, numbers when the scale is known, a red zone from the warning), out of the way of the value.
 */
function gauge(tile: TileGeometry, t: Theme, scaleMax: number | null, unit: number, glow: string, tileOpacity: number): string[] {
  void tileOpacity;
  const out: string[] = [];
  const { cx, cy, d, track, start, sweep } = tile.dial!;
  const R = d / 2;
  // Room around the dial inside its tile (the face never crosses the tile's edge).
  const room = Math.max(0, Math.min(cx - tile.x, tile.x + tile.w - cx, cy - tile.y, tile.y + tile.h - cy) - R - unit);
  const ext = Math.min(room, Math.max(3 * unit, d * 0.06));
  out.push(`<circle cx="${f(cx)}" cy="${f(cy)}" r="${f(R + ext)}" fill="url(#face)"/>`);
  // The rim: a metal ring for the classic faces, a glowing one for the futuristic ones.
  if (ext >= 2 * unit) {
    if (t.frame === 'brass' || t.frame === 'gold' || t.frame === 'bevel' || t.frame === 'instrument') {
      out.push(`<circle cx="${f(cx)}" cy="${f(cy)}" r="${f(R + ext * 0.55)}" fill="none" stroke="url(#metal)" stroke-width="${f(Math.max(1.5 * unit, ext * 0.7))}"/>`);
    } else {
      out.push(`<circle cx="${f(cx)}" cy="${f(cy)}" r="${f(R + ext * 0.55)}" fill="none" stroke="${hex(t.rim, 0.9)}" stroke-width="${f(1.4 * unit)}"${glow}/>`);
    }
  }
  if (t.frame === 'holo') out.push(`<circle cx="${f(cx)}" cy="${f(cy)}" r="${f(R + ext * 0.2)}" fill="none" stroke="${hex(t.accent2, 0.5)}" stroke-width="${f(unit)}" stroke-dasharray="${f(3 * unit)} ${f(5 * unit)}"/>`);

  // Inside the track: the scale.
  const inner = R - track - 2 * unit;
  if (inner < 12 * unit) return out;
  if (t.guilloche) for (let k = 0.18; k < 0.62; k += 0.075) out.push(`<circle cx="${f(cx)}" cy="${f(cy)}" r="${f(inner * k)}" fill="none" stroke="${hex(t.accent, 0.1)}" stroke-width="${f(0.8 * unit)}"/>`);
  const major = Math.min(18 * unit, Math.max(4 * unit, inner * 0.11));
  const minor = major * 0.55;
  // The red zone, from the reading's warning (or the last part of the scale) to its end.
  if (t.redZone && sweep < 360) {
    const from = scaleMax && tile.reading.warning != null ? Math.min(0.95, Math.max(0.5, tile.reading.warning / scaleMax)) : 0.85;
    out.push(`<path d="${arc(cx, cy, inner - major * 0.5, start + sweep * from, sweep * (1 - from))}" fill="none" stroke="#EF4444" stroke-opacity="0.8" stroke-width="${f(major * 0.9)}"/>`);
  }
  // Ticks: majors at each tenth of the sweep (a twelfth of a ring), four minors between them.
  const majors = sweep >= 360 ? 12 : 10;
  const steps = majors * 5;
  for (let i = 0; i <= steps; i++) {
    if (sweep >= 360 && i === steps) break;
    const deg = start + (sweep * i) / steps;
    const isMajor = i % 5 === 0;
    const [x1, y1] = point(cx, cy, inner, deg);
    const [x2, y2] = point(cx, cy, inner - (isMajor ? major : minor), deg);
    out.push(`<line x1="${f(x1)}" y1="${f(y1)}" x2="${f(x2)}" y2="${f(y2)}" stroke="${hex(t.ink, isMajor ? 0.85 : 0.45)}" stroke-width="${f(isMajor ? Math.max(1.2, 1.6 * unit) : Math.max(0.8, unit))}"/>`);
  }
  // Numbers inside the ticks, every second major (a ring's quarters), when the scale is known and there is room.
  if (t.numbers && scaleMax && inner >= 34 * unit) {
    const size = Math.min(22 * unit, Math.max(8 * unit, inner * 0.13));
    const r = inner - major - size * 0.85;
    const marks = sweep >= 360 ? [3, 6, 9].map((i) => ({ at: i / 12, n: (scaleMax * i) / 12 })) : [0, 2, 4, 6, 8, 10].map((i) => ({ at: i / 10, n: (scaleMax * i) / 10 }));
    for (const { at, n } of marks) {
      const [x, y] = point(cx, cy, r, start + sweep * at);
      out.push(`<text x="${f(x)}" y="${f(y)}" font-family="${t.font}" font-size="${f(size)}" font-weight="600" fill="${t.ink}" fill-opacity="0.8" text-anchor="middle" dominant-baseline="central">${label(n)}</text>`);
    }
  }
  return out;
}

/** A scope's grid behind a graph: a frame, divisions and finer marks along its axes. */
function scope(c: Rect, t: Theme, unit: number): string[] {
  if (c.w < 10 || c.h < 8) return [];
  const out: string[] = [];
  out.push(`<rect x="${f(c.x - 2 * unit)}" y="${f(c.y - 2 * unit)}" width="${f(c.w + 4 * unit)}" height="${f(c.h + 4 * unit)}" rx="${f(2 * unit)}" fill="#000" fill-opacity="0.35" stroke="${hex(t.ink, 0.22)}" stroke-width="${f(unit)}"/>`);
  const cols = Math.max(4, Math.round(c.w / (40 * unit)));
  const rows = Math.max(2, Math.round(c.h / (22 * unit)));
  for (let i = 1; i < cols; i++) out.push(`<line x1="${f(c.x + (c.w * i) / cols)}" y1="${f(c.y)}" x2="${f(c.x + (c.w * i) / cols)}" y2="${f(c.y + c.h)}" stroke="${hex(t.ink, 0.1)}" stroke-width="${f(unit)}"/>`);
  for (let i = 1; i < rows; i++) out.push(`<line x1="${f(c.x)}" y1="${f(c.y + (c.h * i) / rows)}" x2="${f(c.x + c.w)}" y2="${f(c.y + (c.h * i) / rows)}" stroke="${hex(t.ink, 0.1)}" stroke-width="${f(unit)}" stroke-dasharray="${f(2 * unit)} ${f(3 * unit)}"/>`);
  for (let i = 0; i <= cols * 5; i++) out.push(`<line x1="${f(c.x + (c.w * i) / (cols * 5))}" y1="${f(c.y + c.h)}" x2="${f(c.x + (c.w * i) / (cols * 5))}" y2="${f(c.y + c.h - (i % 5 === 0 ? 4 : 2) * unit)}" stroke="${hex(t.ink, 0.35)}" stroke-width="${f(0.8 * unit)}"/>`);
  return out;
}

/** A bar's channel and its scale above it. */
function channel(b: Rect, t: Theme, scaleMax: number | null, reading: OverlayReading, unit: number): string[] {
  const out: string[] = [];
  const pad = 2.5 * unit;
  out.push(`<rect x="${f(b.x - pad)}" y="${f(b.y - pad)}" width="${f(b.w + 2 * pad)}" height="${f(b.h + 2 * pad)}" rx="${f((b.h + 2 * pad) / 2)}" fill="#000" fill-opacity="0.45" stroke="${hex(t.ink, 0.2)}" stroke-width="${f(unit)}"/>`);
  const top = b.y - pad - 2 * unit;
  for (let i = 0; i <= 20; i++) {
    const x = b.x + (b.w * i) / 20;
    const major = i % 5 === 0;
    out.push(`<line x1="${f(x)}" y1="${f(top)}" x2="${f(x)}" y2="${f(top - (major ? 5 : 2.5) * unit)}" stroke="${hex(t.ink, major ? 0.6 : 0.3)}" stroke-width="${f(0.9 * unit)}"/>`);
  }
  if (t.redZone && scaleMax && reading.warning != null) {
    const from = Math.min(1, reading.warning / scaleMax);
    out.push(`<rect x="${f(b.x + b.w * from)}" y="${f(top - 1.5 * unit)}" width="${f(b.w * (1 - from))}" height="${f(1.5 * unit)}" fill="#EF4444" fill-opacity="0.85"/>`);
  }
  return out;
}

/** Whether an art is drawn here as an SVG (the instruments), not generated on the GPU. */
export const isDrawnArt = (art: string | null | undefined): art is OverlayDrawnArt => !!art && art in ART_THEMES;

/** The panel's art as a picture URL for a preview (cached by what it depends on); null when it cannot be laid out. */
const cache = new Map<string, string | null>();
export function artUrl(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>): string | null {
  const kind = panel.style.art;
  if (!isDrawnArt(kind)) return null;
  const key = artSignature(panel);
  let url = cache.get(key);
  if (url === undefined) {
    const svg = artSvg(panel, kind);
    url = svg ? `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}` : null;
    if (cache.size > 300) cache.delete(cache.keys().next().value!);
    cache.set(key, url);
  }
  return url;
}

/** What a panel's art depends on: its size, readings and look (not its picture or place). */
export function artSignature(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>): string {
  const { image: _image, ...look } = panel.style;
  void _image;
  return JSON.stringify({ layout: panel.layout, columns: panel.columns, readings: panel.readings, title: panel.title, width: panel.width, height: panel.height, look });
}

/**
 * The panel's art as a picture to save for the recorder: drawn at up to twice its size (a scaled monitor draws it
 * larger), as PNG, or JPEG when it is very large (it is opaque); null when it cannot be laid out.
 */
export async function artPicture(panel: Pick<OverlayPanel, 'layout' | 'columns' | 'readings' | 'style' | 'title' | 'width' | 'height'>): Promise<Blob | null> {
  const kind = panel.style.art;
  const svg = isDrawnArt(kind) ? artSvg(panel, kind) : null;
  if (!svg || panel.width == null || panel.height == null) return null;
  const k = Math.min(2, 4096 / Math.max(panel.width, panel.height));
  const picture = new Image();
  picture.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  await picture.decode();
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(panel.width * k));
  canvas.height = Math.max(1, Math.round(panel.height * k));
  canvas.getContext('2d')?.drawImage(picture, 0, 0, canvas.width, canvas.height);
  const large = canvas.width * canvas.height > 2_500_000;
  return new Promise<Blob | null>((resolve) => canvas.toBlob((blob) => resolve(blob), large ? 'image/jpeg' : 'image/png', 0.92));
}
