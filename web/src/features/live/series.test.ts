import { describe, expect, it } from 'vitest';
import { liveSnapshot, metric } from '@/test/fixtures';
import { appendPoint, chartData, emptySeries, GAP_MS, latest, MAX_HISTORY_MS, mergeTrend, pointFromSnapshot, seriesFromTrend } from './series';
import type { LiveTrend } from '@/lib/api/types';

const T0 = Date.parse('2026-10-06T12:00:00Z');

function trend(times: number[], cpu: (number | null)[]): LiveTrend {
  const none = times.map(() => null);
  return { times, cpu, memory: none, gpu: none, vram: none, disk: none, netReceive: none, netSend: none, cpuTemperature: none, gpuTemperature: none, gpuHotspot: none };
}

describe('live series', () => {
  it('derives the headline values the same way as the Agent', () => {
    const live = liveSnapshot({
      networks: [
        { name: 'a', kind: 'Ethernet', up: true, receiveBytesPerSec: metric(100), sendBytesPerSec: metric(10), linkBitsPerSec: metric(1e9), wifiSignalPercent: metric(null) },
        { name: 'b', kind: 'Wifi', up: false, receiveBytesPerSec: metric(999), sendBytesPerSec: metric(999), linkBitsPerSec: metric(1e9), wifiSignalPercent: metric(null) },
      ],
    });

    const point = pointFromSnapshot(live);

    expect(point.cpu).toBe(37.5);
    expect(point.vram).toBeCloseTo((100 * 10) / 24);
    expect(point.netReceive).toBe(100); // only connected adapters
    expect(point.cpuTemperature).toBeNull();
    expect(point.gpuHotspot).toBe(84);
  });

  it('appends only newer samples and keeps ten minutes', () => {
    let series = emptySeries();
    for (let s = 0; s <= 700; s++) series = appendPoint(series, T0 + s * 1000, pointFromSnapshot(liveSnapshot()));
    const unchanged = appendPoint(series, T0, pointFromSnapshot(liveSnapshot()));

    expect(unchanged).toBe(series);
    expect(series.times.at(-1)! - series.times[0]!).toBeLessThanOrEqual(MAX_HISTORY_MS);
    expect(latest(series, 'cpu')).toBe(37.5);
  });

  it('fills older history from the Agent trend without duplicating samples', () => {
    let series = emptySeries();
    series = appendPoint(series, T0 + 5000, { ...pointFromSnapshot(liveSnapshot()), cpu: 99 });

    const merged = mergeTrend(series, seriesFromTrend(trend([T0, T0 + 1000, T0 + 5000], [1, 2, 3])));

    expect(merged.times).toEqual([T0, T0 + 1000, T0 + 5000]);
    expect(merged.values.cpu).toEqual([1, 2, 99]); // the browser's own sample wins
  });

  it('breaks the line across gaps instead of bridging them', () => {
    const series = seriesFromTrend(trend([T0, T0 + 1000, T0 + 1000 + GAP_MS + 1], [10, 20, 30]));

    expect(chartData(series, 'cpu')).toEqual([
      [T0, 10],
      [T0 + 1000, 20],
      [T0 + 2000, null],
      [T0 + 1000 + GAP_MS + 1, 30],
    ]);
  });
});

describe('keyed chart data', () => {
  it('names every point by its time so an animated update matches the same sample', async () => {
    const { keyedChartData, appendPoint, emptySeries } = await import('./series');
    const point = (v: number) => ({ cpu: v, memory: null, gpu: null, vram: null, disk: null, netReceive: null, netSend: null, cpuTemperature: null, gpuTemperature: null, gpuHotspot: null });
    let s = emptySeries();
    s = appendPoint(s, 1000, point(10));
    s = appendPoint(s, 2000, point(20));
    const before = keyedChartData(s, 'cpu');
    s = appendPoint(s, 3000, point(30));
    const after = keyedChartData(s, 'cpu');
    // The sample at 2000 is the same item before and after (same name, same value).
    expect(after.find((p) => p.name === '2000')).toEqual(before.find((p) => p.name === '2000'));
    expect(after.map((p) => p.name)).toEqual(['1000', '2000', '3000']);
  });
});
