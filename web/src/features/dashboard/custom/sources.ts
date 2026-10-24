import type { LiveMetric, LiveSnapshot, SeriesInfo } from '@/lib/api/types';
import type { LiveSeries, SeriesKey } from '@/features/live/series';

/**
 * Value sources for widgets the user builds. A source is any recorded series (every device reading and every
 * hardware sensor the recorder keeps) or a value of the live feed; most are both, and then the widget shows the
 * live value once a second and fills in the past from the recording.
 *
 * Source ids stay valid across databases and restarts: whole-PC values keep their series id ("cpu", "cpu.temp"),
 * device values use the device's stable key ("gpu:gpu-10de-2684-…:temp", "disk:disk-bfac…:active",
 * "net:net-{GUID}:rx") and hardware sensors their logical key ("sensor:lhm/gpu-nvidia/0/fan/1").
 */
export interface Source {
  id: string;
  label: string;
  unit: string;
  /** Where the picker lists it, e.g. "CPU", "NVIDIA GeForce RTX 4090", "Fan sensors". */
  group: string;
  device: string | null;
  /** The recorded series ("gpu.1.temp", "sensor.12"), if the recorder keeps it. */
  recordedId: string | null;
  /** The live value it reads, if the live feed has it. */
  liveId: string | null;
  /** The device has not been seen for a day (e.g. an adapter that was removed). */
  stale: boolean;
}

const DAY = 24 * 3600_000;

/** Live values that map onto whole-PC recorded series. */
const SYSTEM_LIVE: Record<string, string> = {
  cpu: 'cpu',
  memory: 'memory',
  gpu: 'gpu',
  vram: 'vram',
  disk: 'disk',
  net: 'net',
  'cpu.freq': 'cpu.freq',
  'cpu.temp': 'cpu.temp',
  'cpu.power': 'cpu.power',
  'memory.commit': 'memory.commit',
  'memory.available': 'memory.available',
  'memory.hardfaults': 'memory.hardfaults',
  'agent.cpu': 'agent.cpu',
  'agent.tick': 'agent.tick',
  'agent.ws': 'agent.ws',
};

const GPU_LIVE = ['util', 'vram', 'temp', 'hotspot', 'core', 'power'];
const DISK_LIVE = ['active', 'read', 'write', 'temp'];
const NET_LIVE = ['rx', 'tx', 'wifi'];

/** Live-only values (not recorded as series of their own). */
const LIVE_ONLY: Omit<Source, 'recordedId' | 'stale'>[] = [
  { id: 'memory.used', label: 'Memory in use', unit: 'B', group: 'Memory', device: null, liveId: 'memory.used' },
  { id: 'net.rx', label: 'Download (all adapters)', unit: 'B/s', group: 'Network', device: null, liveId: 'net.rx' },
  { id: 'net.tx', label: 'Upload (all adapters)', unit: 'B/s', group: 'Network', device: null, liveId: 'net.tx' },
  { id: 'processes.count', label: 'Running processes', unit: '', group: 'System', device: null, liveId: 'processes.count' },
  { id: 'agent.heap', label: 'Recorder managed heap', unit: 'B', group: 'Recorder', device: null, liveId: 'agent.heap' },
];

function deviceGroup(info: SeriesInfo): string {
  const kind = info.id.split('.')[0];
  const prefix = kind === 'gpu' ? 'Graphics' : kind === 'disk' ? 'Drive' : 'Network';
  return `${prefix} · ${info.device ?? 'device'}`;
}

/** The source for a recorded series. */
export function sourceFromSeries(info: SeriesInfo, now: number): Source | null {
  const stale = info.lastSeen != null && now - info.lastSeen > DAY;
  if (info.id.startsWith('sensor.')) {
    if (!info.key) return null;
    // A widget stores source ids of at most 120 characters; the few sensors with longer keys (a device path in
    // them) keep their recorded id, which holds while the recording database does.
    const id = info.key.length <= 100 ? `sensor:${info.key}` : info.id;
    return { id, label: info.label, unit: info.unit, group: info.group, device: info.device, recordedId: info.id, liveId: null, stale: false };
  }
  const device = /^(gpu|disk|net)\.\d+\.([a-z0-9]+)$/.exec(info.id);
  if (device) {
    const [, kind, suffix] = device as unknown as [string, 'gpu' | 'disk' | 'net', string];
    const live =
      kind === 'gpu' ? GPU_LIVE.includes(suffix) && info.key ? `gpu:${info.key}:${suffix}` : null : kind === 'disk' ? (DISK_LIVE.includes(suffix) && info.device ? `disk:@${info.device}:${suffix}` : null) : NET_LIVE.includes(suffix) && info.device ? `net:@${info.device}:${suffix}` : null;
    return {
      id: `${kind}:${info.key ?? info.id}:${suffix}`,
      label: info.label,
      unit: info.unit,
      group: deviceGroup(info),
      device: info.device,
      recordedId: info.id,
      liveId: live,
      stale,
    };
  }
  return { id: info.id, label: info.label, unit: info.unit, group: info.group, device: null, recordedId: info.id, liveId: SYSTEM_LIVE[info.id] ?? null, stale: false };
}

