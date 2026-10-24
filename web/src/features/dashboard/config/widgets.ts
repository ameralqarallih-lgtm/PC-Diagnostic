import {
  Activity,
  BellRing,
  Camera,
  Cpu,
  Gauge,
  Hash,
  ListOrdered,
  Monitor,
  Network,
  Radar,
  Siren,
  SlidersHorizontal,
  Thermometer,
  Wand2,
  type LucideIcon,
} from 'lucide-react';
import { USER_INCIDENT_TYPES } from '@/lib/api/types';
import type { WidgetSettings, WidgetType } from './types';

/** A setting shown in the widget settings dialog. */
export type SettingField =
  | { key: string; label: string; kind: 'multi'; options: { value: string; label: string }[]; help?: string }
  | { key: string; label: string; kind: 'select'; options: { value: string; label: string }[]; help?: string }
  | { key: string; label: string; kind: 'toggle'; help?: string };

export interface WidgetDefinition {
  type: WidgetType;
  name: string;
  description: string;
  icon: LucideIcon;
  /** Size on the 12-column layout, in columns and rows. */
  size: { w: number; h: number; minW: number; minH: number };
  /** Rows needed when the widget is a single column wide (phones). */
  narrowHeight: number;
  /** Narrowest width, in pixels, at which the widget is still readable; narrower and it moves to its own row. */
  minWidthPx: number;
  defaults: WidgetSettings;
  fields: SettingField[];
}

export const GAUGE_OPTIONS = [
  { value: 'cpu', label: 'CPU' },
  { value: 'memory', label: 'Memory' },
  { value: 'gpu', label: 'GPU' },
  { value: 'vram', label: 'VRAM' },
  { value: 'disk', label: 'Disk' },
  { value: 'network', label: 'Network' },
  { value: 'cpuTemp', label: 'CPU temperature' },
  { value: 'gpuTemp', label: 'GPU temperature' },
];

export const SERIES_OPTIONS = [
  { value: 'cpu', label: 'CPU %' },
  { value: 'memory', label: 'Memory %' },
  { value: 'gpu', label: 'GPU %' },
  { value: 'vram', label: 'VRAM %' },
  { value: 'disk', label: 'Disk active %' },
  { value: 'cpuTemperature', label: 'CPU package °C' },
  { value: 'gpuTemperature', label: 'GPU core °C' },
  { value: 'gpuHotspot', label: 'GPU hotspot °C' },
  { value: 'netReceive', label: 'Download' },
  { value: 'netSend', label: 'Upload' },
];

export const METRIC_OPTIONS = [
  ...SERIES_OPTIONS,
  { value: 'cpuFrequency', label: 'CPU frequency' },
  { value: 'commit', label: 'Memory commit %' },
  { value: 'hardFaults', label: 'Hard page faults/s' },
  { value: 'processCount', label: 'Running processes' },
  { value: 'agentCpu', label: 'Recorder CPU %' },
];

const VIEW_OPTIONS = [
  { value: 'usage', label: 'Utilization' },
  { value: 'temperature', label: 'Temperatures' },
  { value: 'network', label: 'Network' },
];

export const EVENT_SOURCES = [
  { value: 'windows', label: 'Windows events' },
  { value: 'display', label: 'Display changes' },
  { value: 'process', label: 'Process crashes and hangs' },
  { value: 'service', label: 'Service failures' },
  { value: 'recorder', label: 'Recorder (network, other)' },
];

const all = (options: { value: string }[]) => options.map((o) => o.value);

