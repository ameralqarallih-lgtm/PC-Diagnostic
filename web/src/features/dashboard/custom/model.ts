import {
  AlignLeft,
  AreaChart,
  BarChart3,
  BellRing,
  Gauge,
  Grid3x3,
  Hash,
  LineChart,
  ListOrdered,
  SlidersHorizontal,
  Table2,
  type LucideIcon,
} from 'lucide-react';
import type { WidgetSettings } from '../config/types';
import { flag, list, text } from '../config/widgets';

/**
 * A widget the user builds: a design (how it looks) fed by value sources (what it shows). Everything is kept in the
 * widget's settings as plain values, so the dashboard document stays simple and is checked by the web host like any
 * other widget's settings.
 */
export const DESIGNS = ['value', 'gauge', 'bars', 'line', 'area', 'columns', 'table', 'tiles', 'processes', 'events', 'note'] as const;
export type CustomDesign = (typeof DESIGNS)[number];

export interface DesignInfo {
  id: CustomDesign;
  name: string;
  description: string;
  icon: LucideIcon;
  /** How many value sources it takes (0: none; it has its own data). */
  maxSources: number;
  /** Over time (a chart window) rather than one value per source. */
  overTime: boolean;
  /** Starting size on the 12-column grid. */
  size: { w: number; h: number };
}

export const DESIGN_INFO: Record<CustomDesign, DesignInfo> = {
  value: { id: 'value', name: 'Number', description: 'Big numbers with their trend: the current value, or the average, lowest or highest over the window.', icon: Hash, maxSources: 4, overTime: false, size: { w: 3, h: 4 } },
  gauge: { id: 'gauge', name: 'Dials', description: 'Dials (arc, ring, speedometer or thermometer) that change color past your limits.', icon: Gauge, maxSources: 8, overTime: false, size: { w: 6, h: 5 } },
  bars: { id: 'bars', name: 'Bars', description: 'One meter bar per value, against its scale and limits.', icon: SlidersHorizontal, maxSources: 16, overTime: false, size: { w: 4, h: 6 } },
  line: { id: 'line', name: 'Line chart', description: 'Values over time, up to an hour, with hover values and limit lines.', icon: LineChart, maxSources: 8, overTime: true, size: { w: 6, h: 8 } },
  area: { id: 'area', name: 'Area chart', description: 'Filled lines over time; stack them to see a total made of parts.', icon: AreaChart, maxSources: 8, overTime: true, size: { w: 6, h: 8 } },
  columns: { id: 'columns', name: 'Column chart', description: 'Compare values side by side, e.g. every drive or every fan.', icon: BarChart3, maxSources: 16, overTime: false, size: { w: 6, h: 7 } },
  table: { id: 'table', name: 'Table', description: 'Current, lowest, average and highest of each value over the window, with a trend line.', icon: Table2, maxSources: 30, overTime: false, size: { w: 6, h: 7 } },
  tiles: { id: 'tiles', name: 'Status tiles', description: 'A colored tile per value: green, amber or red against your limits.', icon: Grid3x3, maxSources: 16, overTime: false, size: { w: 4, h: 5 } },
  processes: { id: 'processes', name: 'Process list', description: 'The busiest processes by the measure you choose, optionally only those matching a name.', icon: ListOrdered, maxSources: 0, overTime: false, size: { w: 4, h: 8 } },
  events: { id: 'events', name: 'Event feed', description: 'Live events from the sources you choose, at the severity you choose, optionally matching words.', icon: BellRing, maxSources: 0, overTime: false, size: { w: 4, h: 8 } },
  note: { id: 'note', name: 'Text note', description: 'A heading or a note on the dashboard, e.g. what to watch for.', icon: AlignLeft, maxSources: 0, overTime: false, size: { w: 4, h: 3 } },
};

export type Aggregate = 'latest' | 'avg' | 'min' | 'max';
export type Direction = 'up' | 'down';
export type DialStyle = 'arc' | 'ring' | 'speed' | 'thermo';
export type TextSize = 's' | 'm' | 'l';

export const WINDOWS = [
  { value: '1', label: '1 minute' },
  { value: '5', label: '5 minutes' },
  { value: '10', label: '10 minutes' },
  { value: '30', label: '30 minutes' },
  { value: '60', label: '1 hour' },
];

export const AGGREGATES: { value: Aggregate; label: string }[] = [
  { value: 'latest', label: 'Current value' },
  { value: 'avg', label: 'Average over the window' },
  { value: 'min', label: 'Lowest in the window' },
  { value: 'max', label: 'Highest in the window' },
];