/**
 * Every source: the recorded catalog (with sensors), plus what only the live feed has, plus live devices the
 * recording has not seen yet (nothing recorded on a new install).
 */
export function buildSources(catalog: SeriesInfo[], live: LiveSnapshot | null, now: number): Source[] {
  const sources = new Map<string, Source>();
  for (const info of catalog) {
    const source = sourceFromSeries(info, now);
    if (source && !sources.has(source.id)) sources.set(source.id, source);
  }
  const add = (source: Source) => {
    if (!sources.has(source.id)) sources.set(source.id, source);
  };
  for (const [id, liveId] of Object.entries(SYSTEM_LIVE)) {
    add({ id, label: SYSTEM_LABELS[id] ?? id, unit: SYSTEM_UNITS[id] ?? '', group: SYSTEM_GROUPS[id] ?? 'System', device: null, recordedId: null, liveId, stale: false });
  }
  for (const source of LIVE_ONLY) add({ ...source, recordedId: null, stale: false });

  const liveIds = new Set([...sources.values()].map((s) => s.liveId).filter(Boolean));
  for (const gpu of live?.gpus ?? []) {
    for (const [suffix, label, unit] of [['util', 'utilization', '%'], ['vram', 'VRAM used', 'B'], ['temp', 'temperature', '°C'], ['hotspot', 'hotspot', '°C'], ['core', 'core clock', 'MHz'], ['power', 'power', 'W']] as const) {
      const liveId = `gpu:${gpu.key}:${suffix}`;
      if (!liveIds.has(liveId)) add({ id: liveId, label: `${gpu.name} ${label}`, unit, group: `Graphics · ${gpu.name}`, device: gpu.name, recordedId: null, liveId, stale: false });
    }
    const vramPct = `gpu:${gpu.key}:vrampct`;
    add({ id: vramPct, label: `${gpu.name} VRAM in use`, unit: '%', group: `Graphics · ${gpu.name}`, device: gpu.name, recordedId: null, liveId: vramPct, stale: false });
  }
  for (const disk of live?.disks ?? []) {
    for (const [suffix, label, unit] of [['active', 'active time', '%'], ['read', 'read', 'B/s'], ['write', 'write', 'B/s'], ['temp', 'temperature', '°C']] as const) {
      const liveId = `disk:@${disk.name}:${suffix}`;
      if (!liveIds.has(liveId)) add({ id: liveId, label: `${disk.name} ${label}`, unit, group: `Drive · ${disk.name}`, device: disk.name, recordedId: null, liveId, stale: false });
    }
  }
  for (const net of live?.networks ?? []) {
    for (const [suffix, label, unit] of [['rx', 'download', 'B/s'], ['tx', 'upload', 'B/s'], ['wifi', 'Wi-Fi signal', '%']] as const) {
      const liveId = `net:@${net.name}:${suffix}`;
      if (!liveIds.has(liveId)) add({ id: liveId, label: `${net.name} ${label}`, unit, group: `Network · ${net.name}`, device: net.name, recordedId: null, liveId, stale: false });
    }
    const link = `net:@${net.name}:link`;
    add({ id: link, label: `${net.name} link speed`, unit: 'bit/s', group: `Network · ${net.name}`, device: net.name, recordedId: null, liveId: link, stale: false });
  }
  return [...sources.values()];
}

