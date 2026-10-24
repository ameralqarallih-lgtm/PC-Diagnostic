import { forwardRef, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import type { OverlayPanel, OverlayReading, OverlaySource, OverlayStyle, OverlayValueAlign, OverlayVisual } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { ICON_PATHS, type IconId } from './icons';
import { DialBezel, TileBackdrop } from './TileBackdrop';
import { useGlidingValues } from '@/lib/glide';
import { artUrl } from './art';
import { LiveArt, drawsLive } from './liveArt';
import { CHARTS, DIALS, UNDERS, formatSource, heroScale, iconOf, imageUrl, level, maxOf, autoMax, needsHistory, needsScale, pack, patternSvg, patternTile, readable, rgba, shown as shownOf, visualOf, widest, zeros, type History, type Place } from './model';

export interface PanelPreviewProps {
  panel: OverlayPanel;
  sources: ReadonlyMap<string, OverlaySource>;
  /** Canvas pixels per screen pixel, times the monitor's display scaling. */
  scale: number;
  /** Recent values for graphs. */
  history?: History;
  className?: string;
  style?: CSSProperties;
  selected?: boolean;
  /** New values glide there (numbers count, bars, rings and dials sweep), as on the screens; off for still thumbnails. */
  glide?: boolean;
}

/** Each font's line spacing against its size, as Windows reports it (FontFamily.GetLineSpacing), which the recorder uses. */
const LINE_SPACING: Record<string, number> = {
  'Segoe UI': 1.3301,
  'Segoe UI Semibold': 1.3301,
  Bahnschrift: 1.2002,
  Consolas: 1.1709,
  'Cascadia Mono': 1.1621,
  Arial: 1.1499,
  Verdana: 1.2153,
  Tahoma: 1.207,
  Calibri: 1.2207,
  'Courier New': 1.1328,
  Impact: 1.2197,
};

/** A font's line height against its size (Segoe UI's for a font not listed). */
export const lineOf = (font: string) => LINE_SPACING[font] ?? 1.3301;

/** Every size for a panel, as the recorder computes it (OverlayRenderer.Metrics). */
export function metrics(style: OverlayStyle, scale: number, labelScale = 1, visualScale = 1, lineScale = 1) {
  const s = scale;
  const px = style.size * s;
  const spacing = style.spacing / 100;
  const valuePx = (px * style.valueScale) / 100;
  const labelPx = px * (style.caps ? 0.74 : 0.85) * labelScale;
  const titlePx = px * (style.caps ? 0.74 : 0.8);
  const LINE = lineOf(style.font);
  const line = px * LINE;
  const valueLine = valuePx * LINE;
  const labelLine = labelPx * LINE;
  const glowPx = style.glow > 0 ? px * 0.55 * (0.5 + style.glow / 200) : 0;
  const shadowBlur = style.shadow > 0 ? (6 + (14 * style.shadow) / 100) * s : 0;
  const shadowOffset = shadowBlur * 0.35;
  return {
    s,
    /** The font's line height against its size. */
    lineRatio: LINE,
    px,
    valuePx,
    labelPx,
    titlePx,
    unitPx: valuePx * 0.62,
    unitGap: valuePx * 0.12,
    pad: style.padding * s,
    rowGap: px * 0.3 * spacing,
    colGap: px * 0.6 * spacing,
    line,
    valueLine,
    labelLine,
    titleLine: titlePx * LINE,
    iconSize: Math.max(labelLine, line * 0.9),
    tileIcon: labelLine * 1.05,
    barH: Math.max(2, (px * 0.22 * style.barHeight) / 100),
    barGap: px * 0.18,
    inlineRing: Math.max(valueLine * 0.85, line) * visualScale,
    inlineGraphWidth: px * 4.5 * visualScale,
    lineGraphWidth: px * 3.5 * visualScale,
    tileGraphHeight: px * 1.9 * visualScale,
    /** A reading's dial and chart size against the panel's. */
    visualScale,
    graphLine: Math.max(1.5 * s, px * 0.1) * lineScale,
    tilePad: px * 0.6 * Math.min(1.5, spacing),
    tileGap: px * 0.5 * spacing,
    glowPx,
    shadowBlur,
    shadowOffset,
    // Room for the shadow, and for the glow of a border (text and dials glow within the padding), as the recorder.
    margin: Math.ceil(Math.max(shadowBlur + shadowOffset * 0.5, style.border ? glowPx : 0)),
    font: style.font,
    bold: style.bold,
  };
}

type Metrics = ReturnType<typeof metrics>;

/** A percentage sign's size against its number's, and the space before it (as the recorder's). */
const PERCENT_SIZE = 0.7;
const PERCENT_GAP = 0.03;

let measurer: CanvasRenderingContext2D | null | undefined;
const measured = new Map<string, number>();

/**
 * A text's width in a font at a size, as the browser draws it (the recorder measures its text the same way); null where
 * nothing can measure (tests), and the caller estimates.
 */
export function textWidth(text: string, font: string, px: number, bold: boolean): number | null {
  if (measurer === undefined) {
    measurer = typeof navigator === 'undefined' || navigator.userAgent.includes('jsdom') ? null : (document.createElement('canvas').getContext('2d') ?? null);
  }
  if (!measurer) return null;
  const key = `${font}|${bold ? 7 : 4}|${text}`;
  let width = measured.get(key);
  if (width === undefined) {
    measurer.font = `${bold ? 700 : 400} 100px "${font}"`;
    width = measurer.measureText(text).width;
    if (measured.size > 4096) measured.clear();
    measured.set(key, width);
  }
  return (width * px) / 100;
}

interface Item {
  key: string;
  source: string;
  label: string | null;
  value: string;
  unit: string;
  level: 0 | 1 | 2;
  valueColor: string;
  visualColor: string;
  iconColor: string;
  visual: OverlayVisual;
  fraction: number | null;
  max: number | null;
  icon: IconId | null;
  /** Its value's size against the panel's (a tile spanning more cells shows it larger). */
  scale: number;
  /** A status light's color: OK, warning or critical. */
  statusColor: string;
  /** Text after the value: lowest, average and highest, or the trend. */
  extra: string | null;
  /** The widest the value, its unit and the text after it can read: their space, as the recorder keeps it. */
  room: { value: string | null; unit: string | null; extra: string | null };
  /** A text shortened with "…" to fit its space (an app's name). */
  fit: boolean;
  /** Its value not read yet. */
  loading: boolean;
  /** Its own look over the panel's: its style and sizes. */
  s: OverlayStyle;
  m: Metrics;
  labelColor: string;
  unitColor: string;
  /** The unfilled part of its ring, arc, bar or thermometer, and its chart's baseline. */
  track: string;
  /** Its graph's area opacity at the top, 0–1. */
  fillOpacity: number;
}

type Points = readonly (readonly [number, number])[] | undefined;

/** The readings in a graph's window (its last seconds), as [time, value]. */
function windowOf(points: Points, seconds: number): (readonly [number, number])[] {
  if (!points?.length) return [];
  const start = points[points.length - 1]![0] - seconds * 1000;
  return points.filter(([t, v]) => t >= start && Number.isFinite(v));
}

/** "lo 41 · avg 52 · hi 78" or "▲ 12", as the recorder writes them. */
function extraOf(visual: OverlayVisual, source: OverlaySource | undefined, reading: OverlayReading, points: Points, style: OverlayStyle): string | null {
  if (!source || (visual !== 'stats' && visual !== 'trend')) return null;
  const values = windowOf(points, style.graphSeconds).map(([, v]) => v);
  if (values.length === 0) return null;
  const f = (v: number) => formatSource({ ...source, value: v, max: null }, reading.decimals).value;
  // Words, not arrows: small arrows read as ones ("↓84" as "184").
  if (visual === 'stats') return `lo ${f(Math.min(...values))} · avg ${f(values.reduce((a, b) => a + b, 0) / values.length)} · hi ${f(Math.max(...values))}`;
  const change = values[values.length - 1]! - values[0]!;
  const still = Math.abs(change) < Math.max(Math.abs(Math.max(...values) - Math.min(...values)) * 0.05, 1e-9);
  return still ? '● 0' : `${change > 0 ? '▲' : '▼'} ${f(Math.abs(change))}`;
}

/** Relative luminance, 0 (black) to 1 (white). */
export function luminance(hex: string): number {
  const n = Number.parseInt(hex.slice(1), 16);
  const channel = (v: number) => {
    const x = v / 255;
    return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel((n >> 16) & 255) + 0.7152 * channel((n >> 8) & 255) + 0.0722 * channel(n & 255);
}

/** The title's color on the accent band: its own when it stands out, else white or near black (as the recorder). */
export function onBand(title: string, band: string): string {
  const [a, b] = [luminance(title), luminance(band)];
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05) >= 3 ? title : luminance(band) > 0.45 ? '#0F172A' : '#FFFFFF';
}

/** A reading's own look over its panel's: its style, and its sizes (the panel's places and gaps stay), as the recorder. */
function lookOf(reading: OverlayReading, style: OverlayStyle, m: Metrics): { s: OverlayStyle; m: Metrics } {
  const look = reading.look;
  if (!look) return { s: style, m };
  const s: OverlayStyle = {
    ...style,
    font: look.font ?? style.font,
    valueScale: (style.valueScale * (look.valueSize ?? 100)) / 100,
    bold: look.bold ?? style.bold,
    valueColor: look.valueColor ?? style.valueColor,
    labelColor: look.labelColor ?? style.labelColor,
    unitColor: look.unitColor ?? style.unitColor,
    showLabels: look.showLabel ?? style.showLabels,
    showUnits: look.showUnit ?? style.showUnits,
    valueAlign: look.align ?? style.valueAlign,
    valueVertical: look.vertical ?? style.valueVertical,
    barStyle: look.barStyle ?? style.barStyle,
    barHeight: (style.barHeight * (look.thickness ?? 100)) / 100,
  };
  return { s, m: metrics(s, m.s, (look.labelSize ?? 100) / 100, (look.visualSize ?? 100) / 100, (look.thickness ?? 100) / 100) };
}

function makeItem(reading: OverlayReading, index: number, source: OverlaySource | undefined, panelStyle: OverlayStyle, panelMetrics: Metrics, scale = 1, points?: Points): Item {
  const { s: style, m } = lookOf(reading, panelStyle, panelMetrics);
  const look = reading.look;
  const labelColor = readable(style.labelColor, style);
  const shown = shownOf(source, reading);
  const lvl = level(reading, source?.value, reading.max ?? source?.max);
  const levelColor = lvl === 2 ? style.criticalColor : lvl === 1 ? style.warningColor : null;
  let visual = visualOf(reading);
  // A dial, bar or thermometer needs a full scale (a status light only limits): without one of its own it is drawn against
  // an automatic one, and only a text reading falls back to its value. History needs a number.
  const own = maxOf(reading, source);
  const max = needsScale(visual) && !(own != null && own > 0) ? autoMax(source) : own;
  if ((needsScale(visual) && source?.text != null) || ((needsHistory(visual) || visual === 'status') && source?.text != null)) visual = 'none';
  return {
    key: `${reading.source}-${index}`,
    source: reading.source,
    label: style.showLabels ? (reading.label ? reading.label : (source?.label ?? reading.source)) : null,
    // Not read yet: a loading mark where its value goes (the recorder's dots, moving).
    value: source?.loading ? '···' : shown.value,
    loading: source?.loading === true,
    unit: style.showUnits ? shown.unit : '',
    level: lvl,
    valueColor: levelColor ?? (look?.valueColor ? null : reading.color) ?? style.valueColor,
    visualColor: levelColor ?? reading.color ?? style.accent,
    iconColor: levelColor ?? style.iconColor ?? style.accent,
    visual,
    fraction: max != null && max > 0 && source?.value != null ? Math.max(0, Math.min(1, source.value / max)) : null,
    max,
    icon: style.icons ? iconOf(reading, source) : null,
    scale,
    // A status light without a reading is neither OK nor a problem.
    statusColor: source?.value == null ? style.unitColor : (levelColor ?? style.okColor),
    extra: extraOf(visual, source, reading, points, style),
    room: roomOf(visual, source, reading, style, shown.value),
    fit: source?.text != null && (source.room?.length ?? 0) > 0,
    s: style,
    m,
    labelColor,
    unitColor: readable(style.unitColor, style),
    track: rgba(look?.trackColor ?? labelColor, look?.trackOpacity ?? 22),
    fillOpacity: (look?.fillOpacity ?? 43) / 100,
  };
}

/** The value's, unit's and extra text's widest (estimated: digits as wide as each other, a point or space narrower). */
function roomOf(visual: OverlayVisual, source: OverlaySource | undefined, reading: OverlayReading, style: OverlayStyle, now: string): Item['room'] {
  // The widest of a few texts in the same font: measured where the browser can, else estimated.
  const pick = (texts: string[], bold: boolean) => {
    const width = (t: string) => textWidth(t, style.font, 100, bold) ?? spanOf(t);
    return texts.length ? texts.reduce((a, b) => (width(b) > width(a) ? b : a)) : null;
  };
  const { values: wide, units } = widest(source, reading);
  // A number wider than its usual (1200 fps) widens its space rather than being cut; a text with a space is cut to it.
  const values = source?.text != null && wide.length ? wide : [...wide, zeros(now)];
  let extra: string | null = null;
  if ((visual === 'stats' || visual === 'trend') && source && source.text == null) {
    const raw = widest(source, reading, true).values;
    extra = pick(visual === 'stats' ? raw.map((v) => `lo ${v} · avg ${v} · hi ${v}`) : [...raw.flatMap((v) => [`▲ ${v}`, `▼ ${v}`]), '● 0'], false);
  }
  return { value: pick(values, style.bold), unit: style.showUnits ? pick(units, false) : null, extra };
}

/** A text's rough width in digits (for choosing the widest of a few texts in the same font). */
function spanOf(text: string): number {
  let span = 0;
  for (const c of text) span += /[0-9]/.test(c) ? 1 : c === '.' || c === ',' || c === ' ' || c === '/' ? 0.45 : /[A-Zmw]/.test(c) ? 1.1 : 0.85;
  return span;
}

/**
 * A panel drawn the way the recorder draws it on the screen (same layouts, sizes, colors, effects, icons, bars, rings
 * and graphs), at the editor's scale. The recorder is the reference: this is a close preview, not a pixel copy.
 */
export const PanelPreview = forwardRef<HTMLDivElement, PanelPreviewProps>(function PanelPreview({ panel, sources: given, scale, history, className, style, selected, glide = true }, ref) {
  const sources = useGlidingValues(given, (src) => (src.text == null && !src.loading ? src.value : null), (src, value) => ({ ...src, value }), glide);
  const s = panel.style;
  const m = metrics(s, scale);
  const columns = Math.max(1, Math.min(6, panel.columns ?? 2));
  const places = panel.layout === 'tiles' ? pack(panel.readings, columns) : null;
  const items = panel.readings.map((r, i) => makeItem(r, i, sources.get(r.source), s, m, places ? heroScale(places[i]!) : 1, history?.get(r.source)));
  const hasBox = s.backgroundOpacity > 0 || s.border != null;

  // An automatic size never shrinks while the same panel is shown (so it does not jump as values change); a change
  // to the panel itself, or to the scale, starts afresh.
  const box = useRef<HTMLDivElement>(null);
  // A size kept for the same panel, scaling and set of readings with a value source (one not there yet shows its id).
  const signature = JSON.stringify({ ...panel, x: 0, y: 0, scale, present: panel.readings.map((r) => sources.has(r.source)) });
  const grown = useRef<{ signature: string; width: number; height: number }>({ signature, width: 0, height: 0 });
  if (grown.current.signature !== signature) grown.current = { signature, width: 0, height: 0 };
  // The kept size is for its own signature only: kept at another scale (zoomed, turned, resized), applying it once
  // before measuring again made the panel measure that size and keep it for good (a panel stuck huge).
  const [floor, setFloor] = useState({ signature, width: 0, height: 0 });
  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    const next = { width: Math.max(grown.current.width, element.offsetWidth), height: Math.max(grown.current.height, element.offsetHeight) };
    grown.current = { signature, ...next };
    if (floor.signature !== signature || next.width !== floor.width || next.height !== floor.height) setFloor({ signature, ...next });
  });
  const fixedWidth = panel.width != null ? panel.width * scale : null;
  const fixedHeight = panel.height != null ? panel.height * scale : null;

  // Labels readable on the panel (a dim color on a dark panel over a light desktop disappeared); each reading keeps its own.
  const labelColor = readable(s.labelColor, s);
  const textShadow = (color: string | null, glow: boolean): string | undefined => {
    const parts: string[] = [];
    if (glow && s.glow > 0 && color) parts.push(`0 0 ${m.glowPx * 0.45}px ${rgba(color, s.glow * 0.8)}`, `0 0 ${m.glowPx}px ${rgba(color, s.glow * 0.5)}`);
    if (s.outline) {
      const o = Math.max(1, m.px * 0.09);
      parts.push(`0 0 ${o}px #000`, `0 0 ${o}px #000`, `0 0 ${o / 2}px #000`);
    }
    return parts.length ? parts.join(', ') : undefined;
  };

  // The background in the recorder's order: its color, the picture, the tint, the pattern, the sheen, the accent edge.
  const backgrounds: string[] = [];
  if (s.backgroundOpacity > 0) {
    backgrounds.push(s.background2 ? `linear-gradient(${s.gradientAngle}deg, ${rgba(s.background, s.backgroundOpacity)}, ${rgba(s.background2, s.backgroundOpacity)})` : `linear-gradient(${rgba(s.background, s.backgroundOpacity)}, ${rgba(s.background, s.backgroundOpacity)})`);
  }
  const shadows: string[] = [];
  if (s.shadow > 0 && hasBox) shadows.push(`0 ${m.shadowOffset}px ${m.shadowBlur}px ${rgba('#000000', 50 * (s.shadow / 100) + 10)}`);
  if (s.border && s.glow > 0) shadows.push(`0 0 ${m.glowPx * 0.8}px ${rgba(s.border, s.glow * 0.7)}`);
  const edge = Math.max(3, 3.5 * m.s);
  const layer: CSSProperties = { position: 'absolute', inset: 0, borderRadius: 'inherit', pointerEvents: 'none', zIndex: -1 };
  const imageSize = useImageSize(s.image ?? null);
  // A picture drawn for this panel from its art is drawn here live (it follows the panel at once); the recorder shows
  // the copy saved as its picture.
  // A generated scene, glass over a library picture, or an animation is drawn live on a canvas (liveArt.tsx).
  const live = drawsLive(panel);
  const art = s.art && !live ? artUrl(panel) : null;
  const picture = live ? null : (art ?? (s.image ? imageUrl(s.image) : null));
  const fit = art ? 'stretch' : (s.imageFit ?? 'cover');
  const position = s.imagePosition ?? 'center';
  const blur = (s.imageBlur ?? 0) * scale;
  const tint = s.imageTint && (s.imageTintOpacity ?? 0) > 0 && <span style={{ position: 'absolute', inset: 0, background: rgba(s.imageTint, s.imageTintOpacity ?? 0) }} />;
  const layers = (
    <>
      {live && (
        <span aria-hidden style={{ ...layer, overflow: 'hidden' }} data-image={s.image ?? undefined} data-art={s.art ?? undefined}>
          <span
            style={{
              position: 'absolute',
              inset: 0,
              opacity: s.art ? 1 : (s.imageOpacity ?? 100) / 100,
              filter: s.art ? undefined : [blur > 0 ? `blur(${blur}px)` : '', s.imageGray ? 'grayscale(1)' : ''].filter(Boolean).join(' ') || undefined,
            }}
          >
            <LiveArt panel={panel} />
          </span>
          {tint}
        </span>
      )}
      {picture && (
        <span aria-hidden style={{ ...layer, overflow: 'hidden' }} data-image={s.image ?? undefined} data-art={art ? s.art : undefined}>
          <span
            style={{
              position: 'absolute',
              inset: -blur * 2,
              backgroundImage: `url("${picture}")`,
              backgroundRepeat: fit === 'tile' ? 'repeat' : 'no-repeat',
              backgroundSize:
                fit === 'cover' ? 'cover' : fit === 'contain' ? 'contain' : fit === 'stretch' ? '100% 100%' : imageSize ? `${imageSize.width * scale}px ${imageSize.height * scale}px` : 'auto',
              backgroundPosition: fit === 'tile' ? `${blur * 2}px ${blur * 2}px` : position,
              backgroundOrigin: 'content-box',
              padding: blur * 2,
              opacity: (s.imageOpacity ?? 100) / 100,
              filter: [blur > 0 ? `blur(${blur}px)` : '', s.imageGray ? 'grayscale(1)' : ''].filter(Boolean).join(' ') || undefined,
            }}
          />
          {tint}
        </span>
      )}
      {s.pattern && (s.patternOpacity ?? 0) > 0 && (
        <span
          aria-hidden
          data-pattern={s.pattern}
          style={{
            ...layer,
            backgroundImage: patternSvg(s.pattern, 12 * scale * ((s.patternScale ?? 100) / 100), s.patternColor ?? (luminance(s.background) < 0.5 ? '#FFFFFF' : '#000000'), (s.patternOpacity ?? 0) / 100),
            backgroundSize: (() => {
              const t = patternTile(s.pattern, 12 * scale * ((s.patternScale ?? 100) / 100));
              return `${t.width}px ${t.height}px`;
            })(),
          }}
        />
      )}
      {s.sheen && s.backgroundOpacity > 0 && (
        <span aria-hidden style={{ ...layer, background: 'linear-gradient(180deg, rgba(255,255,255,0.12), rgba(255,255,255,0) 50%)', boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.35)' }} />
      )}
      {s.accentEdge !== 'none' && (
        <span
          aria-hidden
          style={{ ...layer, overflow: 'hidden' }}
        >
          <span
            style={{
              position: 'absolute',
              background: s.accent,
              ...(s.accentEdge === 'left' ? { left: 0, top: 0, bottom: 0, width: edge } : s.accentEdge === 'top' ? { left: 0, right: 0, top: 0, height: edge } : { left: 0, right: 0, bottom: 0, height: edge }),
            }}
          />
        </span>
      )}
      {s.motion && s.motion !== 'none' && (
        // The moving background: over the color, picture and pattern, under the readings (as the recorder draws it).
        <span aria-hidden data-motion={s.motion} className="motion-layer" style={{ ...layer, overflow: 'hidden' }}>
          <span
            className={`motion-${s.motion}`}
            style={
              {
                '--motion-accent': s.accent,
                '--motion-second': s.background2 ?? s.accent,
                '--motion-strength': Math.min(100, Math.max(35, s.backgroundOpacity)) / 100,
                '--motion-speed': Math.min(300, Math.max(25, s.motionSpeed ?? 100)) / 100,
                '--motion-unit': `${22 * scale}px`,
              } as CSSProperties
            }
          />
        </span>
      )}
    </>
  );

  const boxStyle: CSSProperties = {
    position: 'relative',
    // Its own stacking order: the background layers sit under the readings, over the box's color.
    isolation: 'isolate',
    fontFamily: `"${s.font}", "Segoe UI", sans-serif`,
    fontSize: m.px,
    lineHeight: m.lineRatio,
    padding: m.pad,
    borderRadius: s.cornerRadius * scale,
    background: backgrounds.length ? backgrounds.join(', ') : 'transparent',
    border: s.border ? `${s.borderWidth * scale}px solid ${s.border}` : undefined,
    boxShadow: shadows.length ? shadows.join(', ') : undefined,
    color: s.valueColor,
    whiteSpace: 'nowrap',
    overflow: s.titleBar || fixedWidth != null || fixedHeight != null ? 'hidden' : undefined,
    boxSizing: 'border-box',
    width: fixedWidth ?? undefined,
    height: fixedHeight ?? undefined,
    minWidth: fixedWidth == null && floor.signature === signature ? floor.width || undefined : undefined,
    minHeight: fixedHeight == null && floor.signature === signature ? floor.height || undefined : undefined,
    display: 'flex',
    flexDirection: 'column',
  };

  // A reading in its own font (the panel's is the box's).
  const font = (item: Item) => (item.s.font !== s.font ? `"${item.s.font}", "Segoe UI", sans-serif` : undefined);
  const caps: CSSProperties = s.caps ? { textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 700 } : { fontWeight: 400 };
  const label = (item: Item) =>
    item.label && (
      <span style={{ color: item.labelColor, fontSize: item.m.labelPx, lineHeight: item.m.lineRatio, fontFamily: font(item), textShadow: textShadow(null, false), ...caps }} data-label>
        {item.label}
      </span>
    );
  // Each value has a fixed space: as wide as the widest it can read (laid out under it, hidden). The value, its unit
  // and the text after it sit in it as one, on the left, in the middle or on the right, so a value changing never
  // moves another (as the recorder draws them). Digits of even width, as the recorder measures them.
  // A percentage's sign belongs to its number ("50%"): in the value's color and weight, close to it, as the recorder draws it.
  const unitStyle = (item: Item): CSSProperties =>
    item.unit === '%' || item.room.unit === '%'
      ? { color: item.valueColor, fontWeight: item.s.bold ? 700 : 400, fontSize: item.m.valuePx * item.scale * PERCENT_SIZE, marginLeft: item.m.valuePx * item.scale * PERCENT_GAP, textShadow: textShadow(item.valueColor, true) }
      : { color: item.unitColor, fontWeight: 400, fontSize: item.m.unitPx * item.scale, marginLeft: item.m.unitGap * item.scale, textShadow: textShadow(null, false) };
  const extraStyle = (item: Item): CSSProperties => ({ color: item.labelColor, fontWeight: 400, fontSize: item.m.unitPx * item.scale, marginLeft: item.m.unitGap * item.scale * 2.5, textShadow: textShadow(null, false) });
  const value = (item: Item, fill = false, align: OverlayValueAlign = item.s.valueAlign ?? 'center') => (
    <span
      className={cn('value-slot', fill && 'value-slot-fill')}
      style={{ color: item.valueColor, fontSize: item.m.valuePx * item.scale, lineHeight: item.m.lineRatio, fontFamily: font(item), fontWeight: item.s.bold ? 700 : 400, fontVariantNumeric: 'tabular-nums', textShadow: textShadow(item.valueColor, true), textAlign: align }}
      data-value
      data-align={align}
      data-source={item.source}
    >
      <span className={cn('value-text', item.fit && 'value-fit', item.loading && 'animate-pulse')} data-loading={item.loading || undefined}>
        <span>{item.value}</span>
        {item.unit && <span style={unitStyle(item)}>{item.unit}</span>}
        {item.extra && (
          <span style={extraStyle(item)} data-extra>
            {item.extra}
          </span>
        )}
      </span>
      <span className="value-ghost" aria-hidden>
        <span data-room={item.room.value ?? ''} />
        {item.room.unit && <span data-room={item.room.unit} style={unitStyle(item)} />}
        {item.room.extra && <span data-room={item.room.extra} style={extraStyle(item)} />}
      </span>
    </span>
  );
  const dial = (item: Item, size: number, inline: boolean) => <Dial size={size} thickness={inline ? size * 0.16 : Math.max(2 * m.s, Math.min(size * 0.085, m.px * 0.9))} item={item} style={item.s} m={item.m} inline={inline} />;
  const icon = (item: Item, size: number) => item.icon && <Icon id={item.icon} size={size} color={item.iconColor} glow={s.glow > 0 ? m.glowPx : 0} outline={s.outline} />;
  const points = (item: Item) => history?.get(item.source);

  const title = panel.title ? (
    s.titleBar ? (
      <div style={{ margin: `${-m.pad}px ${-m.pad}px ${m.pad * 0.8}px`, padding: `0 ${m.pad}px`, height: m.titleLine + m.pad * 0.9, display: 'flex', alignItems: 'center', background: rgba(s.accent, 92) }}>
        <span style={{ color: onBand(s.titleColor, s.accent), fontSize: m.titlePx, fontWeight: 700, lineHeight: m.lineRatio, ...(s.caps ? { textTransform: 'uppercase', letterSpacing: '0.08em' } : {}) }}>{panel.title}</span>
      </div>
    ) : (
      <div style={{ color: s.titleColor, fontWeight: 700, fontSize: m.titlePx, lineHeight: m.lineRatio, marginBottom: m.rowGap, textShadow: textShadow(s.titleColor, true), ...(s.caps ? { textTransform: 'uppercase', letterSpacing: '0.08em' } : {}) }}>
        {panel.title}
      </div>
    )
  ) : null;

  let body: ReactNode;
  if (items.length === 0) {
    body = !panel.title && <div style={{ color: labelColor, fontSize: m.labelPx, minWidth: m.px * 6 }}>Empty panel</div>;
  } else if (panel.layout === 'line') {
    // One height for every reading's line (the tallest: a value, a label, an icon or a ring), each in its middle, and the
    // line in the middle of a panel taller than it (a size kept or chosen), as the recorder draws it.
    const lineHeight = Math.max(...items.map((i) => Math.max(i.m.valueLine, i.label ? i.m.labelLine : 0, i.icon ? i.m.iconSize : 0, DIALS.includes(i.visual) ? i.m.inlineRing : 0)));
    body = (
      <div className="flex flex-1 items-center">
        <div className="flex items-start" style={{ gap: m.colGap * 1.4 }}>
          {items.map((item) => (
            <span key={item.key} className="inline-flex flex-col">
              <span className="inline-flex items-center" style={{ minHeight: lineHeight }}>
                {item.icon && <span style={{ marginRight: m.colGap * 0.35, display: 'inline-flex' }}>{icon(item, item.m.iconSize)}</span>}
                <span className="inline-flex items-baseline">
                  {item.label && <span style={{ marginRight: m.px * 0.35 }}>{label(item)}</span>}
                  {value(item)}
                </span>
                {CHARTS.includes(item.visual) && (
                  <span style={{ marginLeft: m.colGap * 0.5, display: 'inline-flex' }}>
                    <Chart width={item.m.lineGraphWidth} height={Math.max(item.m.valueLine, item.m.iconSize) * 0.72} item={item} points={points(item)} style={item.s} m={item.m} />
                  </span>
                )}
                {DIALS.includes(item.visual) && <span style={{ marginLeft: m.colGap * 0.4, display: 'inline-flex' }}>{dial(item, item.m.inlineRing, true)}</span>}
              </span>
              {UNDERS.includes(item.visual) && <Under item={item} style={item.s} m={item.m} gap={m.barGap} />}
            </span>
          ))}
        </div>
      </div>
    );
  } else if (panel.layout === 'tiles') {
    const cols = Math.min(columns, Math.max(...places!.map((p) => p.column + p.columns)));
    const tileBase = s.tileColor ?? (luminance(s.background) < 0.5 ? '#FFFFFF' : '#000000');
    const tint = rgba(tileBase, s.tileOpacity);
    const backdrop = s.backdrop ?? 'none';
    const tileRadius = Math.max(2 * m.s, s.cornerRadius * scale * 0.65);
    body = (
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, 1fr)`, gridAutoRows: '1fr', gap: m.tileGap, flex: 1 }}>
        {items.map((item, i) => {
          const place: Place = places![i]!;
          const ring = ringDiameter(item, item.m) * Math.max(1, item.m.visualScale);
          // A status tile is tinted in its status color, like the dashboard's status tiles.
          const fill = item.visual === 'status' ? rgba(item.statusColor, Math.max(18, s.tileOpacity * 3)) : tint;
          const header = item.label || item.icon ? Math.max(item.m.labelLine, item.icon ? item.m.tileIcon : 0) : 0;
          // A chevron tile is drawn in its cut shape by its backdrop.
          const cut = backdrop === 'chevron';
          return (
            <div
              key={item.key}
              className="flex flex-col"
              data-tile
              data-source={item.source}
              style={{
                gridColumn: `${place.column + 1} / span ${place.columns}`,
                gridRow: `${place.row + 1} / span ${place.rows}`,
                padding: m.tilePad,
                minWidth: m.px * 5.5 + 2 * m.tilePad,
                borderRadius: tileRadius,
                background: cut ? undefined : fill,
                boxShadow: item.level > 0 && backdrop === 'none' ? `inset 0 0 0 ${Math.max(1, 1.5 * m.s)}px ${rgba(item.visualColor, 80)}` : undefined,
                ...(backdrop !== 'none' ? { position: 'relative', isolation: 'isolate' } : {}),
              }}
            >
              {backdrop !== 'none' && (
                <TileBackdrop
                  kind={backdrop}
                  s={m.s}
                  radius={tileRadius}
                  pad={m.tilePad}
                  accent={s.accent}
                  underline={header > 0 ? m.tilePad + header + m.px * 0.175 : null}
                  dial={item.visual === 'ring' || item.visual === 'arc' || item.visual === 'speed'}
                  level={item.level > 0 ? item.visualColor : null}
                  fill={cut ? fill : undefined}
                />
              )}
              {(item.label || item.icon) && (
                <div className="flex items-center" style={{ minHeight: Math.max(item.m.labelLine, item.icon ? item.m.tileIcon : 0), marginBottom: m.px * 0.35, gap: m.px * 0.3 }}>
                  {icon(item, item.m.tileIcon)}
                  {label(item)}
                </div>
              )}
              {item.visual === 'ring' || item.visual === 'arc' || item.visual === 'speed' ? (
                // The dial fills its tile (a large tile gets a large dial), as the recorder draws it: as wide as the tile
                // or as tall as the room under the label allows, whichever is less, in the middle of that room.
                <div className="flex min-h-0 flex-1 items-center justify-center" style={{ containerType: 'size', minWidth: ring, minHeight: ring * dialHeight(item) }}>
                  <div className="relative shrink-0" style={{ ...dialBox(item, ring), ...(backdrop === 'bezel' ? { isolation: 'isolate' } : {}) }}>
                    {backdrop === 'bezel' && <DialBezel px={m.px} pad={m.tilePad} s={m.s} accent={s.accent} tint={rgba(tileBase, Math.min(40, Math.max(10, Math.trunc((s.tileOpacity * 5) / 2))))} />}
                    <Dial size={ring} thickness={Math.max(2 * m.s, Math.min(ring * 0.085, m.px * 0.9))} item={item} style={item.s} m={item.m} fill />
                    <span
                      className={cn('absolute inset-x-0 flex justify-center', item.visual === 'ring' && 'inset-y-0 items-center')}
                      style={item.visual === 'arc' ? { top: '52%', transform: 'translateY(-50%)' } : item.visual === 'speed' ? { top: '66%' } : undefined}
                    >
                      {value(item, false, 'center')}
                    </span>
                  </div>
                </div>
              ) : (
                <>
                  <div
                    className={cn(
                      'flex',
                      // Up and down in its room (above a bar), as the recorder: the value alone in the middle, over a bar at the top, unless chosen.
                      (item.visual === 'none' || item.visual === 'status' || item.visual === 'stats' || item.visual === 'trend' || (UNDERS.includes(item.visual) && item.s.valueVertical && item.s.valueVertical !== 'top')) && 'flex-1',
                      (item.visual === 'none' || item.visual === 'status' || item.visual === 'stats' || item.visual === 'trend' || UNDERS.includes(item.visual)) &&
                        { top: 'items-start', middle: 'items-center', bottom: 'items-end' }[item.s.valueVertical ?? (UNDERS.includes(item.visual) ? 'top' : 'middle')],
                    )}
                  >
                    {value(item, true)}
                    {/* The light sits at the end of the value's line (in the header it would crowd the label). */}
                    {item.visual === 'status' && (
                      <span className="ml-auto flex shrink-0" style={{ paddingLeft: m.px * 0.5, paddingRight: item.m.valueLine * 0.12 }}>
                        <StatusLight size={item.m.valueLine * 0.4 * item.scale * item.m.visualScale} item={item} style={item.s} m={item.m} />
                      </span>
                    )}
                  </div>
                  {CHARTS.includes(item.visual) && (
                    // A graph fills its tile but never widens it (an SVG's own width does not follow the scale), as the recorder.
                    <div className="flex min-h-0 flex-1 flex-col justify-end" style={{ paddingTop: m.px * 0.3, contain: 'inline-size' }}>
                      <Chart width="100%" height={place.rows > 1 ? `${Math.min(1, item.m.visualScale) * 100}%` : item.m.tileGraphHeight} minHeight={item.m.tileGraphHeight} item={item} points={points(item)} style={item.s} m={item.m} />
                    </div>
                  )}
                  {UNDERS.includes(item.visual) && (
                    <div style={{ marginTop: 'auto' }}>
                      <Under item={item} style={item.s} m={item.m} gap={m.barGap} />
                    </div>
                  )}
                </>
              )}
            </div>
          );
        })}
      </div>
    );
  } else {
    const split = Math.max(1, Math.min(4, panel.rowColumns ?? 1, items.length));
    const groups = Array.from({ length: split }, (_, j) => items.filter((_, i) => i % split === j));
    const tracks = groups.map((g) => ({
      icon: g.some((i) => i.icon),
      label: g.some((i) => i.label),
      graph: g.some((i) => CHARTS.includes(i.visual)),
      ring: g.some((i) => DIALS.includes(i.visual)),
    }));
    // Track numbers of each column's parts (1-based), with a gap track between columns.
    let next = 1;
    const at = tracks.map((t, j) => {
      const start = next;
      const icon = t.icon ? next++ : 0;
      const label = t.label ? next++ : 0;
      const graph = t.graph ? next++ : 0;
      const value = next++;
      const ring = t.ring ? next++ : 0;
      const end = next;
      if (j < tracks.length - 1) next++;
      return { start, icon, label, graph, value, ring, end };
    });
    const template = tracks
      .map((t, j) => [t.icon && 'auto', t.label && 'auto', t.graph && 'auto', 'auto', t.ring && 'auto', j < tracks.length - 1 && `${m.colGap * 1.6}px`].filter(Boolean).join(' '))
      .join(' ');
    const rowsOf = Math.ceil(items.length / split);
    body = (
      <div style={{ display: 'grid', gridTemplateColumns: template, alignItems: 'center', columnGap: 0 }}>
        {Array.from({ length: rowsOf }, (_, r) => {
          const row = items.slice(r * split, r * split + split);
          const h = Math.max(...row.map((item) => Math.max(item.m.valueLine, item.label ? item.m.labelLine : 0, item.icon ? item.m.iconSize : 0, DIALS.includes(item.visual) ? item.m.inlineRing : 0)));
          const bars = row.some((i) => UNDERS.includes(i.visual));
          const line = r * 2 + 1;
          return row.map((item, j) => {
            const a = at[j]!;
            return (
              <div key={item.key} className="contents">
                {a.icon > 0 && <span style={{ gridColumn: a.icon, gridRow: line, paddingRight: m.colGap * 0.55, height: h, display: 'flex', alignItems: 'center', marginTop: r ? m.rowGap : 0 }}>{icon(item, item.m.iconSize)}</span>}
                {a.label > 0 && <span style={{ gridColumn: a.label, gridRow: line, paddingRight: m.colGap, alignSelf: 'baseline', marginTop: r ? m.rowGap : 0 }}>{label(item)}</span>}
                {a.graph > 0 && (
                  <span style={{ gridColumn: a.graph, gridRow: line, paddingRight: m.colGap * 0.6, display: 'flex', alignItems: 'center', height: h, marginTop: r ? m.rowGap : 0 }}>
                    {CHARTS.includes(item.visual) && <Chart width={item.m.inlineGraphWidth} height={h * 0.78} item={item} points={points(item)} style={item.s} m={item.m} />}
                  </span>
                )}
                {/* The value column is each row's value space. */}
                <span style={{ gridColumn: a.value, gridRow: line, alignSelf: 'baseline', marginTop: r ? m.rowGap : 0 }}>{value(item, true)}</span>
                {a.ring > 0 && (
                  <span style={{ gridColumn: a.ring, gridRow: line, paddingLeft: m.colGap * 0.5, display: 'flex', alignItems: 'center', height: h, marginTop: r ? m.rowGap : 0 }}>
                    {DIALS.includes(item.visual) && dial(item, item.m.inlineRing, true)}
                  </span>
                )}
                {bars && UNDERS.includes(item.visual) && (
                  <span style={{ gridColumn: `${a.icon > 0 ? a.icon + 1 : a.start} / ${a.end}`, gridRow: line + 1, marginTop: m.barGap }}>
                    <Under item={item} style={item.s} m={item.m} gap={0} />
                  </span>
                )}
              </div>
            );
          });
        })}
      </div>
    );
  }

  return (
    // Always its natural size: next to a screen's edge an absolutely placed box could otherwise be squeezed.
    <div ref={ref} className={cn('inline-block select-none', className)} style={{ padding: m.margin, opacity: s.opacity / 100, width: 'max-content', ...style }} data-panel={panel.id}>
      <div ref={box} style={boxStyle} className={cn(selected && 'ring-2 ring-accent ring-offset-1 ring-offset-transparent')}>
        {layers}
        {title}
        {body}
      </div>
    </div>
  );
});

/** Background pictures' sizes in pixels, as they load (a tiled or centred picture is drawn at its own size). */
const imageSizes = new Map<string, { width: number; height: number }>();

function useImageSize(id: string | null): { width: number; height: number } | undefined {
  const [, rerender] = useState(0);
  useLayoutEffect(() => {
    if (!id || imageSizes.has(id) || typeof Image === 'undefined') return undefined;
    let alive = true;
    const picture = new Image();
    picture.onload = () => {
      imageSizes.set(id, { width: picture.naturalWidth, height: picture.naturalHeight });
      if (alive) rerender((n) => n + 1);
    };
    picture.src = imageUrl(id);
    return () => {
      alive = false;
    };
  }, [id]);
  return id ? imageSizes.get(id) : undefined;
}

/** Text below this is drawn bigger by browsers (their minimum font size), so small previews are drawn at it and shrunk. */
const SMALLEST_TEXT = 9;

export interface ShrunkPreviewProps extends Omit<PanelPreviewProps, 'style' | 'className'> {
  /** Fit inside this box (centered, never enlarged) instead of drawing at `scale`. */
  fit?: { width: number; height: number };
  /** The size it takes, in the page's pixels. */
  onSize?: (size: { width: number; height: number }) => void;
  className?: string;
  style?: CSSProperties;
}

/**
 * A preview drawn at a readable size and shrunk as a whole with a CSS transform: true to its proportions at tiny scales
 * (the monitor miniatures) and fitted into a box (the theme gallery).
 */
export function ShrunkPreview({ panel, sources, history, scale, fit, onSize, className, style }: ShrunkPreviewProps) {
  const inner = useRef<HTMLDivElement>(null);
  const [natural, setNatural] = useState({ width: 0, height: 0 });
  const drawn = fit ? 1 : Math.max(scale, SMALLEST_TEXT / Math.max(1, panel.style.size * 0.74));
  useLayoutEffect(() => {
    const element = inner.current;
    if (element && (element.offsetWidth !== natural.width || element.offsetHeight !== natural.height)) setNatural({ width: element.offsetWidth, height: element.offsetHeight });
  });
  const k = fit ? Math.min(1, fit.width / Math.max(1, natural.width), fit.height / Math.max(1, natural.height)) : scale / drawn;
  const size = { width: natural.width * k, height: natural.height * k };
  const reported = useRef({ width: -1, height: -1 });
  useLayoutEffect(() => {
    if (onSize && (Math.abs(reported.current.width - size.width) > 0.5 || Math.abs(reported.current.height - size.height) > 0.5)) {
      reported.current = size;
      onSize(size);
    }
  });
  return (
    <div className={cn('relative overflow-visible', className)} style={{ width: size.width, height: size.height, ...style }}>
      <div style={{ position: 'absolute', left: 0, top: 0, transform: `scale(${k})`, transformOrigin: '0 0' }}>
        <PanelPreview ref={inner} panel={panel} sources={sources} history={history} scale={drawn} />
      </div>
    </div>
  );
}

/** The room a value, its unit and the text after it take (as the recorder's ValueWidth), or null where nothing can measure. */
function valueWidth(item: Item, m: Metrics): number | null {
  const px = m.valuePx * item.scale;
  const value = textWidth(zeros(item.room.value ?? item.value), m.font, px, m.bold);
  const unitText = item.room.unit ?? item.unit;
  const percent = unitText === '%';
  const unit = unitText ? textWidth(unitText, m.font, percent ? px * PERCENT_SIZE : m.unitPx * item.scale, percent && m.bold) : 0;
  const extraText = item.room.extra ?? item.extra;
  const extra = extraText ? textWidth(extraText, m.font, m.unitPx * item.scale, false) : 0;
  if (value == null || unit == null || extra == null) return null;
  return value + (unitText ? (percent ? px * PERCENT_GAP : m.unitGap * item.scale) + unit : 0) + (extraText ? m.unitGap * item.scale * 2.5 + extra : 0);
}

/** How tall a dial is against its width (an arc or a speedometer is open at the bottom). */
const dialHeight = (item: Item) => (item.visual === 'ring' ? 1 : 0.92);

/**
 * A dial's square in its tile's room (a size container): as the recorder's min(width, height / dialHeight), never less
 * than its own diameter; centred by its open-bottomed height (an arc's square reaches below it).
 */
function dialBox(item: Item, ring: number): CSSProperties {
  const k = dialHeight(item);
  const d = `max(${ring.toFixed(2)}px, calc(min(100cqw, ${(100 / k).toFixed(3)}cqh) * ${Math.min(1, item.m.visualScale)}))`;
  return { width: d, height: d, marginBottom: k < 1 ? `calc(${d} * ${(k - 1).toFixed(2)})` : undefined };
}

/** A dial's diameter in a tile: room for its value, as the recorder's RingDiameter. */
const ringDiameter = (item: Item, m: Metrics) =>
  Math.max(m.valueLine * item.scale * 2.3, (valueWidth(item, m) ?? (item.value.length + item.unit.length * 0.62) * m.valuePx * item.scale * 0.58) + m.px * 1.6);

function Icon({ id, size, color, glow, outline }: { id: IconId; size: number; color: string; glow: number; outline: boolean }) {
  const stroke = Math.max(1.2, size / 12) * (24 / size);
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden style={{ overflow: 'visible', filter: glow ? `drop-shadow(0 0 ${glow * 0.3}px ${color})` : undefined }}>
      {outline && <path d={ICON_PATHS[id]} stroke="rgba(0,0,0,0.8)" strokeWidth={stroke + 3} strokeLinecap="round" strokeLinejoin="round" />}
      <path d={ICON_PATHS[id]} stroke={color} strokeWidth={stroke} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** A dial: ring, arc, speedometer or status light. */
function Dial({ size, thickness, item, style, m, fill, inline }: { size: number; thickness: number; item: Item; style: OverlayStyle; m: Metrics; fill?: boolean; inline?: boolean }) {
  if (item.visual === 'status') return <StatusLight size={inline ? size * 0.62 : size * 0.3} item={item} style={style} m={m} />;
  if (item.visual === 'arc') return <ArcDial size={size} thickness={thickness} item={item} style={style} m={m} fill={fill} start={145} sweep={250} />;
  if (item.visual === 'speed') return <Speedometer size={size} thickness={thickness} item={item} style={style} m={m} fill={fill} />;
  return <Ring size={size} thickness={thickness} item={item} style={style} m={m} fill={fill} />;
}

/**
 * A gauge's details for a character playing on it (characters/gauges.ts reads them): its reading, kind, fill (0-1),
 * size and stroke in its own units, and where its fill starts and how far it can go (degrees clockwise from the right).
 */
const gaugeData = (item: Item, size: number, thickness: number, start: number, sweep: number) => ({
  'data-gauge': item.visual,
  'data-source': item.source,
  'data-fill': String(item.fraction ?? 0),
  'data-size': String(size),
  'data-thickness': String(thickness),
  'data-start': String(start),
  'data-sweep': String(sweep),
});

/** An arc of `sweep` degrees from `start` (clockwise from the right, as the recorder draws it), filled to the value. */
function arcPath(c: number, r: number, start: number, sweep: number) {
  const point = (deg: number) => [c + r * Math.cos((deg * Math.PI) / 180), c + r * Math.sin((deg * Math.PI) / 180)] as const;
  const [x1, y1] = point(start);
  const [x2, y2] = point(start + sweep);
  return `M${x1.toFixed(2)} ${y1.toFixed(2)} A${r} ${r} 0 ${sweep > 180 ? 1 : 0} 1 ${x2.toFixed(2)} ${y2.toFixed(2)}`;
}

function ArcDial({ size, thickness, item, style, m, fill, start, sweep }: { size: number; thickness: number; item: Item; style: OverlayStyle; m: Metrics; fill?: boolean; start: number; sweep: number }) {
  const t = (thickness * style.barHeight) / 100;
  const r = Math.max(0.5, (size - t) / 2);
  const filled = sweep * (item.fraction ?? 0);
  return (
    <svg width={fill ? '100%' : size} height={fill ? '100%' : size} viewBox={`0 0 ${size} ${size}`} aria-hidden data-dial={item.visual} {...gaugeData(item, size, t, start, sweep)} style={{ overflow: 'visible', display: 'block' }}>
      <path d={arcPath(size / 2, r, start, sweep)} fill="none" stroke={item.track} strokeWidth={t} strokeLinecap="round" />
      {filled > 0.5 && (
        <path
          d={arcPath(size / 2, r, start, filled)}
          fill="none"
          stroke={item.visualColor}
          strokeWidth={t}
          strokeLinecap="round"
          style={{ filter: style.glow > 0 ? `drop-shadow(0 0 ${m.glowPx * 0.4}px ${item.visualColor})` : undefined }}
        />
      )}
    </svg>
  );
}

/** A speedometer: a 220-degree scale with ticks and a needle at the value. */
function Speedometer({ size, thickness, item, style, m, fill }: { size: number; thickness: number; item: Item; style: OverlayStyle; m: Metrics; fill?: boolean }) {
  const start = 160;
  const sweep = 220;
  const t = (thickness * 0.6 * style.barHeight) / 100;
  const c = size / 2;
  const radius = c - t;
  const at = (deg: number, k: number) => [c + Math.cos((deg * Math.PI) / 180) * radius * k, c + Math.sin((deg * Math.PI) / 180) * radius * k] as const;
  const needle = at(start + sweep * (item.fraction ?? 0), 0.72);
  return (
    <span className="relative block" style={{ width: fill ? '100%' : size, height: fill ? '100%' : size }}>
      <span className="absolute inset-0">
        <ArcDial size={size} thickness={thickness * 0.6} item={item} style={style} m={m} fill start={start} sweep={sweep} />
      </span>
      <svg className="absolute inset-0" width="100%" height="100%" viewBox={`0 0 ${size} ${size}`} aria-hidden style={{ overflow: 'visible' }}>
        {[0, 1, 2, 3, 4, 5].map((i) => {
          const [x1, y1] = at(start + (sweep * i) / 5, 0.78);
          const [x2, y2] = at(start + (sweep * i) / 5, 0.9);
          return <line key={i} x1={x1} y1={y1} x2={x2} y2={y2} stroke={rgba(style.labelColor, 60)} strokeWidth={Math.max(1, m.s)} />;
        })}
        <line x1={c} y1={c} x2={needle[0]} y2={needle[1]} stroke={item.visualColor} strokeWidth={Math.max(1.5, size * 0.035)} strokeLinecap="round" />
        <circle cx={c} cy={c} r={Math.max(2, size * 0.07)} fill={item.visualColor} />
      </svg>
    </span>
  );
}

/** A status light: OK, warning or critical, with a soft halo. */
function StatusLight({ size, item, style, m }: { size: number; item: Item; style: OverlayStyle; m: Metrics }) {
  return (
    <span
      aria-hidden
      data-status
      className="inline-block rounded-full"
      style={{
        width: size,
        height: size,
        background: item.statusColor,
        boxShadow: `0 0 0 ${size * 0.3}px ${rgba(item.statusColor, 24)}${style.glow > 0 ? `, 0 0 ${m.glowPx * 0.6}px ${item.statusColor}` : ''}`,
      }}
    />
  );
}

/** A bar or a thermometer under the value. */
function Under({ item, style, m, gap }: { item: Item; style: OverlayStyle; m: Metrics; gap: number }) {
  if (item.visual !== 'thermo') return <Bar item={item} style={style} m={m} gap={gap} />;
  const h = Math.max(m.barH * 1.9, 6 * m.s);
  const tube = Math.max(2, h * 0.46);
  const fraction = item.fraction ?? 0;
  return (
    <span className="relative block" style={{ height: h, marginTop: gap }} data-thermo>
      <span className="absolute rounded-full" style={{ left: h * 0.5, right: 0, top: (h - tube) / 2, height: tube, background: item.track }} />
      {fraction > 0 && <span className="absolute rounded-full" style={{ left: h * 0.5, top: (h - tube) / 2, height: tube, width: `max(${tube}px, calc((100% - ${h * 0.5}px) * ${fraction}))`, background: item.visualColor }} />}
      <span className="absolute left-0 top-0 rounded-full" style={{ width: h, height: h, background: item.visualColor }} />
    </span>
  );
}

/** A chart of the recent history: an area, a line or columns. */
function Chart(props: { width: number | string; height: number | string; minHeight?: number; item: Item; points: Points; style: OverlayStyle; m: Metrics }) {
  if (props.item.visual === 'columns') return <Columns {...props} />;
  return <Graph {...props} area={props.item.visual === 'graph'} />;
}

/** The recent history as columns (the highest of each slice of the window). */
function Columns({ width, height: requested, minHeight, item, points, style, m }: { width: number | string; height: number | string; minHeight?: number; item: Item; points: Points; style: OverlayStyle; m: Metrics }) {
  const w = typeof width === 'number' ? width : 100;
  const height = typeof requested === 'number' ? requested : (minHeight ?? 40);
  const window = windowOf(points, style.graphSeconds);
  const count = Math.max(6, Math.min(40, Math.floor(w / (4 * m.s))));
  let bars: { x: number; h: number }[] = [];
  if (window.length > 0) {
    const values = window.map(([, v]) => v);
    let lo: number;
    let hi: number;
    if (item.max != null) [lo, hi] = [0, item.max];
    else {
      [lo, hi] = [Math.min(...values), Math.max(...values)];
      const span = Math.max(hi - lo, Math.max(Math.abs(hi) * 0.05, 1));
      const middle = (hi + lo) / 2;
      [lo, hi] = [middle - span * 0.6, middle + span * 0.6];
    }
    const end = window[window.length - 1]![0];
    const start = end - style.graphSeconds * 1000;
    const slice = (style.graphSeconds * 1000) / count;
    bars = Array.from({ length: count }, (_, i) => {
      const inSlice = window.filter(([t]) => t >= start + i * slice && t < start + (i + 1) * slice).map(([, v]) => v);
      return inSlice.length ? { x: i, h: Math.max(1, Math.max(0, Math.min(1, (Math.max(...inSlice) - lo) / (hi - lo))) * height) } : { x: i, h: 0 };
    });
  }
  const cell = w / count;
  return (
    <svg width={width} height={requested} viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" aria-hidden data-graph style={{ overflow: 'visible', display: 'block', minHeight }}>
      <line x1={0} y1={height} x2={w} y2={height} stroke={item.track} strokeWidth={Math.max(1, m.s)} vectorEffect="non-scaling-stroke" />
      {bars.filter((b) => b.h > 0).map((b) => (
        <rect key={b.x} x={b.x * cell} y={height - b.h} width={Math.max(0.5, cell - Math.max(1, m.s * 0.8))} height={b.h} fill={item.visualColor} />
      ))}
    </svg>
  );
}

function Bar({ item, style, m, gap }: { item: Item; style: OverlayStyle; m: Metrics; gap: number }) {
  const fraction = item.fraction ?? 0;
  const track = item.track;
  // Its details for a character pushing it (characters/gauges.ts reads them).
  const marks = { 'data-gauge': 'bar', 'data-source': item.source, 'data-fill': String(fraction) };
  if (style.barStyle === 'segments') {
    return <Segments fraction={fraction} color={item.visualColor} track={track} height={m.barH} gap={gap} marks={marks} />;
  }
  const radius = style.barStyle === 'rounded' ? m.barH / 2 : 0;
  return (
    <span className="block" style={{ height: m.barH, background: track, marginTop: gap, borderRadius: radius }} data-bar {...marks}>
      {fraction > 0 && (
        <span
          className="block h-full"
          style={{
            width: style.barStyle === 'rounded' ? `max(${m.barH}px, ${fraction * 100}%)` : `${fraction * 100}%`,
            background: item.visualColor,
            borderRadius: radius,
            boxShadow: style.glow > 0 ? `0 0 ${m.glowPx * 0.6}px ${rgba(item.visualColor, style.glow * 0.6)}` : undefined,
          }}
        />
      )}
    </span>
  );
}

/** A bar of lit and unlit segments (the count follows the width, as the recorder's). */
function Segments({ fraction, color, track, height, gap, marks }: { fraction: number; color: string; track: string; height: number; gap: number; marks?: Record<string, string> }) {
  // The width is not known here: 12 segments, close to the recorder's count for a typical panel.
  const count = 12;
  const lit = Math.round(fraction * count);
  return (
    <span className="flex" style={{ height, marginTop: gap, gap: Math.max(1, height * 0.4) }} data-bar {...marks}>
      {Array.from({ length: count }, (_, i) => (
        <span key={i} className="flex-1" style={{ background: i < lit ? color : track }} />
      ))}
    </span>
  );
}

function Ring({ size, thickness, item, style, m, fill }: { size: number; thickness: number; item: Item; style: OverlayStyle; m: Metrics; fill?: boolean }) {
  const t = (thickness * style.barHeight) / 100;
  const r = Math.max(0.5, (size - t) / 2);
  const circumference = 2 * Math.PI * r;
  const fraction = item.fraction ?? 0;
  return (
    <svg width={fill ? '100%' : size} height={fill ? '100%' : size} viewBox={`0 0 ${size} ${size}`} aria-hidden data-ring {...gaugeData(item, size, t, -90, 360)} style={{ overflow: 'visible', display: 'block' }}>
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={item.track} strokeWidth={t} />
      {fraction > 0.001 && (
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          stroke={item.visualColor}
          strokeWidth={t}
          strokeLinecap="round"
          strokeDasharray={`${fraction * circumference} ${circumference}`}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
          style={{ filter: style.glow > 0 ? `drop-shadow(0 0 ${m.glowPx * 0.4}px ${item.visualColor})` : undefined }}
        />
      )}
    </svg>
  );
}

/** The reading's recent history: an area under a line, ending in a dot; percentages from 0 to their full scale. */
function Graph({
  width,
  height: requested,
  minHeight,
  item,
  points,
  style,
  m,
  area: filled = true,
}: {
  width: number | string;
  height: number | string;
  minHeight?: number;
  item: Item;
  points: readonly (readonly [number, number])[] | undefined;
  style: OverlayStyle;
  m: Metrics;
  /** Filled under the line (an area graph), or the line only. */
  area?: boolean;
}) {
  const w = typeof width === 'number' ? width : 100;
  // Drawn in its own units; a height in percent stretches it (the line keeps its width).
  const height = typeof requested === 'number' ? requested : (minHeight ?? 40);
  const line = m.graphLine;
  const end = points && points.length ? points[points.length - 1]![0] : 0;
  const start = end - style.graphSeconds * 1000;
  const window = (points ?? []).filter(([t, v]) => t >= start && Number.isFinite(v));
  let path = '';
  // Its line for a character surfing it (characters/gauges.ts reads it), in the drawing's own units.
  let surf = '';
  let area = '';
  let last: [number, number] | null = null;
  if (window.length >= 2) {
    let lo: number;
    let hi: number;
    if (item.max != null) [lo, hi] = [0, item.max];
    else {
      [lo, hi] = [Math.min(...window.map((p) => p[1])), Math.max(...window.map((p) => p[1]))];
      const span = Math.max(hi - lo, Math.max(Math.abs(hi) * 0.05, 1));
      const middle = (hi + lo) / 2;
      [lo, hi] = [middle - span * 0.6, middle + span * 0.6];
    }
    const xy = window.map(([t, v]) => [((t - start) / (style.graphSeconds * 1000)) * w, height - line - Math.max(0, Math.min(1, (v - lo) / (hi - lo))) * (height - 2 * line)] as [number, number]);
    path = xy.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(2)} ${y.toFixed(2)}`).join(' ');
    surf = xy.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
    area = `M${xy[0]![0].toFixed(2)} ${height} ${xy.map(([x, y]) => `L${x.toFixed(2)} ${y.toFixed(2)}`).join(' ')} L${xy[xy.length - 1]![0].toFixed(2)} ${height} Z`;
    last = xy[xy.length - 1]!;
  }
  const id = `g${item.key.replace(/[^a-z0-9]/gi, '')}`;
  return (
    <svg
      width={width}
      height={requested}
      viewBox={`0 0 ${w} ${height}`}
      preserveAspectRatio="none"
      aria-hidden
      data-graph
      data-gauge="graph"
      data-source={item.source}
      data-line={surf}
      data-units={`${w} ${height}`}
      style={{ overflow: 'visible', display: 'block', minHeight }}
    >
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={item.visualColor} stopOpacity={item.fillOpacity} />
          <stop offset="1" stopColor={item.visualColor} stopOpacity={Math.min(0.02, item.fillOpacity)} />
        </linearGradient>
      </defs>
      <line x1={0} y1={height} x2={w} y2={height} stroke={item.track} strokeWidth={Math.max(1, m.s)} vectorEffect="non-scaling-stroke" />
      {filled && area && <path d={area} fill={`url(#${id})`} />}
      {path && (
        <path
          d={path}
          fill="none"
          stroke={item.visualColor}
          strokeWidth={line}
          strokeLinejoin="round"
          strokeLinecap="round"
          vectorEffect="non-scaling-stroke"
          style={{ filter: style.glow > 0 ? `drop-shadow(0 0 ${m.glowPx * 0.3}px ${item.visualColor})` : undefined }}
        />
      )}
      {last && typeof width === 'number' && <circle cx={last[0]} cy={last[1]} r={line * 1.5} fill={item.visualColor} />}
    </svg>
  );
}