export const PROCESS_MEASURES = [
  { value: 'cpu', label: 'CPU' },
  { value: 'memory', label: 'Memory' },
  { value: 'gpu', label: 'GPU' },
  { value: 'io', label: 'Disk and network I/O' },
  { value: 'vram', label: 'GPU memory' },
  { value: 'network', label: 'Network' },
];

/** Line, area and column colors, and the swatches offered for each value (distinct in both themes). */
export const PALETTES: Record<string, { name: string; colors: string[] }> = {
  default: { name: 'Default', colors: ['#38bdf8', '#a78bfa', '#34d399', '#fbbf24', '#f472b6', '#fb923c', '#60a5fa', '#2dd4bf', '#f87171', '#a3e635'] },
  cool: { name: 'Cool', colors: ['#38bdf8', '#22d3ee', '#2dd4bf', '#60a5fa', '#818cf8', '#a78bfa', '#34d399', '#93c5fd'] },
  warm: { name: 'Warm', colors: ['#fb923c', '#f87171', '#fbbf24', '#f472b6', '#facc15', '#ef4444', '#fdba74', '#e879f9'] },
  vivid: { name: 'Vivid', colors: ['#ef4444', '#3b82f6', '#22c55e', '#eab308', '#a855f7', '#ec4899', '#14b8a6', '#f97316'] },
  soft: { name: 'Soft', colors: ['#93c5fd', '#c4b5fd', '#86efac', '#fde68a', '#f9a8d4', '#fdba74', '#67e8f9', '#d9f99d'] },
};

/** Accent colors for any widget (its buttons, bars and highlights). */
export const ACCENTS = ['#38bdf8', '#34d399', '#a78bfa', '#f472b6', '#fb923c', '#fbbf24', '#f87171', '#94a3b8'];

/** The settings of a custom widget, read from its stored values (anything missing gets its default). */
export interface CustomConfig {
  design: CustomDesign;
  sources: string[];
  /** Per source, a label replacing the source's own ('' keeps it). */
  labels: string[];
  /** Per source, a color ('' takes the palette's). */
  colors: string[];
  window: number;
  aggregate: Aggregate;
  /** Scale; null is automatic (0–100 for percentages). */
  min: number | null;
  max: number | null;
  warning: number | null;
  critical: number | null;
  /** Up: higher values are worse (temperatures, load). Down: lower values are worse (free space, fan speed). */
  direction: Direction;
  /** Decimals; null is automatic for the unit. */
  decimals: number | null;
  size: TextSize;
  palette: string;
  legend: boolean;
  spark: boolean;
  smooth: boolean;
  stacked: boolean;
  dial: DialStyle;
  /** Process list. */
  measure: string;
  count: number;
  match: string;
  showBars: boolean;
  /** Event feed. */
  minimum: string;
  eventSources: string[];
  /** Text note. */
  text: string;
  align: 'left' | 'center';
}

function number(settings: WidgetSettings, key: string): number | null {
  const value = settings[key];
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
  return null;
}

function oneOf<T extends string>(value: string, options: readonly T[], fallback: T): T {
  return (options as readonly string[]).includes(value) ? (value as T) : fallback;
}

export function readCustom(settings: WidgetSettings): CustomConfig {
  return {
    design: oneOf(text(settings, 'design', 'value'), DESIGNS, 'value'),
    sources: list(settings, 'sources'),
    labels: list(settings, 'labels'),
    colors: list(settings, 'colors'),
    window: Number(oneOf(text(settings, 'window', '5'), WINDOWS.map((w) => w.value), '5')),
    aggregate: oneOf(text(settings, 'aggregate', 'latest'), ['latest', 'avg', 'min', 'max'] as const, 'latest'),
    min: number(settings, 'min'),
    max: number(settings, 'max'),
    warning: number(settings, 'warning'),
    critical: number(settings, 'critical'),
    direction: oneOf(text(settings, 'direction', 'up'), ['up', 'down'] as const, 'up'),
    decimals: number(settings, 'decimals'),
    size: oneOf(text(settings, 'size', 'm'), ['s', 'm', 'l'] as const, 'm'),
    palette: text(settings, 'palette', 'default') in PALETTES ? text(settings, 'palette', 'default') : 'default',
    legend: flag(settings, 'legend', true),
    spark: flag(settings, 'spark', true),
    smooth: flag(settings, 'smooth', true),
    stacked: flag(settings, 'stacked', false),
    dial: oneOf(text(settings, 'dial', 'arc'), ['arc', 'ring', 'speed', 'thermo'] as const, 'arc'),
    measure: oneOf(text(settings, 'measure', 'cpu'), PROCESS_MEASURES.map((m) => m.value), 'cpu'),
    count: Math.min(30, Math.max(1, number(settings, 'count') ?? 8)),
    match: text(settings, 'match', ''),
    showBars: flag(settings, 'showBars', true),
    minimum: oneOf(text(settings, 'minimum', '0'), ['0', '2', '3'], '0'),
    eventSources: list(settings, 'eventSources'),
    text: text(settings, 'text', ''),
    align: oneOf(text(settings, 'align', 'left'), ['left', 'center'] as const, 'left'),
  };
}