const SYSTEM_LABELS: Record<string, string> = {
  cpu: 'CPU',
  memory: 'Memory in use',
  gpu: 'GPU (busiest)',
  vram: 'VRAM (fullest GPU)',
  disk: 'Disk active (busiest)',
  net: 'Network (all adapters)',
  'cpu.freq': 'CPU frequency',
  'cpu.temp': 'CPU package temperature',
  'cpu.power': 'CPU package power',
  'memory.commit': 'Memory commit',
  'memory.available': 'Memory available',
  'memory.hardfaults': 'Hard page faults',
  'agent.cpu': 'Recorder CPU',
  'agent.tick': 'Recorder sampling time',
  'agent.ws': 'Recorder working set',
};

const SYSTEM_UNITS: Record<string, string> = {
  cpu: '%',
  memory: '%',
  gpu: '%',
  vram: '%',
  disk: '%',
  net: 'B/s',
  'cpu.freq': 'MHz',
  'cpu.temp': '°C',
  'cpu.power': 'W',
  'memory.commit': '%',
  'memory.available': 'B',
  'memory.hardfaults': '/s',
  'agent.cpu': '%',
  'agent.tick': 'ms',
  'agent.ws': 'B',
};

const SYSTEM_GROUPS: Record<string, string> = {
  cpu: 'System',
  memory: 'System',
  gpu: 'System',
  vram: 'System',
  disk: 'System',
  net: 'System',
  'cpu.freq': 'CPU',
  'cpu.temp': 'CPU',
  'cpu.power': 'CPU',
  'memory.commit': 'Memory',
  'memory.available': 'Memory',
  'memory.hardfaults': 'Memory',
  'agent.cpu': 'Recorder',
  'agent.tick': 'Recorder',
  'agent.ws': 'Recorder',
};

/** A placeholder for a source this PC does not have (any more), so a widget still shows what it was set to. */
export function missingSource(id: string): Source {
  return { id, label: id.startsWith('sensor:') ? id.slice(7) : id, unit: '', group: 'Not found', device: null, recordedId: null, liveId: null, stale: true };
}

// The live values, by live id.

type Flat = Map<string, { value: number | null; reason: string | null }>;

function put(flat: Flat, id: string, metric: LiveMetric | null | undefined) {
  flat.set(id, { value: metric?.value ?? null, reason: metric?.value == null ? (metric?.unavailable ?? null) : null });
}

function maxOf(values: (number | null | undefined)[]): number | null {
  const present = values.filter((v): v is number => v != null && Number.isFinite(v));
  return present.length ? Math.max(...present) : null;
}

/** Every value of a live snapshot, by live id. */
export function flattenLive(live: LiveSnapshot): Flat {
  const flat: Flat = new Map();
  const value = (id: string, v: number | null) => flat.set(id, { value: v, reason: null });
  put(flat, 'cpu', live.cpu?.totalPercent);
  put(flat, 'cpu.freq', live.cpu?.frequencyMhz);
  put(flat, 'cpu.temp', live.cpu?.packageTemperatureC);
  put(flat, 'cpu.power', live.cpu?.packagePowerW);
  put(flat, 'memory', live.memory?.usedPercent);
  put(flat, 'memory.used', live.memory?.usedBytes);
  put(flat, 'memory.commit', live.memory?.commitPercent);
  put(flat, 'memory.hardfaults', live.memory?.hardFaultsPerSec);
  const total = live.memory?.totalBytes.value;
  const used = live.memory?.usedBytes.value;
  value('memory.available', total != null && used != null ? total - used : null);

  const vramPct = (g: LiveSnapshot['gpus'][number]) =>
    g.vramUsedBytes.value != null && g.vramTotalBytes.value ? (100 * g.vramUsedBytes.value) / g.vramTotalBytes.value : null;
  value('gpu', maxOf(live.gpus.map((g) => g.utilizationPercent.value)));
  value('vram', maxOf(live.gpus.map(vramPct)));
  for (const g of live.gpus) {
    put(flat, `gpu:${g.key}:util`, g.utilizationPercent);
    put(flat, `gpu:${g.key}:vram`, g.vramUsedBytes);
    put(flat, `gpu:${g.key}:temp`, g.temperatureC);
    put(flat, `gpu:${g.key}:hotspot`, g.hotspotC);
    put(flat, `gpu:${g.key}:core`, g.coreClockMhz);
    put(flat, `gpu:${g.key}:power`, g.powerW);
    value(`gpu:${g.key}:vrampct`, vramPct(g));
  }

  value('disk', maxOf(live.disks.map((d) => d.activePercent.value)));
  for (const d of live.disks) {
    put(flat, `disk:@${d.name}:active`, d.activePercent);
    put(flat, `disk:@${d.name}:read`, d.readBytesPerSec);
    put(flat, `disk:@${d.name}:write`, d.writeBytesPerSec);
    put(flat, `disk:@${d.name}:temp`, d.temperatureC);
  }

  const up = live.networks.filter((n) => n.up);
  const sum = (values: (number | null)[]) => (values.some((v) => v != null) ? values.reduce<number>((t, v) => t + (v ?? 0), 0) : null);
  const rx = sum(up.map((n) => n.receiveBytesPerSec.value));
  const tx = sum(up.map((n) => n.sendBytesPerSec.value));
  value('net.rx', rx);
  value('net.tx', tx);
  value('net', rx == null && tx == null ? null : (rx ?? 0) + (tx ?? 0));
  for (const n of live.networks) {
    put(flat, `net:@${n.name}:rx`, n.receiveBytesPerSec);
    put(flat, `net:@${n.name}:tx`, n.sendBytesPerSec);
    put(flat, `net:@${n.name}:wifi`, n.wifiSignalPercent);
    put(flat, `net:@${n.name}:link`, n.linkBitsPerSec);
  }

  value('processes.count', live.processes?.count ?? null);
  put(flat, 'agent.cpu', live.agent.cpuPercent);
  put(flat, 'agent.ws', live.agent.workingSetBytes);
  put(flat, 'agent.tick', live.agent.tickMs);
  put(flat, 'agent.heap', live.agent.managedHeapBytes);
  return flat;
}

