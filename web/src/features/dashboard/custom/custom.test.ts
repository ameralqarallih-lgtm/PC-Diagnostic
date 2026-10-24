import { describe, expect, it } from 'vitest';
import type { SeriesInfo } from '@/lib/api/types';
import { liveSnapshot, metric } from '@/test/fixtures';
import { defaultCustom, levelFor, readCustom, writeCustom } from './model';
import { buildSources, flattenLive, livePoints, recordLive, resetLive, sourceFromSeries } from './sources';

const NOW = Date.parse('2026-10-08T12:00:00Z');

describe('custom widget settings', () => {
  it('are stored as plain values and read back with defaults for what is missing', () => {
    const config = { ...defaultCustom('line'), sources: ['cpu', 'sensor:lhm/gpu-nvidia/0/fan/1'], labels: ['', 'Fan'], warning: 80, window: 30 };
    const settings = writeCustom(config, '#34d399');

    expect(settings).toMatchObject({ design: 'line', sources: ['cpu', 'sensor:lhm/gpu-nvidia/0/fan/1'], labels: ['', 'Fan'], window: '30', warning: 80, accent: '#34d399' });
    expect(settings).not.toHaveProperty('colors'); // nothing differs from the palette
    expect(settings).not.toHaveProperty('aggregate'); // a chart shows values over time
    expect(Object.values(settings).every((v) => typeof v !== 'object' || Array.isArray(v))).toBe(true);

    const read = readCustom(settings);
    expect(read).toMatchObject({ design: 'line', sources: config.sources, labels: ['', 'Fan'], window: 30, warning: 80, critical: null, direction: 'up' });
    expect(readCustom({ design: 'nonsense', window: '7', count: 999 })).toMatchObject({ design: 'value', window: 5, count: 30 });
  });

  it('judge limits in the direction that is worse', () => {
    expect(levelFor(91, { warning: 75, critical: 90, direction: 'up' })).toBe('critical');
    expect(levelFor(80, { warning: 75, critical: 90, direction: 'up' })).toBe('warning');
    expect(levelFor(900, { warning: 800, critical: 500, direction: 'down' })).toBe('normal');
    expect(levelFor(450, { warning: 800, critical: 500, direction: 'down' })).toBe('critical');
    expect(levelFor(null, { warning: 1, critical: 2, direction: 'up' })).toBe('unavailable');
  });
});

describe('value sources', () => {
  const series = (id: string, extra: Partial<SeriesInfo> = {}): SeriesInfo => ({ id, label: id, unit: '%', group: 'GPU', device: null, longTerm: false, ...extra });

  it('keep device values attached to the device, and know which are live', () => {
    const gpu = sourceFromSeries(series('gpu.3.temp', { device: 'NVIDIA GeForce RTX 4090', key: 'gpu-10de-2684-467519da', lastSeen: NOW, unit: '°C' }), NOW)!;
    expect(gpu).toMatchObject({ id: 'gpu:gpu-10de-2684-467519da:temp', recordedId: 'gpu.3.temp', liveId: 'gpu:gpu-10de-2684-467519da:temp', stale: false, group: 'Graphics · NVIDIA GeForce RTX 4090' });

    const fan = sourceFromSeries(series('gpu.3.fan', { device: 'NVIDIA GeForce RTX 4090', key: 'gpu-10de-2684-467519da' }), NOW)!;
    expect(fan.liveId).toBeNull(); // recorded only

    const disk = sourceFromSeries(series('disk.4.active', { device: 'Samsung SSD 990 PRO', key: 'disk-bfac5402cfa6' }), NOW)!;
    expect(disk).toMatchObject({ id: 'disk:disk-bfac5402cfa6:active', liveId: 'disk:@Samsung SSD 990 PRO:active' });

    const ghost = sourceFromSeries(series('gpu.9.util', { device: 'Old GPU', key: 'gpu-old', lastSeen: NOW - 3 * 24 * 3600_000 }), NOW)!;
    expect(ghost.stale).toBe(true);

    const sensor = sourceFromSeries(series('sensor.12', { label: 'NZXT Kraken X3 / Liquid', unit: '°C', group: 'Temperature sensors', key: 'lhm/usbhid/1E71/2007/0/temperature/0' }), NOW)!;
    expect(sensor).toMatchObject({ id: 'sensor:lhm/usbhid/1E71/2007/0/temperature/0', recordedId: 'sensor.12', liveId: null });

    expect(sourceFromSeries(series('cpu.temp', { group: 'CPU', unit: '°C' }), NOW)).toMatchObject({ id: 'cpu.temp', liveId: 'cpu.temp' });
  });

  it('include live values and live devices the recording has not seen yet', () => {
    const live = liveSnapshot();
    const sources = buildSources([], live, NOW);
    const ids = sources.map((s) => s.id);
    expect(ids).toEqual(expect.arrayContaining(['cpu', 'memory.used', 'processes.count', 'net.rx']));
    expect(ids).toContain(`gpu:${live.gpus[0]!.key}:util`);
    expect(sources.every((s) => s.recordedId == null)).toBe(true);
  });

  it('read every value of a live snapshot and keep ten minutes of them', () => {
    resetLive();
    const live = liveSnapshot({ timestamp: new Date(NOW).toISOString() });
    const flat = flattenLive(live);
    expect(flat.get('cpu')).toEqual({ value: 37.5, reason: null });
    expect(flat.get('cpu.temp')).toEqual({ value: null, reason: 'DriverMissing' });

    recordLive(live);
    recordLive(live); // the same sample again is ignored
    recordLive(liveSnapshot({ timestamp: new Date(NOW + 1000).toISOString(), cpu: { ...live.cpu!, totalPercent: metric(50) } }));
    recordLive(liveSnapshot({ timestamp: new Date(NOW + 11 * 60_000).toISOString() }));
    expect(livePoints('cpu', NOW - 60_000, NOW + 2000)).toEqual([]); // older than ten minutes before the newest
    expect(livePoints('cpu', 0, NOW + 12 * 60_000)).toHaveLength(1);
  });
});
