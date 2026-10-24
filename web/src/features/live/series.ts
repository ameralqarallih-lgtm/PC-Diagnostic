import type { LiveSnapshot, LiveTrend } from '@/lib/api/types';

/** The headline values charted live (same definitions as the Agent's trend). */
export const SERIES_KEYS = [
  'cpu',
  'memory',
  'gpu',
  'vram',
  'disk',
  'netReceive',
  'netSend',
  'cpuTemperature',
  'gpuTemperature',
  'gpuHotspot',
] as const;

export type SeriesKey = (typeof SERIES_KEYS)[number];

export type SeriesPoint = Record<SeriesKey, number | null>;

/** Columnar history: one entry per sample in every list, oldest first. */
export interface LiveSeries {
  times: number[];
  values: Record<SeriesKey, (number | null)[]>;
}

/** Longest history kept in the browser (the Agent keeps the same). */
export const MAX_HISTORY_MS = 10 * 60 * 1000;

/** A pause longer than this between samples is drawn as a gap, not a line. */
export const GAP_MS = 5000;

export function emptySeries(): LiveSeries {
  return { times: [], values: Object.fromEntries(SERIES_KEYS.map((k) => [k, []])) as unknown as LiveSeries['values'] };
}

function max(values: (number | null | undefined)[]): number | null {
  const present = values.filter((v): v is number => v != null && Number.isFinite(v));
  return present.length === 0 ? null : Math.max(...present);
}

function sum(values: (number | null | undefined)[]): number | null {
  const present = values.filter((v): v is number => v != null && Number.isFinite(v));
  return present.length === 0 ? null : present.reduce((a, b) => a + b, 0);
}

/** The headline values of one live snapshot. */
export function pointFromSnapshot(live: LiveSnapshot): SeriesPoint {
  const up = live.networks.filter((n) => n.up);
  return {
    cpu: live.cpu?.totalPercent.value ?? null,
    memory: live.memory?.usedPercent.value ?? null,
    gpu: max(live.gpus.map((g) => g.utilizationPercent.value)),
    vram: max(
      live.gpus.map((g) =>
        g.vramUsedBytes.value != null && g.vramTotalBytes.value ? (100 * g.vramUsedBytes.value) / g.vramTotalBytes.value : null,
      ),
    ),
    disk: max(live.disks.map((d) => d.activePercent.value)),
    netReceive: sum(up.map((n) => n.receiveBytesPerSec.value)),
    netSend: sum(up.map((n) => n.sendBytesPerSec.value)),
    cpuTemperature: live.cpu?.packageTemperatureC.value ?? null,
    gpuTemperature: max(live.gpus.map((g) => g.temperatureC.value)),
    gpuHotspot: max(live.gpus.map((g) => g.hotspotC.value)),
  };
}

/** History from the Agent's trend. */
export function seriesFromTrend(trend: LiveTrend): LiveSeries {
  return {
    times: [...trend.times],
    values: {
      cpu: [...trend.cpu],
      memory: [...trend.memory],
      gpu: [...trend.gpu],
      vram: [...trend.vram],
      disk: [...trend.disk],
      netReceive: [...trend.netReceive],
      netSend: [...trend.netSend],
      cpuTemperature: [...trend.cpuTemperature],
      gpuTemperature: [...trend.gpuTemperature],
      gpuHotspot: [...trend.gpuHotspot],
    },
  };
}

/** Appends a sample (ignored unless newer than the last) and drops what is older than the history limit. */
export function appendPoint(series: LiveSeries, time: number, point: SeriesPoint): LiveSeries {
  const last = series.times.at(-1);
  if (last != null && time <= last) return series;

  let start = 0;
  while (start < series.times.length && (series.times[start] ?? Number.POSITIVE_INFINITY) < time - MAX_HISTORY_MS) start++;

  const values = {} as LiveSeries['values'];
  for (const key of SERIES_KEYS) {
    values[key] = [...series.values[key].slice(start), point[key]];
  }
  return { times: [...series.times.slice(start), time], values };
}

/** Merges an Agent trend under what the browser already has (the browser's samples win where they overlap). */
export function mergeTrend(series: LiveSeries, trend: LiveSeries): LiveSeries {
  const first = series.times[0];
  if (first === undefined) return trend;
  const older = trend.times.findIndex((t) => t >= first);
  const cut = older === -1 ? trend.times.length : older;
  const values = {} as LiveSeries['values'];
  for (const key of SERIES_KEYS) values[key] = [...trend.values[key].slice(0, cut), ...series.values[key]];
  return { times: [...trend.times.slice(0, cut), ...series.times], values };
}

/** [time, value] pairs for a chart, with a null inserted at gaps so the line breaks instead of bridging them. */
export function chartData(series: LiveSeries, key: SeriesKey): [number, number | null][] {
  const data: [number, number | null][] = [];
  const values = series.values[key];
  let previous: number | undefined;
  series.times.forEach((time, i) => {
    if (previous !== undefined && time - previous > GAP_MS) data.push([previous + 1000, null]);
    data.push([time, values[i] ?? null]);
    previous = time;
  });
  return data;
}

/**
 * The same points, each named by its time. A chart that animates its updates matches points by name: named by time,
 * a point keeps its value and only slides as the window moves (matched by position, every point would morph into its
 * neighbour's value each second, so the older part of the line seemed to change).
 */
export function keyedChartData(series: LiveSeries, key: SeriesKey): { name: string; value: [number, number | null] }[] {
  return chartData(series, key).map(([time, value]) => ({ name: String(time), value: [time, value] }));
}

/** The newest value of a series, if any. */
export function latest(series: LiveSeries, key: SeriesKey): number | null {
  const values = series.values[key];
  return values.length === 0 ? null : (values[values.length - 1] ?? null);
}