/** The live chart's history (the Agent's trend) for the values it covers, so headline values have a past at once. */
export const TREND_KEYS: Record<string, SeriesKey> = {
  cpu: 'cpu',
  memory: 'memory',
  gpu: 'gpu',
  vram: 'vram',
  disk: 'disk',
  'cpu.temp': 'cpuTemperature',
  'net.rx': 'netReceive',
  'net.tx': 'netSend',
};

// The live buffer: ten minutes of every live value, kept while the page is open (shared by all widgets).

const KEEP_MS = 10 * 60_000;

interface Buffer {
  times: number[];
  values: (number | null)[];
}

const buffers = new Map<string, Buffer>();
const reasons = new Map<string, string | null>();
let lastTime = 0;

/** Adds a live snapshot (ignored unless newer than the last one). */
export function recordLive(live: LiveSnapshot | null | undefined): void {
  const time = live?.timestamp ? Date.parse(live.timestamp) : Number.NaN;
  if (!live || !Number.isFinite(time) || time <= lastTime) return;
  lastTime = time;
  for (const [id, { value, reason }] of flattenLive(live)) {
    let buffer = buffers.get(id);
    if (!buffer) {
      buffer = { times: [], values: [] };
      buffers.set(id, buffer);
    }
    buffer.times.push(time);
    buffer.values.push(value);
    reasons.set(id, reason);
    let drop = 0;
    while (drop < buffer.times.length && buffer.times[drop]! < time - KEEP_MS) drop++;
    if (drop > 0) {
      buffer.times.splice(0, drop);
      buffer.values.splice(0, drop);
    }
  }
}

/** Live points of a value from <paramref name="from"/> to <paramref name="to"/>, oldest first. */
export function livePoints(liveId: string, from: number, to: number): [number, number | null][] {
  const buffer = buffers.get(liveId);
  if (!buffer) return [];
  const points: [number, number | null][] = [];
  for (let i = 0; i < buffer.times.length; i++) {
    const t = buffer.times[i]!;
    if (t >= from && t <= to) points.push([t, buffer.values[i] ?? null]);
  }
  return points;
}

/** Why a live value is missing (an Availability name), if it is. */
export function liveReason(liveId: string): string | null {
  return reasons.get(liveId) ?? null;
}

/** For tests: forgets every live value. */
export function resetLive(): void {
  buffers.clear();
  reasons.clear();
  lastTime = 0;
}

/** Points of the dashboard's live chart history for a source it covers. */
export function trendPoints(series: LiveSeries, sourceId: string, from: number, to: number): [number, number | null][] {
  const key = TREND_KEYS[sourceId];
  if (!key) return [];
  const values = series.values[key];
  const points: [number, number | null][] = [];
  series.times.forEach((t, i) => {
    if (t >= from && t <= to) points.push([t, values[i] ?? null]);
  });
  return points;
}