/** The stored settings of a custom widget (empty values left out to keep the document small). */
export function writeCustom(config: CustomConfig, accent: string): WidgetSettings {
  const info = DESIGN_INFO[config.design];
  const settings: WidgetSettings = { design: config.design };
  const put = (key: string, value: string | number | boolean | string[] | null) => {
    if (value === null || value === '' || (Array.isArray(value) && value.length === 0)) return;
    settings[key] = value;
  };
  if (info.maxSources > 0) {
    const sources = config.sources.slice(0, info.maxSources);
    put('sources', sources);
    // Parallel lists only when something differs from the defaults.
    const labels = sources.map((_, i) => config.labels[i] ?? '');
    if (labels.some(Boolean)) put('labels', labels);
    const colors = sources.map((_, i) => config.colors[i] ?? '');
    if (colors.some(Boolean)) put('colors', colors);
    put('window', String(config.window));
    if (!info.overTime) put('aggregate', config.aggregate);
    put('min', config.min);
    put('max', config.max);
    put('warning', config.warning);
    put('critical', config.critical);
    put('direction', config.direction);
    put('decimals', config.decimals);
    put('size', config.size);
    put('palette', config.palette);
    put('legend', config.legend);
    put('spark', config.spark);
    put('smooth', config.smooth);
    put('stacked', config.stacked);
    if (config.design === 'gauge') put('dial', config.dial);
  }
  if (config.design === 'processes') {
    put('measure', config.measure);
    put('count', config.count);
    put('match', config.match.trim());
    put('showBars', config.showBars);
  }
  if (config.design === 'events') {
    put('minimum', config.minimum);
    put('eventSources', config.eventSources);
    put('match', config.match.trim());
  }
  if (config.design === 'note') {
    put('text', config.text.slice(0, 500));
    put('align', config.align);
    put('size', config.size);
  }
  put('accent', accent);
  return settings;
}

/** A new custom widget's settings for a design. */
export function defaultCustom(design: CustomDesign): CustomConfig {
  const base = readCustom({ design });
  const sources: Record<CustomDesign, string[]> = {
    value: ['cpu'],
    gauge: ['cpu', 'memory', 'gpu'],
    bars: ['cpu', 'memory', 'gpu', 'disk'],
    line: ['cpu', 'gpu'],
    area: ['cpu', 'gpu'],
    columns: ['cpu', 'memory', 'gpu', 'disk'],
    table: ['cpu', 'memory', 'gpu', 'cpu.temp'],
    tiles: ['cpu', 'memory', 'gpu', 'disk'],
    processes: [],
    events: [],
    note: [],
  };
  return {
    ...base,
    sources: sources[design],
    eventSources: ['windows', 'display', 'process', 'service', 'recorder'],
    text: design === 'note' ? 'Watch the GPU temperature while gaming.' : '',
    warning: design === 'tiles' || design === 'bars' || design === 'gauge' ? 75 : null,
    critical: design === 'tiles' || design === 'bars' || design === 'gauge' ? 90 : null,
  };
}

/** The color of the n-th source. */
export function sourceColor(config: CustomConfig, index: number): string {
  const own = config.colors[index];
  if (own) return own;
  const palette = PALETTES[config.palette]?.colors ?? PALETTES.default!.colors;
  return palette[index % palette.length]!;
}

/** A value's level against the widget's limits, in the direction that is worse. */
export function levelFor(value: number | null, config: Pick<CustomConfig, 'warning' | 'critical' | 'direction'>): 'normal' | 'warning' | 'critical' | 'unavailable' {
  if (value == null) return 'unavailable';
  const worse = (limit: number | null) => limit != null && (config.direction === 'up' ? value >= limit : value <= limit);
  if (worse(config.critical)) return 'critical';
  if (worse(config.warning)) return 'warning';
  return 'normal';
}