export const WIDGETS: Record<WidgetType, WidgetDefinition> = {
  capture: {
    type: 'capture',
    name: 'Capture',
    description: 'The CAPTURE INCIDENT button, quick types and an optional note.',
    icon: Camera,
    size: { w: 12, h: 5, minW: 3, minH: 3 },
    narrowHeight: 11,
    minWidthPx: 340,
    defaults: { quickTypes: USER_INCIDENT_TYPES.map((t) => t.id), note: true },
    fields: [
      { key: 'quickTypes', label: 'Quick types shown', kind: 'multi', options: USER_INCIDENT_TYPES.map((t) => ({ value: t.id, label: t.label })) },
      { key: 'note', label: 'Note field', kind: 'toggle' },
    ],
  },
  gauges: {
    type: 'gauges',
    name: 'Gauges',
    description: 'Animated dials that turn amber and red as values climb. Click one to focus it in the charts.',
    icon: Gauge,
    size: { w: 12, h: 4, minW: 3, minH: 4 },
    narrowHeight: 15,
    minWidthPx: 300,
    defaults: { metrics: all(GAUGE_OPTIONS) },
    fields: [{ key: 'metrics', label: 'Dials shown', kind: 'multi', options: GAUGE_OPTIONS }],
  },
  chart: {
    type: 'chart',
    name: 'Live chart',
    description: 'Streaming lines with zoom, hover values, incident markers and pulsing alerts.',
    icon: Activity,
    size: { w: 8, h: 10, minW: 3, minH: 6 },
    narrowHeight: 10,
    minWidthPx: 360,
    defaults: { view: 'usage', series: all(SERIES_OPTIONS), window: '5', markers: true },
    fields: [
      { key: 'view', label: 'Starts on', kind: 'select', options: VIEW_OPTIONS },
      { key: 'series', label: 'Lines included', kind: 'multi', options: SERIES_OPTIONS, help: 'Each view shows the included lines that match its unit.' },
      { key: 'window', label: 'Time window', kind: 'select', options: [{ value: '1', label: '1 minute' }, { value: '5', label: '5 minutes' }, { value: '10', label: '10 minutes' }] },
      { key: 'markers', label: 'Incident and event markers', kind: 'toggle' },
    ],
  },
  metric: {
    type: 'metric',
    name: 'Single value',
    description: 'One number, large, with its recent trend.',
    icon: Hash,
    size: { w: 3, h: 4, minW: 2, minH: 3 },
    narrowHeight: 4,
    minWidthPx: 170,
    defaults: { metric: 'cpu' },
    fields: [{ key: 'metric', label: 'Value', kind: 'select', options: METRIC_OPTIONS }],
  },
  recorder: {
    type: 'recorder',
    name: 'Recorder controls',
    description: 'Start, stop and pause recording; the incident being recorded.',
    icon: SlidersHorizontal,
    size: { w: 4, h: 10, minW: 3, minH: 6 },
    narrowHeight: 9,
    minWidthPx: 300,
    defaults: { overhead: true },
    fields: [{ key: 'overhead', label: "Show the recorder's own CPU and memory", kind: 'toggle' }],
  },
  processes: {
    type: 'processes',
    name: 'Top processes',
    description: 'The busiest processes by CPU, memory, GPU or disk, with live bars.',
    icon: ListOrdered,
    size: { w: 8, h: 10, minW: 3, minH: 6 },
    narrowHeight: 11,
    minWidthPx: 340,
    defaults: { tab: 'cpu', rows: '8', bars: true, filter: true },
    fields: [
      { key: 'tab', label: 'Sorted by', kind: 'select', options: [{ value: 'cpu', label: 'CPU' }, { value: 'memory', label: 'Memory' }, { value: 'gpu', label: 'GPU' }, { value: 'io', label: 'Disk I/O' }, { value: 'network', label: 'Network (Enhanced mode)' }] },
      { key: 'rows', label: 'Rows', kind: 'select', options: [{ value: '3', label: '3' }, { value: '5', label: '5' }, { value: '8', label: '8' }] },
      { key: 'bars', label: 'Usage bars', kind: 'toggle' },
      { key: 'filter', label: 'Filter box', kind: 'toggle' },
    ],
  },
  incidents: {
    type: 'incidents',
    name: 'Incidents',
    description: 'The incident being recorded and the latest ones.',
    icon: Siren,
    size: { w: 4, h: 10, minW: 3, minH: 5 },
    narrowHeight: 9,
    minWidthPx: 280,
    defaults: { count: '5' },
    fields: [{ key: 'count', label: 'Incidents listed', kind: 'select', options: [{ value: '3', label: '3' }, { value: '5', label: '5' }, { value: '10', label: '10' }] }],
  },
  events: {
    type: 'events',
    name: 'Recent events',
    description: 'Classified Windows events, display changes, crashes and service failures as they happen.',
    icon: BellRing,
    size: { w: 8, h: 8, minW: 3, minH: 5 },
    narrowHeight: 9,
    minWidthPx: 320,
    defaults: { minimum: '0', sources: all(EVENT_SOURCES) },
    fields: [
      { key: 'minimum', label: 'Shows at least', kind: 'select', options: [{ value: '0', label: 'All events' }, { value: '2', label: 'Warnings' }, { value: '3', label: 'Serious problems' }] },
      { key: 'sources', label: 'Sources included', kind: 'multi', options: EVENT_SOURCES },
    ],
  },
  anomalies: {
    type: 'anomalies',
    name: 'Anomalies',
    description: 'What the anomaly rules detected, with their reasoning: the normal range, the current value and the rule. Running ones pulse.',
    icon: Radar,
    size: { w: 4, h: 8, minW: 3, minH: 4 },
    narrowHeight: 8,
    minWidthPx: 260,
    defaults: { running: false, minimum: 'Info' },
    fields: [
      { key: 'running', label: 'Only anomalies still going on', kind: 'toggle' },
      {
        key: 'minimum',
        label: 'Shows at least',
        kind: 'select',
        options: [
          { value: 'Info', label: 'Everything' },
          { value: 'Medium', label: 'Medium severity' },
          { value: 'High', label: 'High severity' },
        ],
      },
    ],
  },
  sensors: {
    type: 'sensors',
    name: 'Sensors',
    description: 'Temperatures of the CPU, every GPU and every drive, and CPU power.',
    icon: Thermometer,
    size: { w: 4, h: 8, minW: 3, minH: 4 },
    narrowHeight: 8,
    minWidthPx: 260,
    defaults: { include: ['cpu', 'gpu', 'disk', 'power'] },
    fields: [
      {
        key: 'include',
        label: 'Sensors included',
        kind: 'multi',
        options: [
          { value: 'cpu', label: 'CPU package' },
          { value: 'gpu', label: 'GPUs (core and hotspot)' },
          { value: 'disk', label: 'Drives' },
          { value: 'power', label: 'CPU package power' },
        ],
      },
    ],
  },
  gpu: {
    type: 'gpu',
    name: 'Graphics',
    description: 'Per-adapter utilization, VRAM, power and clock.',
    icon: Cpu,
    size: { w: 4, h: 8, minW: 3, minH: 4 },
    narrowHeight: 8,
    minWidthPx: 260,
    defaults: { adapters: 'all' },
    fields: [{ key: 'adapters', label: 'Adapters', kind: 'select', options: [{ value: 'all', label: 'All' }, { value: 'busiest', label: 'Busiest only' }] }],
  },
  displays: {
    type: 'displays',
    name: 'Displays',
    description: 'Connected displays with resolution and refresh rate.',
    icon: Monitor,
    size: { w: 4, h: 4, minW: 2, minH: 3 },
    narrowHeight: 4,
    minWidthPx: 240,
    defaults: {},
    fields: [],
  },
  custom: {
    type: 'custom',
    name: 'Custom widget',
    description: 'A widget you build: a design (number, dials, bars, charts, table, tiles, lists or a note) and the values it shows.',
    icon: Wand2,
    size: { w: 4, h: 6, minW: 2, minH: 2 },
    narrowHeight: 6,
    minWidthPx: 200,
    // Its settings come from the widget builder (see custom/model.ts).
    defaults: {},
    fields: [],
  },
  network: {
    type: 'network',
    name: 'Network adapters',
    description: 'Throughput, link speed and Wi-Fi signal per adapter.',
    icon: Network,
    size: { w: 4, h: 6, minW: 3, minH: 4 },
    narrowHeight: 6,
    minWidthPx: 260,
    defaults: { virtual: false, disconnected: false },
    fields: [
      { key: 'virtual', label: 'Include virtual adapters (Hyper-V, WSL, VPN)', kind: 'toggle' },
      { key: 'disconnected', label: 'Include disconnected adapters', kind: 'toggle' },
    ],
  },
};

/** Typed accessors for settings (they are stored as plain JSON values). */
export function list(settings: WidgetSettings, key: string): string[] {
  const value = settings[key];
  return Array.isArray(value) ? value : [];
}

export function text(settings: WidgetSettings, key: string, fallback: string): string {
  const value = settings[key];
  return typeof value === 'string' ? value : typeof value === 'number' ? String(value) : fallback;
}

export function flag(settings: WidgetSettings, key: string, fallback: boolean): boolean {
  const value = settings[key];
  return typeof value === 'boolean' ? value : fallback;
}
